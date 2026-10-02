using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Domain.Exceptions;

namespace DubbingPlatform.Application.Notifications;

/// <summary>
/// Frozen notification-channel vocabulary (Task 049). A channel key names one
/// delivery mechanism; the set is closed, so a key that is not listed here is a
/// wiring mistake and is rejected as a validation error instead of being
/// silently ignored. In-app is the only implemented channel: Plan B §8.3.1
/// requires in-app delivery and reserves email/webhook as future-only, so adding
/// a key without adding the implementation is not possible by accident.
/// <para>
/// Ordering is part of the contract. <see cref="All"/> is ordered and
/// <see cref="NotificationChannelDispatcher"/> publishes in that order, so
/// in-app — the channel that must never be dropped — is always attempted first.
/// </para>
/// </summary>
public static class NotificationChannels
{
    /// <summary>
    /// The in-app channel. Its delivery <em>is</em> the persisted row: the
    /// notifications API (Task 012B) serves it and the notification centre
    /// (Task 034) renders it, so the publisher needs no transport of its own.
    /// </summary>
    public const string InApp = "in-app";

    /// <summary>
    /// Every channel key the product recognises, in publish order. Closed set:
    /// adding a channel adds its key here in the same change that adds the
    /// implementation and the registration.
    /// </summary>
    public static readonly string[] All =
    [
        InApp,
    ];

    /// <summary>
    /// Determines whether a channel key is one the product recognises.
    /// </summary>
    public static bool IsKnown(string? channel)
    {
        return !string.IsNullOrWhiteSpace(channel) && All.Contains(channel.Trim(), StringComparer.Ordinal);
    }

    /// <summary>
    /// Validates and canonicalises a channel key. Throws
    /// <see cref="DomainException"/> for a blank or unknown key: an unknown
    /// future channel is a configuration error to be fixed, never a delivery to
    /// be dropped quietly.
    /// </summary>
    public static string Normalize(string? channel)
    {
        var trimmed = channel?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            throw new DomainException("Notification channel key must not be empty.");
        }

        if (!IsKnown(trimmed))
        {
            throw new DomainException(
                $"Notification channel '{trimmed}' is not a known channel. Known channels: {string.Join(", ", All)}.");
        }

        return trimmed;
    }

    /// <summary>
    /// The per-channel idempotency key a publisher must deduplicate on.
    /// <para>
    /// The rule, which every channel inherits: the durable row's
    /// <see cref="Notification.SourceEventId"/> is the dedup key, scoped by
    /// channel so two channels of the same source event keep independent
    /// delivery state. When <see cref="Notification.SourceEventId"/> is null the
    /// projector's dedup is disabled by design, so the key falls back to the
    /// notification's own id — the only stable identity such a row has.
    /// </para>
    /// <para>
    /// Deliveries are at-least-once: a publisher may be handed the same
    /// notification again after a restart or a redelivered source event, and must
    /// therefore treat this key as a deduplication key rather than an assertion
    /// that the notification is new.
    /// </para>
    /// </summary>
    public static string IdempotencyKey(string channel, Notification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);
        var key = Normalize(channel);
        return notification.SourceEventId is { } sourceEventId
            ? string.Concat(key, ":", sourceEventId.ToString("N"))
            : string.Concat(key, ":notification:", notification.Id.ToString("N"));
    }

    /// <summary>
    /// The publish position of a channel key within <see cref="All"/>. Unknown
    /// keys sort last so a misconfiguration is visible in ordering rather than
    /// displacing a real channel.
    /// </summary>
    internal static int OrderOf(string channel)
    {
        var index = Array.IndexOf(All, channel);
        return index < 0 ? int.MaxValue : index;
    }
}