using DubbingPlatform.Application.Activity;
using DubbingPlatform.Application.MultiTenancy;
using DubbingPlatform.Application.Notifications;
using DubbingPlatform.Application.Services;
using DubbingPlatform.Contracts.Messages;
using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Domain.Enums;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DubbingPlatform.Infrastructure.Messaging;

/// <summary>
/// Outbox-driven durable projection for notifications and activity. Subscribed
/// to the existing published terminal events (run completed/failed, review
/// required/resolved, upload completed/validated); the EF outbox guarantees
/// at-least-once delivery and the projectors deduplicate redeliveries
/// (notifications on <c>SourceEventId</c>, activity on correlation + type +
/// timestamp), so this consumer is idempotent and survives restarts. Terminal
/// run events for cancelling/cancelled runs still project (a failed run the
/// user cancelled is still worth a notification). Schema/identity poison parks
/// in <c>_skipped</c>; cross-tenant/missing-run parks in <c>_error</c>;
/// transient I/O rethrows into transport retry.
/// </summary>
public sealed class NotificationActivityProjectionConsumer :
    IConsumer<RunCompleted>,
    IConsumer<RunFailed>,
    IConsumer<StageReviewRequired>,
    IConsumer<ReviewResolved>,
    IConsumer<MediaUploaded>,
    IConsumer<MediaValidated>
{
    private readonly IStageExecutionContextFactory _contextFactory;
    private readonly NotificationProjector _notifications;
    private readonly ActivityProjector _activity;
    private readonly ILogger<NotificationActivityProjectionConsumer> _logger;

    public NotificationActivityProjectionConsumer(
        IStageExecutionContextFactory contextFactory,
        NotificationProjector notifications,
        ActivityProjector activity,
        ILogger<NotificationActivityProjectionConsumer> logger)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        ArgumentNullException.ThrowIfNull(notifications);
        ArgumentNullException.ThrowIfNull(activity);
        ArgumentNullException.ThrowIfNull(logger);
        _contextFactory = contextFactory;
        _notifications = notifications;
        _activity = activity;
        _logger = logger;
    }

    public Task Consume(ConsumeContext<RunCompleted> context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return ProjectAsync(context, context.Message, context.CancellationToken);
    }

    public Task Consume(ConsumeContext<RunFailed> context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return ProjectAsync(context, context.Message, context.CancellationToken);
    }

    public Task Consume(ConsumeContext<StageReviewRequired> context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return ProjectAsync(context, context.Message, context.CancellationToken);
    }

    public Task Consume(ConsumeContext<ReviewResolved> context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return ProjectAsync(context, context.Message, context.CancellationToken);
    }

    public Task Consume(ConsumeContext<MediaUploaded> context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return ProjectAsync(context, context.Message, context.CancellationToken);
    }

    public Task Consume(ConsumeContext<MediaValidated> context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return ProjectAsync(context, context.Message, context.CancellationToken);
    }

    private async Task ProjectAsync<T>(ConsumeContext<T> context, IntegrationMessage message, CancellationToken cancellationToken)
        where T : class
    {
        var correlationId = string.IsNullOrWhiteSpace(message.CorrelationId)
            ? context.CorrelationId?.ToString() ?? Guid.NewGuid().ToString("N")
            : message.CorrelationId;

        using (Serilog.Context.LogContext.PushProperty("CorrelationId", correlationId))
        using (Serilog.Context.LogContext.PushProperty("TenantId", message.TenantId))
        using (Serilog.Context.LogContext.PushProperty("ProjectId", message.ProjectId))
        {
            if (!MessageVersionPolicy.IsSupported(message.SchemaVersion)
                || message.TenantId == Guid.Empty
                || message.ProjectId == Guid.Empty)
            {
                MessagingMeters.SchemaMismatches.Add(1);
                MessagingMeters.DlqDepth.Add(1);
                await SendToQueueAsync(context, QueueNames.Skipped, message).ConfigureAwait(false);
                return;
            }

            if (message.ProcessingRunId != Guid.Empty && !await RunOwnedByTenantAsync(message, cancellationToken).ConfigureAwait(false))
            {
                MessagingMeters.CrossTenantRejects.Add(1);
                MessagingMeters.DlqDepth.Add(1);
                await SendToQueueAsync(context, QueueNames.Error, message).ConfigureAwait(false);
                return;
            }

            try
            {
                switch (message)
                {
                    case RunCompleted completed:
                        await _notifications.ProjectAsync(NotificationEventMapper.FromRunCompleted(completed), cancellationToken).ConfigureAwait(false);
                        await _activity.AppendAsync(ActivityEventMapper.FromProcessingCompleted(completed), cancellationToken).ConfigureAwait(false);
                        break;
                    case RunFailed failed:
                        await _notifications.ProjectAsync(NotificationEventMapper.FromRunFailed(failed), cancellationToken).ConfigureAwait(false);
                        await _activity.AppendAsync(ActivityEventMapper.FromProcessingFailed(failed), cancellationToken).ConfigureAwait(false);
                        break;
                    case StageReviewRequired required:
                        await _notifications.ProjectAsync(NotificationEventMapper.FromReviewRequired(required), cancellationToken).ConfigureAwait(false);
                        await _activity.AppendAsync(ActivityEventMapper.FromReviewRequested(required), cancellationToken).ConfigureAwait(false);
                        break;
                    case ReviewResolved resolved:
                        await _notifications.ProjectAsync(NotificationEventMapper.FromReviewResolved(resolved), cancellationToken).ConfigureAwait(false);
                        await _activity.AppendAsync(ActivityEventMapper.FromReviewResolved(resolved), cancellationToken).ConfigureAwait(false);
                        break;
                    case MediaUploaded uploaded:
                        await _activity.AppendAsync(ActivityEventMapper.FromUploadCompleted(uploaded), cancellationToken).ConfigureAwait(false);
                        break;
                    case MediaValidated validated:
                        if (!validated.IsValid)
                        {
                            await _notifications.ProjectAsync(NotificationEventMapper.FromUploadRejected(validated), cancellationToken).ConfigureAwait(false);
                            await _activity.AppendAsync(ActivityEventMapper.FromUploadRejected(validated), cancellationToken).ConfigureAwait(false);
                        }

                        break;
                    default:
                        MessagingMeters.DlqDepth.Add(1);
                        await SendToQueueAsync(context, QueueNames.Skipped, message).ConfigureAwait(false);
                        return;
                }
            }
#pragma warning disable CA1031 // Projection poison contract: permanent failures park in _skipped; transient failures skip this filter and retry.
            catch (Exception ex) when (MessageDisposition.DecideFailure(ex) == MessageFate.Skipped)
#pragma warning restore CA1031
            {
                MessagingMeters.DlqDepth.Add(1);
                _logger.LogWarning(
                    "Notification/activity projection poison for message {MessageId}: {Error}. Parked in _skipped.",
                    message.MessageId, ex.Message);
                await SendToQueueAsync(context, QueueNames.Skipped, message).ConfigureAwait(false);
            }
        }
    }

    private async Task<bool> RunOwnedByTenantAsync(IntegrationMessage message, CancellationToken cancellationToken)
    {
        using (TenantContext.BeginMaintenanceScope())
        {
            using var db = _contextFactory.CreateDbContext();
            var run = await db.Set<ProcessingRun>()
                .AsNoTracking()
                .FirstOrDefaultAsync(r => r.Id == message.ProcessingRunId, cancellationToken).ConfigureAwait(false);
            if (run is null)
            {
                return false;
            }

            return run.TenantId == message.TenantId && run.ProjectId == message.ProjectId;
        }
    }

    private static async Task SendToQueueAsync<T>(ConsumeContext<T> context, string queue, IntegrationMessage message)
        where T : class
    {
        var endpoint = await context.GetSendEndpoint(new Uri(string.Concat("queue:", queue))).ConfigureAwait(false);
        await endpoint.Send(message, context.CancellationToken).ConfigureAwait(false);
    }
}
