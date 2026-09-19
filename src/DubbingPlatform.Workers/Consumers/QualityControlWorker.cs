using System.Globalization;
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
/// Project-scoped (single unit) quality control on <c>ai.provider</c> (the
/// workload queue for QualityControl per <c>WorkQueueRouter</c>; the task text
/// names queue <c>media.render</c> and scope <c>Run</c>, but
/// <c>StageGraph</c> scopes QualityControl as <c>Project</c> with
/// <c>DispatchSingleWorkAsync</c> emitting <c>(Project, projectId D)</c> and
/// the router maps it to <c>ai.provider</c> — code truth wins, as in Tasks
/// 022/025/030/031 — so this worker strictly requires
/// <c>ScopeType.Project</c> and filters to <c>QualityControl</c> before
/// claiming; no <c>MassTransitConfig</c> change is needed because extra
/// consumers already attach to <c>ai.provider</c>). Claims via
/// <see cref="BaseConsumer{TMessage}"/> (at-least-once, lease fenced),
/// delegates to <see cref="QualityControlService"/> (segment, project, and
/// signal checks with one <c>QualityResult</c> per failure/warning, a
/// <c>QcReport</c> artifact, and a single project-level review for
/// blocked/manual verdicts), then publishes one event: <c>StageCompleted</c>
/// with the report artifact when the verdict allows the render (pass or
/// warnings), <c>StageReviewRequired</c> for manual verdicts and for blocked
/// verdicts under the default <c>Qc:BlockFailsRun=false</c> (block render,
/// create review, the saga moves the run to <c>ManualReviewRequired</c>),
/// a retryable <c>StageFailed</c> for stale-metadata retries (the saga
/// redispatches), or a permanent <c>StageFailed</c> for blocked verdicts when
/// <c>Qc:BlockFailsRun=true</c>. Barrier accounting stays saga-owned. Never
/// logs text, audio, or secrets: only ids, counts, verdicts, and levels.
/// </summary>
public sealed class QualityControlWorker : BaseConsumer<StageWorkRequested>
{
    private readonly IStageExecutionContextFactory _contextFactory;
    private readonly IOptions<RetryOptions> _retryOptions;
    private readonly QualityControlService _qc;
    private readonly QcOptions _options;
    private readonly ILogger<QualityControlWorker> _logger;

