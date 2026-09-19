using DubbingPlatform.Application.Errors;
using DubbingPlatform.Application.Exceptions;
using DubbingPlatform.Application.MultiTenancy;
using DubbingPlatform.Application.Options;
using DubbingPlatform.Application.Services;
using DubbingPlatform.Contracts.Messages;
using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Domain.Enums;
using DubbingPlatform.Domain.Exceptions;
using DubbingPlatform.Infrastructure.Messaging;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DubbingPlatform.Workers.Consumers;

/// <summary>
/// Window-scoped context building on <c>ai.provider</c> (the workload queue
/// for ContextBuild per <c>WorkQueueRouter</c>; the task text names
/// <c>control.orchestration</c>, but routing is workload-class based and
/// ContextBuild is AI-provider work alongside Diarization and Transcription,
/// while <c>control.orchestration</c> is pub/sub for saga control messages,
/// never point-to-point work). Claims via
/// <see cref="BaseConsumer{TMessage}"/> (at-least-once, lease fenced),
/// ignores other stages sharing the queue before claiming, resolves its
/// window via <see cref="ContextBuilderService.TryGetWindowAsync"/>
/// (<c>ScopeId</c> is the window id in <c>N</c>/<c>D</c> form as dispatched by
/// <c>WorkDispatcher.DispatchWindowWorkAsync</c>; a bare window sequence
/// number is also accepted), builds all windows idempotently when its own is
/// missing, lease-checks before committing, completes with the reusable
/// window artifact id, and publishes <c>StageCompleted</c>. Barrier accounting
/// stays saga-owned (it records on these events); Translation dispatch waits
/// on the <c>ContextBuild</c> barrier via the existing
/// <c>BarrierService.ArePrerequisitesCompleteAsync</c> gate. Transport errors
/// rethrow into transport retry via <see cref="StageWorkerFailure"/>;
/// permanent failures fail the execution lease-fenced. Never logs context
/// text: only ids, counts, and hashes.
/// </summary>
public sealed class ContextBuilderWorker : BaseConsumer<StageWorkRequested>
{
    private readonly IStageExecutionContextFactory _contextFactory;
    private readonly IOptions<RetryOptions> _retryOptions;
    private readonly ContextBuilderService _contexts;
    private readonly ILogger<ContextBuilderWorker> _logger;

