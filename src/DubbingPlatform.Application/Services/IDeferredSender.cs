namespace DubbingPlatform.Application.Services;

/// <summary>
/// Deferred (delayed) message delivery. Implementations use the MassTransit message
/// scheduler when available and return false (never throw) when scheduling is
/// unavailable; callers fall back to the safety net (the 60s recovery sweeper for
/// lease timeouts, immediate dispatch or manual retry for work). Delays are
/// advisory: handlers always re-check lease tokens and run state before acting,
/// so early or duplicate firings are harmless.
/// </summary>
public interface IDeferredSender
{
    /// <summary>
    /// Sends <paramref name="message"/> to <paramref name="queue"/> after
    /// <paramref name="delay"/>. Returns false instead of throwing when the
    /// scheduler is unavailable.
    /// </summary>
    Task<bool> SendDelayedAsync<T>(string queue, T message, TimeSpan delay, CancellationToken cancellationToken = default)
        where T : class;
}
