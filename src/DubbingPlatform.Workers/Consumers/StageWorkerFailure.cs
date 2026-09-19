using DubbingPlatform.Application.Exceptions;
using DubbingPlatform.Application.Options;
using DubbingPlatform.Application.Services;
using DubbingPlatform.Contracts.Messages;
using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Domain.Exceptions;
using DubbingPlatform.Infrastructure.Messaging;
using MassTransit;
using Microsoft.Extensions.Options;

namespace DubbingPlatform.Workers.Consumers;

/// <summary>
/// Shared permanent-failure path for stage workers. Transient failures
/// (<see cref="MessageDisposition.DecideFailure"/> → Error: HTTP I/O,
/// timeouts, sockets, general I/O) return false so the caller rethrows into
/// transport retry with the Running lease intact. Permanent failures fail the
/// execution lease-fenced and publish <c>StageFailed</c> (retryable false) so
/// the saga applies fail-fast/budget policy; the caller then returns normally.
/// <c>LeaseLostException</c> always propagates (the base consumer acknowledges
/// it without work).
/// </summary>
internal static class StageWorkerFailure
{
    public static async Task<bool> TryFailAsync(
        IStageExecutionContextFactory contextFactory,
        IOptions<RetryOptions> retryOptions,
        ConsumeContext<StageWorkRequested> context,
        StageExecution execution,
        Exception exception,
        string stageType,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        ArgumentNullException.ThrowIfNull(retryOptions);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(execution);
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentException.ThrowIfNullOrWhiteSpace(stageType);

        if (MessageDisposition.DecideFailure(exception) == MessageFate.Error)
        {
            return false;
        }

        var (code, message) = Classify(exception);
        var service = new StageExecutionService(contextFactory, retryOptions);
        await service.FailAsync(
            execution.TenantId, execution.Id,
            execution.LeaseOwner, execution.LeaseToken,
            code, message, cancellationToken).ConfigureAwait(false);

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
            stageType, code, message, false), cancellationToken).ConfigureAwait(false);

        return true;
    }

    private static (string Code, string Message) Classify(Exception exception)
    {
        if (exception is AppException app)
        {
            return (app.ErrorCode, Truncate(app.Message));
        }

        if (exception is DomainException)
        {
            return (Application.Errors.ErrorCodes.PipelineInvariantViolation, Truncate(exception.Message));
        }

        return (Application.Errors.ErrorCodes.InternalError, Truncate(exception.Message));
    }

    private static string Truncate(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return "Stage work failed.";
        }

        var trimmed = message.Trim();
        return trimmed.Length <= 500 ? trimmed : trimmed.Substring(0, 500);
    }
}
