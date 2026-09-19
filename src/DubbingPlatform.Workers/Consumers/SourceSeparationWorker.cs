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
/// Run-scoped source separation on <c>media.preparation</c>. Claims via
/// <see cref="BaseConsumer{TMessage}"/> (at-least-once, lease fenced),
/// ignores other stages sharing the queue before claiming, delegates to
/// <see cref="SourceSeparationService"/> (disabled/auto-skip → Skipped,
/// low-confidence or error fallback → Completed with fallback flag +
/// <c>SEPARATION_FALLBACK</c> warning, success → Completed with stems), and
/// publishes <c>StageCompleted</c> with the selected dialogue artifact id
/// first for downstream stages (VAD reads the first output id). Permanent
/// separation failures never fail the run unless settings
/// <c>failOnSeparationError=true</c> (the service throws and the shared
/// <c>StageWorkerFailure</c> path fails the execution); transient failures
/// rethrow into transport retry.
/// </summary>
public sealed class SourceSeparationWorker : BaseConsumer<StageWorkRequested>
{
    private readonly IStageExecutionContextFactory _contextFactory;
    private readonly IOptions<RetryOptions> _retryOptions;
    private readonly SourceSeparationService _separation;
    private readonly ILogger<SourceSeparationWorker> _logger;

    public SourceSeparationWorker(
        IStageExecutionContextFactory contextFactory,
        IOptions<RetryOptions> retryOptions,
        SourceSeparationService separation,
        ILogger<SourceSeparationWorker> logger,
        IDeferredSender? deferredSender = null)
        : base(contextFactory, retryOptions, deferredSender)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        ArgumentNullException.ThrowIfNull(retryOptions);
        ArgumentNullException.ThrowIfNull(separation);
        ArgumentNullException.ThrowIfNull(logger);
        _contextFactory = contextFactory;
        _retryOptions = retryOptions;
        _separation = separation;
        _logger = logger;
    }

    protected override bool ShouldProcess(StageWorkRequested message)
    {
        var stage = message.StageTypeRequired ?? message.StageType;
        return string.Equals(stage?.Trim(), nameof(Domain.Enums.StageType.SourceSeparation), StringComparison.Ordinal);
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
            Guid canonicalId = Guid.Empty;
            if (!string.IsNullOrWhiteSpace(execution.InputHash)
                && Guid.TryParseExact(execution.InputHash.Trim(), "N", out var inputGuid))
            {
                canonicalId = inputGuid;
            }

            var result = await _separation.DecideAsync(
                execution.TenantId, execution.ProjectId, execution.ProcessingRunId,
                canonicalId, execution.Id, execution.LeaseOwner, execution.LeaseToken,
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
                nameof(Domain.Enums.StageType.SourceSeparation),
                [result.SelectedDialogueArtifactId.ToString("N")]), cancellationToken).ConfigureAwait(false);
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
                "Source separation failed for run {RunId}: {Error}.",
                execution.ProcessingRunId, ex.Message);
            if (!await StageWorkerFailure.TryFailAsync(
                _contextFactory, _retryOptions, context, execution, ex,
                nameof(Domain.Enums.StageType.SourceSeparation), cancellationToken).ConfigureAwait(false))
            {
                throw;
            }
        }
    }
}
