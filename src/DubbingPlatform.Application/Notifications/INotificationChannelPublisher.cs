using DubbingPlatform.Domain.Entities;

namespace DubbingPlatform.Application.Notifications;

/// <summary>
/// One notification delivery mechanism, invoked by
/// <see cref="NotificationChannelDispatcher"/> <em>after</em> the notification
/// row is durably persisted (Task 049).
/// <para><b>Ordering invariant.</b> A publisher is never handed a notification
/// that is not already committed to the store. The projector persists first and
/// publishes second, so a publisher that throws cannot unwrite the row.
/// </para>
/// <para><b>Idempotency invariant.</b> Publishers must be idempotent on
/// <see cref="IdempotencyKey"/> and must tolerate at-least-once delivery.
/// </para>
/// <para><b>Extending the set.</b> Implement this interface in a new class, add
/// the key to <see cref="NotificationChannels.All"/>, and register the class in
/// both hosts (<c>DubbingPlatform.Api/Program.cs</c> and
/// <c>DubbingPlatform.Workers/Program.cs</c>). The projector and the dedup rule
/// are not touched. See <c>docs/notifications-channels.md</c>.
/// </para>
/// <para><b>Content rules.</b> A publisher receives the stored row and is bound
/// by Task 002's rules: only ids, short summaries, codes and counts reach this
/// seam — never transcript bodies, storage keys, signed URLs, or secrets — and a
/// channel must log ids and type only.</para>
/// </summary>
public interface INotificationChannelPublisher
{
    /// <summary>
    /// The frozen channel key this publisher delivers on
    /// (<see cref="NotificationChannels.InApp"/> and friends). Must be listed in
    /// <see cref="NotificationChannels.All"/>; an unlisted key is rejected by the
    /// dispatcher rather than dispatched.
    /// </summary>
    string Channel { get; }

    /// <summary>
    /// The deduplication key for this channel's delivery of
    /// <paramref name="notification"/>: the row's
    /// <see cref="Notification.SourceEventId"/> scoped by
    /// <see cref="Channel"/>. Provided as a default implementation so a new
    /// channel inherits the documented rule instead of inventing one.
    /// </summary>
    string IdempotencyKey(Notification notification) =>
        NotificationChannels.IdempotencyKey(Channel, notification);

    /// <summary>
    /// Delivers one already-persisted notification on this channel. Called once
    /// per newly created row; a deduplicated redelivery publishes nothing.
    /// <para>
    /// Implementations must not re-persist the row (the projector owns that),
    /// must be idempotent on <see cref="IdempotencyKey"/>, and must be safe to
    /// call after a crash — the row is durable whether or not this call
    /// succeeded. A failure propagates to the caller with its original exception
    /// type intact.
    /// </para>
    /// </summary>
    Task PublishAsync(Notification notification, CancellationToken cancellationToken = default);
}