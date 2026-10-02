using DubbingPlatform.Application.Exceptions;
using DubbingPlatform.Application.Options;
using DubbingPlatform.Application.Services;
using DubbingPlatform.Contracts.Messages;
using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Infrastructure.Messaging;
using MassTransit;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DubbingPlatform.Workers.Consumers;

/// <summary>
/// Run-scoped <c>MediaValidation</c> on <c>media.preparation</c> (GAP-013).
/// The saga dispatches this stage first on <c>RunStarted</c>, so without a
/// consumer the message is acked as a no-op and the run stalls before
/// <c>MediaAnalysis</c>. The worker claims through
/// <see cref="BaseConsumer{TMessage}"/> (at-least-once, lease fenced, uniform
/// retry/instrumentation), re-affirms the ingestion validation verdict for the
/// run's own execution, and publishes <c>StageCompleted</c> for the saga.
/// Permanent failures fail the execution and publish <c>StageFailed</c>;
/// transient failures rethrow.
/// </summary>
public sealed class MediaValidationWorker : BaseConsumer<StageWorkRequested>
{
    private readonly IStageExecutionContextFactory _contextFactory;
    private readonly IOptions<RetryOptions> _retryOptions;
    private readonly MediaValidationService _validation;
    private readonly ILogger<MediaValidationWorker> _logger;

    public MediaValidationWorker(
        IStageExecutionContextFactory contextFactory,
        IOptions<RetryOptions> retryOptions,
        MediaValidationService validation,
        ILogger<MediaValidationWorker> logger,
        IDeferredSender? deferredSender = null)
        : base(contextFactory, retryOptions, deferredSender)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        ArgumentNullException.ThrowIfNull(retryOptions);
        ArgumentNullException.ThrowIfNull(validation);
        ArgumentNullException.ThrowIfNull(logger);
        _contextFactory = contextFactory;
        _retryOptions = retryOptions;
        _validation = validation;
        _logger = logger;
    }

    protected override bool ShouldProcess(StageWorkRequested message)
    {
        var stage = message.StageTypeRequired ?? message.StageType;
        return string.Equals(stage?.Trim(), nameof(Domain.Enums.StageType.MediaValidation), StringComparison.Ordinal);
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

        try
        {
            var result = await _validation.ValidateAsync(
                execution.TenantId, execution.ProjectId, execution.ProcessingRunId,
                execution.Id, execution.LeaseOwner, execution.LeaseToken,
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
                nameof(Domain.Enums.StageType.MediaValidation),
                [.. result.OutputArtifactIds.Select(id => id.ToString("N"))]), cancellationToken).ConfigureAwait(false);
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
                "Media validation failed for run {RunId}: {Error}.",
                execution.ProcessingRunId, ex.Message);
            if (!await StageWorkerFailure.TryFailAsync(
                _contextFactory, _retryOptions, context, execution, ex,
                nameof(Domain.Enums.StageType.MediaValidation), cancellationToken).ConfigureAwait(false))
            {
                throw;
            }
        }
    }
}