    public QualityControlWorker(
        IStageExecutionContextFactory contextFactory,
        IOptions<RetryOptions> retryOptions,
        QualityControlService qc,
        IOptions<QcOptions> options,
        ILogger<QualityControlWorker> logger,
        IDeferredSender? deferredSender = null)
        : base(contextFactory, retryOptions, deferredSender)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        ArgumentNullException.ThrowIfNull(retryOptions);
        ArgumentNullException.ThrowIfNull(qc);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _contextFactory = contextFactory;
        _retryOptions = retryOptions;
        _qc = qc;
        _options = options.Value;
        _logger = logger;
    }

    protected override bool ShouldProcess(StageWorkRequested message)
    {
        var stage = message.StageTypeRequired ?? message.StageType;
        return string.Equals(stage?.Trim(), nameof(StageType.QualityControl), StringComparison.Ordinal);
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
            if (execution.StageType != StageType.QualityControl)
            {
                throw new DomainException($"Stage execution '{execution.Id}' is '{execution.StageType}', not QualityControl.");
            }

            if (execution.ScopeType != ScopeType.Project)
            {
                throw new DomainException($"Stage execution '{execution.Id}' is '{execution.ScopeType}', not Project, for QualityControl.");
            }

            await EnsureLeaseRunningAsync(execution, cancellationToken).ConfigureAwait(false);

            var outcome = await _qc.RunAsync(
                execution.TenantId, execution.ProjectId, execution.ProcessingRunId,
                cancellationToken).ConfigureAwait(false);

            await EnsureLeaseRunningAsync(execution, cancellationToken).ConfigureAwait(false);

            var stages = new StageExecutionService(_contextFactory, _retryOptions);
            switch (outcome.Verdict)
            {
                case QualityStatus.Pass:
                case QualityStatus.PassWithWarnings:
                    await stages.CompleteAsync(
                        execution.TenantId, execution.Id,
                        execution.LeaseOwner, execution.LeaseToken,
                        [outcome.ReportArtifactId.ToString("N")],
                        cancellationToken).ConfigureAwait(false);
                    await PublishCompletedAsync(context, execution, outcome.ReportArtifactId, cancellationToken).ConfigureAwait(false);
                    break;

                case QualityStatus.ManualReviewRequired:
                    await PublishReviewAsync(
                        context, stages, execution, outcome, QualityControlService.ReviewReasonReview,
                        cancellationToken).ConfigureAwait(false);
                    break;

                case QualityStatus.Blocked when !_options.BlockFailsRun:
                    await PublishReviewAsync(
                        context, stages, execution, outcome, QualityControlService.ReviewReasonBlocked,
                        cancellationToken).ConfigureAwait(false);
                    break;

                case QualityStatus.Blocked:
                    await PublishBlockedFailureAsync(
                        context, stages, execution, outcome,
                        cancellationToken).ConfigureAwait(false);
                    break;

                default:
                    await PublishRetryableFailureAsync(
                        context, stages, execution, outcome,
                        cancellationToken).ConfigureAwait(false);
                    break;
            }

            _logger.LogInformation(
                "QC for run {RunId}: verdict {Verdict} ({Blocked} blocked, {Review} review, {Retry} retry, {Warnings} warnings).",
                execution.ProcessingRunId, outcome.Verdict,
                outcome.BlockedCount.ToString(CultureInfo.InvariantCulture),
                outcome.ReviewCount.ToString(CultureInfo.InvariantCulture),
                outcome.RetryCount.ToString(CultureInfo.InvariantCulture),
                outcome.WarningCount.ToString(CultureInfo.InvariantCulture));
        }
        catch (LeaseLostException)
        {
            throw;
        }
