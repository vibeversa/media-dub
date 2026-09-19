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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DubbingPlatform.Workers.Consumers;

/// <summary>
/// Segment-scoped timing optimization on <c>ai.provider</c> (the workload
/// queue for TimingOptimization per <c>WorkQueueRouter</c>). Claims via
/// <see cref="BaseConsumer{TMessage}"/> (at-least-once, lease fenced),
/// ignores other stages sharing the queue before claiming, delegates to
/// <see cref="TimingOptimizationService"/> (bounded alternate/prosody/rewrite/
/// stretch search with deterministic sync scoring, hard rate/stretch caps,
/// SyncResult persistence, translation re-selection, stretch-preview audit
/// with the stage commit inside the service), then publishes one event per
/// segment: <c>StageCompleted</c> with the chosen audio artifact or
/// <c>StageReviewRequired</c> for exhausted out-of-tolerance timing (the run
/// moves to review, never fails for one mistimed segment). Retryable provider
/// codes publish a retryable <c>StageFailed</c> so the saga redispatches the
/// next attempt (rate-limit codes are deferred by the saga). Transport errors
/// (HTTP I/O, timeouts, sockets, I/O) rethrow into transport retry via
/// <see cref="StageWorkerFailure"/>; permanent failures fail the execution
/// lease-fenced. Barrier accounting stays saga-owned (it records on these
/// events); duplicate deliveries return the stored execution outcome without
/// new FFmpeg work. Never logs text, SSML, audio, or secrets: only ids,
/// durations, rates, factors, and scores.
/// </summary>
public sealed class TimingOptimizationWorker : BaseConsumer<StageWorkRequested>
{
    private static readonly HashSet<string> RetryableProviderCodes = new(StringComparer.Ordinal)
    {
        ErrorCodes.RateLimited,
        ErrorCodes.ProviderRateLimited,
        ErrorCodes.ProviderTimeout,
        ErrorCodes.ProviderQuotaExhausted,
        ErrorCodes.ProviderFailed,
    };

    private readonly IStageExecutionContextFactory _contextFactory;
    private readonly IOptions<RetryOptions> _retryOptions;
    private readonly TimingOptimizationService _timing;
    private readonly ILogger<TimingOptimizationWorker> _logger;

    public TimingOptimizationWorker(
        IStageExecutionContextFactory contextFactory,
        IOptions<RetryOptions> retryOptions,
        TimingOptimizationService timing,
        ILogger<TimingOptimizationWorker> logger,
        IDeferredSender? deferredSender = null)
        : base(contextFactory, retryOptions, deferredSender)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        ArgumentNullException.ThrowIfNull(retryOptions);
        ArgumentNullException.ThrowIfNull(timing);
        ArgumentNullException.ThrowIfNull(logger);
        _contextFactory = contextFactory;
        _retryOptions = retryOptions;
        _timing = timing;
        _logger = logger;
    }

    protected override bool ShouldProcess(StageWorkRequested message)
    {
        var stage = message.StageTypeRequired ?? message.StageType;
        return string.Equals(stage?.Trim(), nameof(StageType.TimingOptimization), StringComparison.Ordinal);
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
            if (execution.StageType != StageType.TimingOptimization)
            {
                throw new DomainException($"Stage execution '{execution.Id}' is '{execution.StageType}', not TimingOptimization.");
            }

            if (execution.ScopeType != ScopeType.Segment
                || execution.SegmentId is null
                || execution.SegmentId.Value == Guid.Empty)
            {
                throw new DomainException($"Stage execution '{execution.Id}' has no segment scope for TimingOptimization.");
            }

            var result = await _timing.OptimizeAsync(
                execution.TenantId, execution.ProjectId, execution.ProcessingRunId,
                execution.SegmentId.Value, execution.Attempt,
                execution.Id, execution.LeaseOwner, execution.LeaseToken,
                cancellationToken).ConfigureAwait(false);

            if (result.NeedsReview)
            {
                if (result.ReviewItemId is null)
                {
                    throw new DomainException($"Timing for segment '{result.SegmentId:D}' requires review but produced no review item.");
                }

                await PublishReviewRequiredAsync(context, execution, result.ReviewItemId.Value, cancellationToken).ConfigureAwait(false);
                return;
            }

            await PublishCompletedAsync(context, execution, result.ChosenAudioArtifactId, cancellationToken).ConfigureAwait(false);
        }
        catch (LeaseLostException)
        {
            throw;
        }
#pragma warning disable CA1031 // Worker poison contract: retryable provider codes publish retryable StageFailed; permanent failures go via TryFailAsync; transient rethrows.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            if (ex is ErrorCodeException coded && RetryableProviderCodes.Contains(coded.ErrorCode))
            {
                _logger.LogInformation(
                    "Timing retryable failure for segment {SegmentId} ({Code}); saga redispatches.",
                    execution.SegmentId, coded.ErrorCode);
                await PublishRetryableFailureAsync(
                    context, execution, coded.ErrorCode, coded.Message, cancellationToken).ConfigureAwait(false);
                return;
            }

            // Never log text/audio: only ids and error summaries.
            _logger.LogWarning(
                "Timing failed for segment {SegmentId}: {Error}.",
                execution.SegmentId, ex.Message);
            if (!await StageWorkerFailure.TryFailAsync(
                _contextFactory, _retryOptions, context, execution, ex,
                nameof(StageType.TimingOptimization), cancellationToken).ConfigureAwait(false))
            {
                throw;
            }
        }
    }

    private async Task PublishRetryableFailureAsync(
        ConsumeContext<StageWorkRequested> context,
        StageExecution execution,
        string errorCode,
        string errorMessage,
        CancellationToken cancellationToken)
    {
        var stages = new StageExecutionService(_contextFactory, _retryOptions);
        await stages.FailAsync(
            execution.TenantId, execution.Id,
            execution.LeaseOwner, execution.LeaseToken,
            errorCode, Truncate(errorMessage), cancellationToken).ConfigureAwait(false);

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
            nameof(StageType.TimingOptimization), errorCode, Truncate(errorMessage), true), cancellationToken).ConfigureAwait(false);
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
            nameof(StageType.TimingOptimization),
            [artifactId.ToString("N")]), cancellationToken).ConfigureAwait(false);
    }

    private async Task PublishReviewRequiredAsync(
        ConsumeContext<StageWorkRequested> context,
        StageExecution execution,
        Guid reviewItemId,
        CancellationToken cancellationToken)
    {
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
            nameof(StageType.TimingOptimization),
            reviewItemId.ToString("N")), cancellationToken).ConfigureAwait(false);
    }

    private static string Truncate(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return "Timing failed.";
        }

        var trimmed = message.Trim();
        return trimmed.Length <= 500 ? trimmed : trimmed.Substring(0, 500);
    }
}
