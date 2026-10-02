using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Domain.Exceptions;
using Microsoft.Extensions.Logging;

namespace DubbingPlatform.Application.Notifications;

/// <summary>
/// Fans one already-persisted notification out to every registered
/// <see cref="INotificationChannelPublisher"/> (Task 049). The dispatcher owns
/// the whole channel policy so the projector does not have to: the projector
/// persists, then hands the committed rows here, and adding a channel later is a
/// new class plus a registration.
/// <para><b>Construction is the validation point.</b> An unknown channel key, a
/// duplicated key, or a missing in-app channel fails here — at service
/// resolution — instead of at delivery time. That is what makes "unknown future
/// channel key" a loud error and "never a silent drop of in-app" a property of
/// the object rather than a code review.</para>
/// </summary>
public sealed class NotificationChannelDispatcher
{
    private readonly IReadOnlyList<INotificationChannelPublisher> _channels;

    private readonly ILogger<NotificationChannelDispatcher> _logger;

    public NotificationChannelDispatcher(
        IEnumerable<INotificationChannelPublisher> channels,
        ILogger<NotificationChannelDispatcher> logger)
    {
        ArgumentNullException.ThrowIfNull(channels);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        var registered = channels.ToList();
        if (registered.Count == 0)
        {
            throw new DomainException(
                $"No notification channel is registered; '{NotificationChannels.InApp}' must be the active channel.");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var channel in registered)
        {
            if (channel is null)
            {
                throw new ArgumentException("A registered notification channel publisher must not be null.", nameof(channels));
            }

            // Normalize throws DomainException for a blank or unknown key.
            var key = NotificationChannels.Normalize(channel.Channel);
            if (!seen.Add(key))
            {
                throw new DomainException($"Notification channel '{key}' is registered more than once.");
            }
        }

        if (!seen.Contains(NotificationChannels.InApp))
        {
            throw new DomainException(
                $"The '{NotificationChannels.InApp}' notification channel must be registered; without it a published notification would be silently dropped.");
        }

        // Publish in vocabulary order so the in-app channel is always attempted
        // first and the sequence does not depend on registration order.
        _channels = [.. registered.OrderBy(c => NotificationChannels.OrderOf(c.Channel))];
    }

    /// <summary>
    /// The active channel keys, in publish order.
    /// </summary>
    public IReadOnlyList<string> ActiveChannels => [.. _channels.Select(c => c.Channel)];

    /// <summary>
    /// Publishes one committed notification on every active channel, in publish
    /// order, stopping at the first failure.
    /// <para>
    /// The exception is rethrown <em>unchanged</em>: this product classifies
    /// transient versus poison failures by exception type
    /// (<c>MessageDisposition.IsTransient</c>), so wrapping it here would turn
    /// every future channel's transport failure into poison and silently stop
    /// the retry. Channel identity is recorded in the log and the meter instead.
    /// </para>
    /// <para>At-least-once: a publisher may see the same notification twice and
    /// must deduplicate on its own <see cref="INotificationChannelPublisher.IdempotencyKey"/>.</para>
    /// </summary>
    public async Task DispatchAsync(Notification notification, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notification);

        foreach (var channel in _channels)
        {
            try
            {
                await channel.PublishAsync(notification, cancellationToken).ConfigureAwait(false);
                NotificationMeters.ChannelPublished.Add(1, ChannelTag(channel.Channel));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The caller went away, not the channel. Counting it as a
                // delivery failure would put a number on a dashboard that no
                // channel can fix.
                throw;
            }
            catch (Exception ex)
            {
                NotificationMeters.ChannelFailed.Add(1, ChannelTag(channel.Channel));

                // Ids and type only: the row is already durable, so this is the
                // only record of the failure and it must not carry the body.
                _logger.LogError(
                    ex,
                    "Notification channel {Channel} failed for notification {NotificationId} of type {NotificationType} for recipient {RecipientUserId} in tenant {TenantId}; the row is already persisted.",
                    channel.Channel,
                    notification.Id,
                    notification.Type,
                    notification.RecipientUserId,
                    notification.TenantId);
                throw;
            }
        }
    }

    private static KeyValuePair<string, object?> ChannelTag(string channel)
    {
        return new KeyValuePair<string, object?>("channel", channel);
    }
}