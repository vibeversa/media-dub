using DubbingPlatform.Application.MultiTenancy;
using DubbingPlatform.Application.Options;
using DubbingPlatform.Application.Services;
using DubbingPlatform.Contracts.Messages;
using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Domain.Enums;
using DubbingPlatform.Infrastructure.Messaging;
using DubbingPlatform.Infrastructure.Observability;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DubbingPlatform.Infrastructure.Orchestration;

/// <summary>
/// Handles delayed <see cref="StageLeaseTimeout"/> firings. This consumer never
/// claims work: it verifies the fencing token, execution status, and expiry, and
/// recovers only when the lease is still stale. Fresh tokens, non-Running
/// status, or unexpired leases are acknowledged without side effects (the worker
/// renewed or finished; the firing is obsolete). Recovery reuses
/// <see cref="StageExecutionService.RecoverStaleAsync"/> (bounded by per-stage
/// retry budgets) and requeues the recovered unit when it parked as
/// <c>RetryPending</c> so the attempt is actually redriven.
/// </summary>
public sealed class StageLeaseTimeoutConsumer : IConsumer<StageLeaseTimeout>
{
    private readonly IStageExecutionContextFactory _contextFactory;
    private readonly RetryOptions _retry;
    private readonly WorkDispatcher _dispatcher;
    private readonly ILogger<StageLeaseTimeoutConsumer> _logger;

    public StageLeaseTimeoutConsumer(
        IStageExecutionContextFactory contextFactory,
        IOptions<RetryOptions> retryOptions,
        WorkDispatcher dispatcher,
        ILogger<StageLeaseTimeoutConsumer> logger)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        ArgumentNullException.ThrowIfNull(retryOptions);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(logger);
        _contextFactory = contextFactory;
        _retry = retryOptions.Value;
        _dispatcher = dispatcher;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<StageLeaseTimeout> context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var message = context.Message;

        if (!MessageVersionPolicy.IsSupported(message.SchemaVersion))
        {
            MessagingMeters.SchemaMismatches.Add(1);
            MessagingMeters.DlqDepth.Add(1);
            await SendToQueueAsync(context, QueueNames.Skipped, message).ConfigureAwait(false);
            return;
        }

        if (message.TenantId == Guid.Empty
            || message.ProjectId == Guid.Empty
            || message.ProcessingRunId == Guid.Empty
            || message.StageExecutionId is null
            || message.StageExecutionId == Guid.Empty)
        {
            MessagingMeters.SchemaMismatches.Add(1);
            MessagingMeters.DlqDepth.Add(1);
            await SendToQueueAsync(context, QueueNames.Skipped, message).ConfigureAwait(false);
            return;
        }

        using (TenantContext.BeginScope(message.TenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            var execution = await db.Set<StageExecution>()
                .AsNoTracking()
                .FirstOrDefaultAsync(e => e.Id == message.StageExecutionId, context.CancellationToken)
                .ConfigureAwait(false);

            if (execution is null)
            {
                return;
            }

            if (execution.TenantId != message.TenantId)
            {
                MessagingMeters.CrossTenantRejects.Add(1);
                MessagingMeters.DlqDepth.Add(1);
                await SendToQueueAsync(context, QueueNames.Error, message).ConfigureAwait(false);
                return;
            }

            if (!string.Equals(execution.LeaseToken, message.LeaseToken, StringComparison.Ordinal)
                || execution.Status != StageStatus.Running
                || execution.LeaseExpiresAt > DateTimeOffset.UtcNow)
            {
                return;
            }

            var service = new StageExecutionService(_contextFactory, Options.Create(_retry));
            var recovered = await service.RecoverStaleAsync(context.CancellationToken).ConfigureAwait(false);
            if (recovered == 0)
            {
                return;
            }

            PlatformMetrics.LeaseRecovered(message.TenantId, recovered);
            PlatformMetrics.StageRetry(message.TenantId, execution.StageType.ToString());

            _logger.LogInformation(
                "Lease timeout recovered {Recovered} stale execution(s) for run {RunId}.",
                recovered, message.ProcessingRunId);

            db.ChangeTracker.Clear();
            var current = await db.Set<StageExecution>()
                .AsNoTracking()
                .FirstOrDefaultAsync(e => e.Id == message.StageExecutionId, context.CancellationToken)
                .ConfigureAwait(false);
            if (current is null || current.Status != StageStatus.RetryPending)
            {
                return;
            }

            var requeue = await _dispatcher.DispatchUnitAsync(
                message.TenantId,
                message.ProjectId,
                message.ProcessingRunId,
                current.StageType,
                current.ScopeType,
                current.ScopeId,
                current.SegmentId,
                current.Attempt,
                delay: null,
                context.CancellationToken).ConfigureAwait(false);
            if (requeue.Dispatched == 0)
            {
                _logger.LogWarning(
                    "Recovered execution {ExecutionId} not requeued (reason {Reason}); manual retry may be required.",
                    current.Id, requeue.Reason);
            }
        }
    }

    private static async Task SendToQueueAsync<T>(ConsumeContext<T> context, string queue, T message)
        where T : class
    {
        var endpoint = await context.GetSendEndpoint(new Uri($"queue:{queue}", UriKind.Absolute)).ConfigureAwait(false);
        await endpoint.Send(message, context.CancellationToken).ConfigureAwait(false);
    }
}
