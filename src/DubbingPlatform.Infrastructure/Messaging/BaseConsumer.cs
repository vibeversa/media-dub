using DubbingPlatform.Application.Exceptions;
using DubbingPlatform.Application.MultiTenancy;
using DubbingPlatform.Application.Options;
using DubbingPlatform.Application.Security;
using DubbingPlatform.Application.Services;
using DubbingPlatform.Contracts.Messages;
using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Domain.Enums;
using DubbingPlatform.Infrastructure.Observability;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DubbingPlatform.Infrastructure.Messaging;

/// <summary>
/// Idempotent at-least-once consumer base. Steps in order: CorrelationId into
/// <c>LogContext</c>; unsupported <c>SchemaVersion</c> or empty ids to
/// <c>_skipped</c> plus metrics; load run (missing to <c>_error</c>, tenant
/// mismatch to <c>_error</c> plus metric, no side effects;
/// Cancelling/Cancelled ack without work — see <see cref="MessageDisposition"/>
/// for the decision table); atomic claim-or-return via
/// <see cref="StageExecutionService"/> (unique-constraint duplicate returns
/// existing, expired leases are taken over); lease renewal; deferred
/// <c>StageLeaseTimeout</c> scheduling (lease TTL + 30s grace) when a
/// <see cref="IDeferredSender"/> is wired; abstract <see cref="HandleAsync"/>;
/// handler poison (non-transient) to <c>_skipped</c> plus <c>dlq.depth</c>,
/// transient failures rethrown into transport retry; lease re-check before
/// commit (stale commits throw <see cref="LeaseLostException"/> and publish
/// nothing; worker-committed Completed/Failed with a matching lease passes).
/// Subclasses sharing a queue override <see cref="ShouldProcess(TMessage)"/>
/// to ignore other stages before claiming.
/// </summary>
/// <typeparam name="TMessage">Integration message type.</typeparam>
public abstract class BaseConsumer<TMessage> : IConsumer<TMessage>
    where TMessage : IntegrationMessage
{    private static readonly TimeSpan LeaseTtl = TimeSpan.FromMinutes(5);

    private static readonly TimeSpan LeaseTimeoutGrace = TimeSpan.FromSeconds(30);

    private readonly IStageExecutionContextFactory _contextFactory;
    private readonly IOptions<RetryOptions> _retryOptions;
    private readonly IDeferredSender? _deferredSender;

    protected BaseConsumer(
        IStageExecutionContextFactory contextFactory,
        IOptions<RetryOptions> retryOptions,
        IDeferredSender? deferredSender = null)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        ArgumentNullException.ThrowIfNull(retryOptions);
        _contextFactory = contextFactory;
        _retryOptions = retryOptions;
        _deferredSender = deferredSender;
    }

    public async Task Consume(ConsumeContext<TMessage> context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var message = context.Message;
        var correlationId = string.IsNullOrWhiteSpace(message.CorrelationId)
            ? context.CorrelationId?.ToString() ?? Guid.NewGuid().ToString("N")
            : message.CorrelationId;

        using (Serilog.Context.LogContext.PushProperty("CorrelationId", correlationId))
        using (Serilog.Context.LogContext.PushProperty("TenantId", message.TenantId))
        using (Serilog.Context.LogContext.PushProperty("ProjectId", message.ProjectId))
        using (Serilog.Context.LogContext.PushProperty("ProcessingRunId", message.ProcessingRunId))
        using (Serilog.Context.LogContext.PushProperty("StageType", message.StageType ?? string.Empty))
        using (Serilog.Context.LogContext.PushProperty("ScopeType", message.ScopeType ?? string.Empty))
        using (Serilog.Context.LogContext.PushProperty("ScopeId", message.ScopeId ?? string.Empty))
        using (Serilog.Context.LogContext.PushProperty("Attempt", message.Attempt))
        {
            TraceEnricher.SetCurrent(message.TenantId, message.ProjectId, message.ProcessingRunId, message.StageType, null, null, message.Attempt);
            if (!MessageVersionPolicy.IsSupported(message.SchemaVersion)
                || message.TenantId == Guid.Empty
                || message.ProjectId == Guid.Empty
                || message.ProcessingRunId == Guid.Empty)
            {
                MessagingMeters.SchemaMismatches.Add(1);
                MessagingMeters.DlqDepth.Add(1);
                await SendToQueueAsync(context, QueueNames.Skipped, message).ConfigureAwait(false);
                return;
            }

            var stageIdentity = ResolveStageIdentity(message);
            using (TenantContext.BeginScope(message.TenantId))
            {
                using var db = _contextFactory.CreateDbContext();

                var run = await db.Set<ProcessingRun>()
                    .FirstOrDefaultAsync(r => r.Id == message.ProcessingRunId, context.CancellationToken)
                    .ConfigureAwait(false);
                var fate = MessageDisposition.Decide(message, run?.Status, run?.TenantId);
                if (fate == MessageFate.Skipped)
                {
                    MessagingMeters.SchemaMismatches.Add(1);
                    MessagingMeters.DlqDepth.Add(1);
                    await SendToQueueAsync(context, QueueNames.Skipped, message).ConfigureAwait(false);
                    return;
                }

                if (fate == MessageFate.Error && run is null)
                {
                    MessagingMeters.DlqDepth.Add(1);
                    await SendToQueueAsync(context, QueueNames.Error, message).ConfigureAwait(false);
                    return;
                }

                if (fate == MessageFate.Error)
                {
                    MessagingMeters.CrossTenantRejects.Add(1);
                    SecurityMeters.CrossTenantRejects.Add(1);
                    MessagingMeters.DlqDepth.Add(1);
                    await SendToQueueAsync(context, QueueNames.Error, message).ConfigureAwait(false);
                    return;
                }

                if (fate == MessageFate.AckWithoutWork)
                {
                    return;
                }

                if (!ShouldProcess(message))
                {
                    return;
                }

                StageExecution? execution = null;
                string owner = string.Empty;
                string token = string.Empty;

                if (stageIdentity is not null)
                {
                    var service = new StageExecutionService(_contextFactory, _retryOptions);
                    owner = ConsumerOwner();
                    var claim = await service.ClaimAsync(
                        message.TenantId,
                        message.ProjectId,
                        message.ProcessingRunId,
                        stageIdentity.Value.StageType,
                        stageIdentity.Value.ScopeType,
                        stageIdentity.Value.ScopeId,
                        message.SegmentId,
                        message.Attempt,
                        owner,
                        LeaseTtl,
                        context.CancellationToken).ConfigureAwait(false);
                    execution = claim.Execution;
                    token = execution.LeaseToken;

                    await service.RenewLeaseAsync(
                        message.TenantId,
                        execution.Id,
                        owner,
                        token,
                        LeaseTtl,
                        context.CancellationToken).ConfigureAwait(false);

                    db.ChangeTracker.Clear();
                    execution = await db.Set<StageExecution>()
                        .FirstOrDefaultAsync(e => e.Id == execution.Id, context.CancellationToken)
                        .ConfigureAwait(false);

                    await ScheduleLeaseTimeoutAsync(message, execution, context.CancellationToken).ConfigureAwait(false);
                    PlatformMetrics.StageStarted(message.TenantId, stageIdentity.Value.StageType);
                }

                try
                {
                    await HandleAsync(context, execution, context.CancellationToken).ConfigureAwait(false);
                }
                catch (LeaseLostException)
                {
                    return;
                }
#pragma warning disable CA1031 // Poison routing contract: permanent handler failures park in _skipped; transient failures skip this filter and retry.
                catch (Exception ex) when (MessageDisposition.DecideFailure(ex) == MessageFate.Skipped)
#pragma warning restore CA1031
                {
                    MessagingMeters.DlqDepth.Add(1);
                    await SendToQueueAsync(context, QueueNames.Skipped, message).ConfigureAwait(false);
                    return;
                }

                if (execution is not null)
                {
                    db.ChangeTracker.Clear();
                    var current = await db.Set<StageExecution>()
                        .AsNoTracking()
                        .FirstOrDefaultAsync(e => e.Id == execution.Id, context.CancellationToken)
                        .ConfigureAwait(false);
                    if (current is null
                        || !string.Equals(current.LeaseOwner, owner, StringComparison.Ordinal)
                        || !string.Equals(current.LeaseToken, token, StringComparison.Ordinal)
                        || (current.Status != StageStatus.Running
                            && current.Status != StageStatus.Completed
                            && current.Status != StageStatus.Failed))
                    {
                        throw new LeaseLostException($"Lease lost for stage execution '{execution.Id}'.");
                    }
                }
            }
        }
    }

    protected abstract Task HandleAsync(
        ConsumeContext<TMessage> context,
        StageExecution? execution,
        CancellationToken cancellationToken);

    /// <summary>
    /// Stage filter checked after run validation but before claiming. Multiple
    /// stage workers share one workload queue (for example MediaAnalysis and
    /// AudioPreparation on <c>media.preparation</c>); MassTransit delivers each
    /// message to every consumer on the endpoint, so each worker must ignore
    /// other stages here — before claiming — or it would orphan a Running
    /// execution owned by the wrong worker. Returning false acknowledges the
    /// message with no side effects.
    /// </summary>
    protected virtual bool ShouldProcess(TMessage message)
    {
        return true;
    }

    private static (string StageType, string ScopeType, string ScopeId)? ResolveStageIdentity(TMessage message)
    {
        if (!string.IsNullOrWhiteSpace(message.StageType)
            && !string.IsNullOrWhiteSpace(message.ScopeType)
            && !string.IsNullOrWhiteSpace(message.ScopeId))
        {
            return (message.StageType, message.ScopeType, message.ScopeId);
        }

        if (message is StageWorkRequested work
            && !string.IsNullOrWhiteSpace(work.StageTypeRequired)
            && !string.IsNullOrWhiteSpace(work.ScopeTypeRequired)
            && !string.IsNullOrWhiteSpace(work.ScopeIdRequired))
        {
            return (work.StageTypeRequired, work.ScopeTypeRequired, work.ScopeIdRequired);
        }

        return null;
    }

    private string ConsumerOwner()
    {
        return $"{GetType().Name}:{Environment.MachineName}";
    }

    private async Task ScheduleLeaseTimeoutAsync(TMessage message, StageExecution? execution, CancellationToken cancellationToken)
    {
        if (_deferredSender is null || execution is null)
        {
            return;
        }

        var timeout = new StageLeaseTimeout(
            Guid.NewGuid(),
            CorrelationId: message.CorrelationId,
            message.TenantId,
            message.ProjectId,
            message.ProcessingRunId,
            execution.Id,
            execution.StageType.ToString(),
            execution.ScopeType.ToString(),
            execution.ScopeId,
            execution.SegmentId,
            MessageVersionPolicy.CurrentVersion,
            DateTimeOffset.UtcNow,
            execution.Attempt,
            execution.InputHash,
            execution.ConfigurationHash,
            execution.ExecutionSnapshotHash,
            execution.LeaseToken,
            execution.LeaseExpiresAt);
        await _deferredSender.SendDelayedAsync(
            QueueNames.Maintenance, timeout, LeaseTtl + LeaseTimeoutGrace, cancellationToken).ConfigureAwait(false);
    }

    private static async Task SendToQueueAsync<T>(ConsumeContext<T> context, string queue, T message)
        where T : class
    {
        var endpoint = await context.GetSendEndpoint(new Uri($"queue:{queue}")).ConfigureAwait(false);
        await endpoint.Send(message, context.CancellationToken).ConfigureAwait(false);
    }
}
