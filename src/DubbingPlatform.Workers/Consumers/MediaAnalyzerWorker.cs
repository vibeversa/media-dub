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
/// Run-scoped media analysis on <c>media.preparation</c>. Claims via
/// <see cref="BaseConsumer{TMessage}"/> (at-least-once, lease fenced),
/// ignores other stages sharing the queue before claiming, verifies the
/// source hash, publishes the analysis artifact, completes the execution, and
/// publishes <c>StageCompleted</c> for the saga. Permanent failures fail the
/// execution and publish <c>StageFailed</c>; transient failures rethrow.
/// </summary>
public sealed class MediaAnalyzerWorker : BaseConsumer<StageWorkRequested>
{
    private readonly IStageExecutionContextFactory _contextFactory;
    private readonly IOptions<RetryOptions> _retryOptions;
    private readonly MediaAnalysisService _analysis;
    private readonly ILogger<MediaAnalyzerWorker> _logger;

    public MediaAnalyzerWorker(
        IStageExecutionContextFactory contextFactory,
        IOptions<RetryOptions> retryOptions,
        MediaAnalysisService analysis,
        ILogger<MediaAnalyzerWorker> logger,
        IDeferredSender? deferredSender = null)
        : base(contextFactory, retryOptions, deferredSender)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        ArgumentNullException.ThrowIfNull(retryOptions);
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentNullException.ThrowIfNull(logger);
        _contextFactory = contextFactory;
        _retryOptions = retryOptions;
        _analysis = analysis;
        _logger = logger;
    }

    protected override bool ShouldProcess(StageWorkRequested message)
    {
        var stage = message.StageTypeRequired ?? message.StageType;
        return string.Equals(stage?.Trim(), nameof(Domain.Enums.StageType.MediaAnalysis), StringComparison.Ordinal);
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
            var result = await _analysis.AnalyzeAsync(
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
                nameof(Domain.Enums.StageType.MediaAnalysis),
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
                "Media analysis failed for run {RunId}: {Error}.",
                execution.ProcessingRunId, ex.Message);
            if (!await StageWorkerFailure.TryFailAsync(
                _contextFactory, _retryOptions, context, execution, ex,
                nameof(Domain.Enums.StageType.MediaAnalysis), cancellationToken).ConfigureAwait(false))
            {
                throw;
            }
        }
    }
}
