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
/// Speaker-scoped voice assignment on <c>ai.provider</c> (the workload queue
/// for VoiceAssignment per <c>WorkQueueRouter</c>). Claims via
/// <see cref="BaseConsumer{TMessage}"/> (at-least-once, lease fenced),
/// ignores other stages sharing the queue before claiming, delegates to
/// <see cref="VoiceAssignmentService"/> (stable project-reused voice,
/// override validation, live consent enforcement, cloned-use audit), completes
/// the execution with the assignment id as the output (no media artifact is
/// produced by this stage), and publishes <c>StageCompleted</c>. Barrier
/// accounting stays saga-owned (it records on these events; the dispatcher
/// seeds <c>ExpectedUnits</c> with the speaker count). Permanent failures
/// (unknown override, missing inventory, consent/policy denials) fail the
/// execution lease-fenced via <see cref="StageWorkerFailure"/> (fail-fast per
/// the DAG); transport errors rethrow into transport retry. Never logs voice
/// audio, subject identity, or evidence: only ids, voice ids, and reasons.
/// </summary>
public sealed class VoiceAssignmentWorker : BaseConsumer<StageWorkRequested>
{
    private readonly IStageExecutionContextFactory _contextFactory;
    private readonly IOptions<RetryOptions> _retryOptions;
    private readonly VoiceAssignmentService _assignment;
    private readonly ILogger<VoiceAssignmentWorker> _logger;

    public VoiceAssignmentWorker(
        IStageExecutionContextFactory contextFactory,
        IOptions<RetryOptions> retryOptions,
        VoiceAssignmentService assignment,
        ILogger<VoiceAssignmentWorker> logger,
        IDeferredSender? deferredSender = null)
        : base(contextFactory, retryOptions, deferredSender)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        ArgumentNullException.ThrowIfNull(retryOptions);
        ArgumentNullException.ThrowIfNull(assignment);
        ArgumentNullException.ThrowIfNull(logger);
        _contextFactory = contextFactory;
        _retryOptions = retryOptions;
        _assignment = assignment;
        _logger = logger;
    }

    protected override bool ShouldProcess(StageWorkRequested message)
    {
        var stage = message.StageTypeRequired ?? message.StageType;
        return string.Equals(stage?.Trim(), nameof(StageType.VoiceAssignment), StringComparison.Ordinal);
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
            if (execution.StageType != StageType.VoiceAssignment)
            {
                throw new DomainException($"Stage execution '{execution.Id}' is '{execution.StageType}', not VoiceAssignment.");
            }

            if (execution.ScopeType != ScopeType.Speaker
                || string.IsNullOrWhiteSpace(execution.ScopeId))
            {
                throw new DomainException($"Stage execution '{execution.Id}' has no speaker scope for VoiceAssignment.");
            }

            if (!TryParseSpeakerId(execution.ScopeId, out var speakerId))
            {
                throw new DomainException($"Stage execution '{execution.Id}' has an invalid speaker scope '{execution.ScopeId}'.");
            }

            var result = await _assignment.AssignAsync(
                execution.TenantId, execution.ProjectId, execution.ProcessingRunId,
                speakerId, cancellationToken).ConfigureAwait(false);

            await EnsureLeaseRunningAsync(execution, cancellationToken).ConfigureAwait(false);

            var stages = new StageExecutionService(_contextFactory, _retryOptions);
            await stages.CompleteAsync(
                execution.TenantId, execution.Id,
                execution.LeaseOwner, execution.LeaseToken,
                [result.AssignmentId.ToString("N")],
                cancellationToken).ConfigureAwait(false);

            _logger.LogInformation(
                "Voice assigned for speaker {SpeakerId} in run {RunId}: voice {VoiceId} ({Reason}).",
                speakerId, execution.ProcessingRunId, result.VoiceId, result.Reason);

            await PublishCompletedAsync(context, execution, result.AssignmentId, cancellationToken).ConfigureAwait(false);
        }
        catch (LeaseLostException)
        {
            throw;
        }
#pragma warning disable CA1031 // Worker poison contract: permanent failures are recorded via TryFailAsync; transient rethrows.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            // Never log voice biometrics or consent PII: only ids and error summaries.
            _logger.LogWarning(
                "Voice assignment failed for run {RunId} scope {ScopeId}: {Error}.",
                execution.ProcessingRunId, execution.ScopeId, ex.Message);
            if (!await StageWorkerFailure.TryFailAsync(
                _contextFactory, _retryOptions, context, execution, ex,
                nameof(StageType.VoiceAssignment), cancellationToken).ConfigureAwait(false))
            {
                throw;
            }
        }
    }

    private static bool TryParseSpeakerId(string scopeId, out Guid speakerId)
    {
        var trimmed = scopeId.Trim();
        if (Guid.TryParseExact(trimmed, "N", out speakerId) && speakerId != Guid.Empty)
        {
            return true;
        }

        if (Guid.TryParseExact(trimmed, "D", out speakerId) && speakerId != Guid.Empty)
        {
            return true;
        }

        speakerId = Guid.Empty;
        return false;
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
        Guid assignmentId,
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
            nameof(StageType.VoiceAssignment),
            [assignmentId.ToString("N")]), cancellationToken).ConfigureAwait(false);
    }
}
