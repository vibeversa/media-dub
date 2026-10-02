using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Domain.Exceptions;
using Microsoft.Extensions.Logging;

namespace DubbingPlatform.Application.Notifications;

/// <summary>
/// The sole active notification channel (Task 049). In-app delivery is the
/// persisted row itself: Task 012B serves it over
/// <c>GET /api/v1/notifications</c> and Task 034 renders it, so this publisher
/// adds no transport, no queue hop, and no external send.
/// <para>
/// What it does do is hold the channel boundary to Task 002's content rules:
/// the row is re-validated on the way out, so a row carrying a URL, a bearer
/// token, or a secret can never reach a channel (including a future one) even if
/// some future writer bypasses the projector's sanitizers. The log line carries
/// ids and type only.
/// </para>
/// <para>
/// <c>notification.created</c> (Task 013's frozen SSE event type) is the obvious
/// future addition here — a push, not a second durable copy — and it is
/// deliberately not built by this task: the envelope type lives in
/// <c>DubbingPlatform.Api</c> while notifications are projected in
/// <c>DubbingPlatform.Workers</c>, so wiring it is a cross-host transport
/// decision rather than a no-op. See <c>docs/notifications-channels.md</c>.
/// </para>
/// </summary>
public sealed class InAppChannelPublisher : INotificationChannelPublisher
{
    private readonly ILogger<InAppChannelPublisher> _logger;

    public InAppChannelPublisher(ILogger<InAppChannelPublisher> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public string Channel => NotificationChannels.InApp;

    /// <summary>
    /// Accepts the committed row and does nothing else. The durable row is the
    /// delivery, so there is no second write and no external send — the property
    /// <c>NotificationChannelTests</c> asserts as "exactly one active channel,
    /// zero external sends".
    /// <para>
    /// The cancellation token is deliberately unused: there is no I/O to cancel,
    /// and the row is already durable, so throwing on an already-cancelled
    /// token would manufacture a failure for work that is done.
    /// </para>
    /// </summary>
    public Task PublishAsync(Notification notification, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notification);

        // Re-runs the entity guards (URLs, bearer tokens, lengths, expiry
        // ordering) so the channel boundary itself rejects unsafe content.
        notification.Validate();

        _logger.LogDebug(
            "Notification {NotificationId} of type {NotificationType} is delivered by its persisted row on channel {Channel} for recipient {RecipientUserId} in tenant {TenantId}.",
            notification.Id,
            notification.Type,
            Channel,
            notification.RecipientUserId,
            notification.TenantId);

        return Task.CompletedTask;
    }
}