    public ContextBuilderWorker(
        IStageExecutionContextFactory contextFactory,
        IOptions<RetryOptions> retryOptions,
        ContextBuilderService contexts,
        ILogger<ContextBuilderWorker> logger,
        IDeferredSender? deferredSender = null)
        : base(contextFactory, retryOptions, deferredSender)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        ArgumentNullException.ThrowIfNull(retryOptions);
        ArgumentNullException.ThrowIfNull(contexts);
        ArgumentNullException.ThrowIfNull(logger);
        _contextFactory = contextFactory;
        _retryOptions = retryOptions;
        _contexts = contexts;
        _logger = logger;
    }

    protected override bool ShouldProcess(StageWorkRequested message)
    {
        var stage = message.StageTypeRequired ?? message.StageType;
        return string.Equals(stage?.Trim(), nameof(StageType.ContextBuild), StringComparison.Ordinal);
    }

    protected override async Task HandleAsync(
        ConsumeContext<StageWorkRequested> context,
        StageExecution? execution,
        CancellationToken cancellationToken)
    {
        if (execution is null)
        {
            return;
        }

        if (execution.Status is StageStatus.Completed or StageStatus.Skipped or StageStatus.ManualReviewRequired or StageStatus.Failed)
        {
            return;
        }

        try
        {
            if (execution.StageType != StageType.ContextBuild)
            {
                throw new DomainException($"Stage execution '{execution.Id}' is '{execution.StageType}', not ContextBuild.");
            }

            if (execution.ScopeType != ScopeType.Window
                || string.IsNullOrWhiteSpace(execution.ScopeId))
            {
                throw new DomainException($"Stage execution '{execution.Id}' has no window scope for ContextBuild.");
            }

            var resolved = await _contexts.TryGetWindowAsync(
                execution.TenantId, execution.ProcessingRunId,
                execution.ScopeId, cancellationToken).ConfigureAwait(false);
            if (resolved is null)
            {
                await _contexts.BuildWindowsAsync(
                    execution.TenantId, execution.ProjectId, execution.ProcessingRunId,
                    cancellationToken).ConfigureAwait(false);
                resolved = await _contexts.TryGetWindowAsync(
                    execution.TenantId, execution.ProcessingRunId,
                    execution.ScopeId, cancellationToken).ConfigureAwait(false);
            }

            if (resolved is null)
            {
                throw new ErrorCodeException(
                    ErrorCodes.PipelineInvariantViolation,
                    $"Context window '{execution.ScopeId}' was not found for run '{execution.ProcessingRunId:D}'.");
            }

            await EnsureLeaseRunningAsync(execution, cancellationToken).ConfigureAwait(false);

            var stages = new StageExecutionService(_contextFactory, _retryOptions);
            await stages.CompleteAsync(
                execution.TenantId, execution.Id,
                execution.LeaseOwner, execution.LeaseToken,
                [resolved.Value.ArtifactId.ToString("N")],
                cancellationToken).ConfigureAwait(false);

            _logger.LogInformation(
                "Context window {Sequence} built for run {RunId} (artifact {ArtifactId}).",
                resolved.Value.Window.Sequence, execution.ProcessingRunId, resolved.Value.ArtifactId);

            await PublishCompletedAsync(context, execution, resolved.Value.ArtifactId, cancellationToken).ConfigureAwait(false);
        }
        catch (LeaseLostException)
        {
            throw;
        }
#pragma warning disable CA1031 // Worker poison contract: permanent failures are recorded via TryFailAsync; transient rethrows.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogWarning(
                "Context build failed for run {RunId} scope {ScopeId}: {Error}.",
                execution.ProcessingRunId, execution.ScopeId, ex.Message);
            if (!await StageWorkerFailure.TryFailAsync(
                _contextFactory, _retryOptions, context, execution, ex,
                nameof(StageType.ContextBuild), cancellationToken).ConfigureAwait(false))
            {
                throw;
            }
        }
    }

    private async Task EnsureLeaseRunningAsync(StageExecution execution, CancellationToken cancellationToken)
    {
        using (TenantContext.BeginScope(execution.TenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            var current = await db.Set<StageExecution>()
                .AsNoTracking()
                .FirstOrDefaultAsync(e => e.Id == execution.Id, cancellationToken).ConfigureAwait(false);
            if (current is null
                || current.Status != StageStatus.Running
                || !string.Equals(current.LeaseOwner, execution.LeaseOwner, StringComparison.Ordinal)
                || !string.Equals(current.LeaseToken, execution.LeaseToken, StringComparison.Ordinal))
            {
                throw new LeaseLostException($"Lease lost for stage execution '{execution.Id}'.");
            }

            var run = await db.Set<ProcessingRun>()
                .AsNoTracking()
                .FirstOrDefaultAsync(r => r.Id == execution.ProcessingRunId, cancellationToken).ConfigureAwait(false);
            if (run is not null
                && run.Status is ProcessingRunStatus.Cancelling or ProcessingRunStatus.Cancelled)
            {
                throw new LeaseLostException($"Processing run '{run.Id}' is '{run.Status}'; aborting before commit.");
            }
        }
    }

    private async Task PublishCompletedAsync(
        ConsumeContext<StageWorkRequested> context,
        StageExecution execution,
        Guid artifactId,
        CancellationToken cancellationToken)
    {
        var inbound = context.Message;
        var correlationId = string.IsNullOrWhiteSpace(inbound.CorrelationId)
            ? context.CorrelationId?.ToString() ?? Guid.NewGuid().ToString("N")
            : inbound.CorrelationId;
        await context.Publish(new StageCompleted(
            Guid.NewGuid(), correlationId,
            execution.TenantId, execution.ProjectId, execution.ProcessingRunId,
            execution.Id, execution.StageType.ToString(), execution.ScopeType.ToString(), execution.ScopeId,
            execution.SegmentId, MessageVersionPolicy.CurrentVersion, DateTimeOffset.UtcNow, execution.Attempt,
            execution.InputHash, execution.ConfigurationHash, execution.ExecutionSnapshotHash,
            nameof(StageType.ContextBuild),
            [artifactId.ToString("N")]), cancellationToken).ConfigureAwait(false);
    }
}
