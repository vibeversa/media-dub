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
/// Run-scoped (project scope, single unit) timeline assembly on
/// <c>media.render</c> (the workload queue for TimelineAssembly per
/// <c>WorkQueueRouter</c>; the task text names
/// <c>control.orchestration</c>, but that endpoint is pub/sub saga control
/// and never carries point-to-point work — rendering-class work owns
/// <c>media.render</c>, so this worker attaches there while the dispatcher
/// sends via the router). Claims via <see cref="BaseConsumer{TMessage}"/>
/// (at-least-once, lease fenced), ignores other stages sharing the queue
/// before claiming, delegates to <see cref="TimelineAssemblyService"/>
/// (source-start placement, silence preserved as gaps, intentional overlaps
/// kept via shared groups, invalid overlaps and overflow rejected before any
/// commit), completes the execution, and publishes <c>StageCompleted</c> with
/// the timeline artifact. Permanent failures (including
/// <c>PIPELINE_INVARIANT_VIOLATION</c> and <c>QC_BLOCKED</c>) fail the
/// execution lease-fenced via <see cref="StageWorkerFailure"/>; transient
/// failures rethrow into transport retry. Barrier accounting stays
/// saga-owned. Never logs audio or secrets: only ids, counts, and durations.
/// </summary>
public sealed class TimelineAssemblerWorker : BaseConsumer<StageWorkRequested>
{
    private readonly IStageExecutionContextFactory _contextFactory;
    private readonly IOptions<RetryOptions> _retryOptions;
    private readonly TimelineAssemblyService _timeline;
    private readonly ILogger<TimelineAssemblerWorker> _logger;

    public TimelineAssemblerWorker(
        IStageExecutionContextFactory contextFactory,
        IOptions<RetryOptions> retryOptions,
        TimelineAssemblyService timeline,
        ILogger<TimelineAssemblerWorker> logger,
        IDeferredSender? deferredSender = null)
        : base(contextFactory, retryOptions, deferredSender)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        ArgumentNullException.ThrowIfNull(retryOptions);
        ArgumentNullException.ThrowIfNull(timeline);
        ArgumentNullException.ThrowIfNull(logger);
        _contextFactory = contextFactory;
        _retryOptions = retryOptions;
        _timeline = timeline;
        _logger = logger;
    }

    protected override bool ShouldProcess(StageWorkRequested message)
    {
        var stage = message.StageTypeRequired ?? message.StageType;
        return string.Equals(stage?.Trim(), nameof(StageType.TimelineAssembly), StringComparison.Ordinal);
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

        if (execution.Status is StageStatus.Completed or StageStatus.Skipped)
        {
            return;
        }

        try
        {
            if (execution.StageType != StageType.TimelineAssembly)
            {
                throw new DomainException($"Stage execution '{execution.Id}' is '{execution.StageType}', not TimelineAssembly.");
            }

            if (execution.ScopeType != ScopeType.Project)
            {
                throw new DomainException($"Stage execution '{execution.Id}' is '{execution.ScopeType}', not Project, for TimelineAssembly.");
            }

            await EnsureLeaseRunningAsync(execution, cancellationToken).ConfigureAwait(false);

            var result = await _timeline.AssembleAsync(
                execution.TenantId,
                execution.ProjectId,
                execution.ProcessingRunId,
                cancellationToken).ConfigureAwait(false);

            await EnsureLeaseRunningAsync(execution, cancellationToken).ConfigureAwait(false);

            var stages = new StageExecutionService(_contextFactory, _retryOptions);
            await stages.CompleteAsync(
                execution.TenantId, execution.Id,
                execution.LeaseOwner, execution.LeaseToken,
                [result.ArtifactId.ToString("N")],
                cancellationToken).ConfigureAwait(false);

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
                nameof(StageType.TimelineAssembly),
                [result.ArtifactId.ToString("N")]), cancellationToken).ConfigureAwait(false);
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
                "Timeline assembly failed for run {RunId}: {Error}.",
                execution.ProcessingRunId, ex.Message);
            if (!await StageWorkerFailure.TryFailAsync(
                _contextFactory, _retryOptions, context, execution, ex,
                nameof(StageType.TimelineAssembly), cancellationToken).ConfigureAwait(false))
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
            var loaded = await db.Set<StageExecution>()
                .AsNoTracking()
                .FirstOrDefaultAsync(e => e.Id == execution.Id, cancellationToken).ConfigureAwait(false);
            if (loaded is null
                || loaded.Status != StageStatus.Running
                || !string.Equals(loaded.LeaseOwner, execution.LeaseOwner, StringComparison.Ordinal)
                || !string.Equals(loaded.LeaseToken, execution.LeaseToken, StringComparison.Ordinal))
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
}