#pragma warning disable CA1031 // Worker poison contract: permanent failures go via TryFailAsync; transient rethrows.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogWarning(
                "Quality control failed for run {RunId}: {Error}.",
                execution.ProcessingRunId, ex.Message);
            if (!await StageWorkerFailure.TryFailAsync(
                _contextFactory, _retryOptions, context, execution, ex,
                nameof(StageType.QualityControl), cancellationToken).ConfigureAwait(false))
            {
                throw;
            }
        }
    }

    private async Task PublishCompletedAsync(
        ConsumeContext<StageWorkRequested> context,
        StageExecution execution,
        Guid reportArtifactId,
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
            nameof(StageType.QualityControl),
            [reportArtifactId.ToString("N")]), cancellationToken).ConfigureAwait(false);
    }

    private async Task PublishReviewAsync(
        ConsumeContext<StageWorkRequested> context,
        StageExecutionService stages,
        StageExecution execution,
        QcOutcome outcome,
        string reason,
        CancellationToken cancellationToken)
    {
        if (outcome.ReviewItemId is null)
        {
            throw new DomainException($"QC for run '{execution.ProcessingRunId:D}' requires review but produced no review item.");
        }

        await stages.MarkReviewRequiredAsync(
            execution.TenantId, execution.Id,
            execution.LeaseOwner, execution.LeaseToken,
            cancellationToken).ConfigureAwait(false);

        var inbound = context.Message;
        var correlationId = string.IsNullOrWhiteSpace(inbound.CorrelationId)
            ? context.CorrelationId?.ToString() ?? Guid.NewGuid().ToString("N")
            : inbound.CorrelationId;
        await context.Publish(new StageReviewRequired(
            Guid.NewGuid(), correlationId,
            execution.TenantId, execution.ProjectId, execution.ProcessingRunId,
            execution.Id, execution.StageType.ToString(), execution.ScopeType.ToString(), execution.ScopeId,
            execution.SegmentId, MessageVersionPolicy.CurrentVersion, DateTimeOffset.UtcNow, execution.Attempt,
            execution.InputHash, execution.ConfigurationHash, execution.ExecutionSnapshotHash,
            nameof(StageType.QualityControl),
            outcome.ReviewItemId.Value.ToString("N")), cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "QC for run {RunId} routed to review ({Reason}, {Findings} findings).",
            execution.ProcessingRunId, reason,
            outcome.Findings.Count.ToString(CultureInfo.InvariantCulture));
    }

    private async Task PublishBlockedFailureAsync(
        ConsumeContext<StageWorkRequested> context,
        StageExecutionService stages,
        StageExecution execution,
        QcOutcome outcome,
        CancellationToken cancellationToken)
    {
        var first = outcome.Findings.FirstOrDefault(f => f.Status == QualityStatus.Blocked);
        var code = first?.Code ?? ErrorCodes.QcBlocked;
        var message = first is null
            ? "Quality control blocked the render."
            : string.Concat("Quality control blocked the render: ", first.Code, " (", outcome.BlockedCount.ToString(CultureInfo.InvariantCulture), " blocking).");

        await stages.FailAsync(
            execution.TenantId, execution.Id,
            execution.LeaseOwner, execution.LeaseToken,
            ErrorCodes.QcBlocked, Truncate(message), cancellationToken).ConfigureAwait(false);

        var inbound = context.Message;
        var correlationId = string.IsNullOrWhiteSpace(inbound.CorrelationId)
            ? context.CorrelationId?.ToString() ?? Guid.NewGuid().ToString("N")
            : inbound.CorrelationId;
        await context.Publish(new StageFailed(
            Guid.NewGuid(), correlationId,
            execution.TenantId, execution.ProjectId, execution.ProcessingRunId,
            execution.Id, execution.StageType.ToString(), execution.ScopeType.ToString(), execution.ScopeId,
            execution.SegmentId, MessageVersionPolicy.CurrentVersion, DateTimeOffset.UtcNow, execution.Attempt,
            execution.InputHash, execution.ConfigurationHash, execution.ExecutionSnapshotHash,
            nameof(StageType.QualityControl), code, Truncate(message), false), cancellationToken).ConfigureAwait(false);
    }

    private async Task PublishRetryableFailureAsync(
        ConsumeContext<StageWorkRequested> context,
        StageExecutionService stages,
        StageExecution execution,
        QcOutcome outcome,
        CancellationToken cancellationToken)
    {
        var first = outcome.Findings.FirstOrDefault(f => f.Status == QualityStatus.RetryRequired);
        var code = first?.Code ?? QualityControlService.CodeStaleMetadata;
        var message = first?.Message ?? "Quality control requires a retry after upstream attempts settle.";

        await stages.FailAsync(
            execution.TenantId, execution.Id,
            execution.LeaseOwner, execution.LeaseToken,
            code, Truncate(message), cancellationToken).ConfigureAwait(false);

        var inbound = context.Message;
        var correlationId = string.IsNullOrWhiteSpace(inbound.CorrelationId)
            ? context.CorrelationId?.ToString() ?? Guid.NewGuid().ToString("N")
            : inbound.CorrelationId;
        await context.Publish(new StageFailed(
            Guid.NewGuid(), correlationId,
            execution.TenantId, execution.ProjectId, execution.ProcessingRunId,
            execution.Id, execution.StageType.ToString(), execution.ScopeType.ToString(), execution.ScopeId,
            execution.SegmentId, MessageVersionPolicy.CurrentVersion, DateTimeOffset.UtcNow, execution.Attempt,
            execution.InputHash, execution.ConfigurationHash, execution.ExecutionSnapshotHash,
            nameof(StageType.QualityControl), code, Truncate(message), true), cancellationToken).ConfigureAwait(false);
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

    private static string Truncate(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return "Quality control failed.";
        }

        var trimmed = message.Trim();
        return trimmed.Length <= 500 ? trimmed : trimmed.Substring(0, 500);
    }
}
