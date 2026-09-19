using DubbingPlatform.Application.Errors;
using DubbingPlatform.Application.Exceptions;
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
/// Segment-scoped transcription on <c>ai.provider</c> (the workload queue for
/// Transcription per <c>WorkQueueRouter</c>). Claims via
/// <see cref="BaseConsumer{TMessage}"/> (at-least-once, lease fenced),
/// ignores other stages sharing the queue before claiming, delegates to
/// <see cref="TranscriptionService"/> (versioned transcripts, one fallback
/// attempt, review routing with the stage commit inside the service), then
/// publishes one event per segment: <c>StageCompleted</c> with the winning
/// words artifact, <c>StageReviewRequired</c> for persistent low confidence
/// (the run moves to review, never fails for one weak segment), or a
/// retryable <c>StageFailed</c> for low-confidence retries and
/// rate-limit/timeout/quota/failed provider codes (the saga redispatches the
/// next attempt, deferring rate-limit codes instead of retrying immediately).
/// Transport errors (HTTP I/O, timeouts, sockets, I/O) rethrow into transport
/// retry via <see cref="StageWorkerFailure"/>; permanent failures fail the
/// execution lease-fenced. Barrier accounting stays saga-owned (it records on
/// these events); duplicate deliveries return the stored execution outcome
/// without new provider calls.
/// </summary>
public sealed class TranscriptionWorker : BaseConsumer<StageWorkRequested>
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
    private readonly TranscriptionService _transcription;
    private readonly ILogger<TranscriptionWorker> _logger;

    public TranscriptionWorker(
        IStageExecutionContextFactory contextFactory,
        IOptions<RetryOptions> retryOptions,
        TranscriptionService transcription,
        ILogger<TranscriptionWorker> logger,
        IDeferredSender? deferredSender = null)
        : base(contextFactory, retryOptions, deferredSender)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        ArgumentNullException.ThrowIfNull(retryOptions);
        ArgumentNullException.ThrowIfNull(transcription);
        ArgumentNullException.ThrowIfNull(logger);
        _contextFactory = contextFactory;
        _retryOptions = retryOptions;
        _transcription = transcription;
        _logger = logger;
    }

    protected override bool ShouldProcess(StageWorkRequested message)
    {
        var stage = message.StageTypeRequired ?? message.StageType;
        return string.Equals(stage?.Trim(), nameof(StageType.Transcription), StringComparison.Ordinal);
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
            if (execution.StageType != StageType.Transcription)
            {
                throw new DomainException($"Stage execution '{execution.Id}' is '{execution.StageType}', not Transcription.");
            }

            if (execution.ScopeType != ScopeType.Segment
                || execution.SegmentId is null
                || execution.SegmentId.Value == Guid.Empty)
            {
                throw new DomainException($"Stage execution '{execution.Id}' has no segment scope for Transcription.");
            }

            var result = await _transcription.TranscribeSegmentAsync(
                execution.TenantId, execution.ProjectId, execution.ProcessingRunId,
                execution.SegmentId.Value, execution.Attempt,
                execution.Id, execution.LeaseOwner, execution.LeaseToken,
                cancellationToken).ConfigureAwait(false);

            if (result.NeedsReview)
            {
                if (result.ReviewItemId is null)
                {
                    throw new DomainException($"Transcription for segment '{result.SegmentId:D}' requires review but produced no review item.");
                }

                await PublishReviewRequiredAsync(context, execution, result.ReviewItemId.Value, cancellationToken).ConfigureAwait(false);
                return;
            }

            await PublishCompletedAsync(context, execution, result.WordsArtifactId, cancellationToken).ConfigureAwait(false);
        }
        catch (TranscriptionRetryableException ex)
        {
            _logger.LogInformation(
                "Transcription retry for segment {SegmentId}: {Error}.",
                execution.SegmentId, ex.Message);
            await PublishRetryableFailureAsync(
                context, execution, ErrorCodes.ProviderFailed, ex.Message, cancellationToken).ConfigureAwait(false);
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
                    "Transcription retryable failure for segment {SegmentId} ({Code}); saga redispatches.",
                    execution.SegmentId, coded.ErrorCode);
                await PublishRetryableFailureAsync(
                    context, execution, coded.ErrorCode, coded.Message, cancellationToken).ConfigureAwait(false);
                return;
            }

            // Never log transcript text: only ids and error summaries.
            _logger.LogWarning(
                "Transcription failed for segment {SegmentId}: {Error}.",
                execution.SegmentId, ex.Message);
            if (!await StageWorkerFailure.TryFailAsync(
                _contextFactory, _retryOptions, context, execution, ex,
                nameof(StageType.Transcription), cancellationToken).ConfigureAwait(false))
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
            nameof(StageType.Transcription), errorCode, Truncate(errorMessage), true), cancellationToken).ConfigureAwait(false);
    }

    private async Task PublishCompletedAsync(
        ConsumeContext<StageWorkRequested> context,
        StageExecution execution,
        Guid wordsArtifactId,
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
            nameof(StageType.Transcription),
            [wordsArtifactId.ToString("N")]), cancellationToken).ConfigureAwait(false);
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
            nameof(StageType.Transcription),
            reviewItemId.ToString("N")), cancellationToken).ConfigureAwait(false);
    }

    private static string Truncate(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return "Transcription failed.";
        }

        var trimmed = message.Trim();
        return trimmed.Length <= 500 ? trimmed : trimmed.Substring(0, 500);
    }
}
