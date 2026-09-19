using DubbingPlatform.Application.Services;
using MassTransit;
using Microsoft.Extensions.Logging;

namespace DubbingPlatform.Infrastructure.Orchestration;

/// <summary>
/// <see cref="IDeferredSender"/> over the MassTransit message scheduler
/// (registered via <c>AddDelayedMessageScheduler</c>). Delayed delivery is the
/// primary timeout mechanism; the 60s recovery sweeper is the safety-only
/// backstop. When the scheduler is unavailable (e.g. a RabbitMQ broker without
/// the delayed-message plugin), scheduling returns false instead of throwing
/// and the sweeper path covers recovery. Broker-native scheduling never replaces
/// the handler-side re-checks: handlers always verify lease token, status, and
/// expiry before acting, so early or duplicate firings are harmless.
/// </summary>
public sealed class MassTransitDeferredSender : IDeferredSender
{
    private readonly IMessageScheduler _scheduler;
    private readonly ILogger<MassTransitDeferredSender> _logger;

    public MassTransitDeferredSender(IMessageScheduler scheduler, ILogger<MassTransitDeferredSender> logger)
    {
        ArgumentNullException.ThrowIfNull(scheduler);
        ArgumentNullException.ThrowIfNull(logger);
        _scheduler = scheduler;
        _logger = logger;
    }

    public async Task<bool> SendDelayedAsync<T>(string queue, T message, TimeSpan delay, CancellationToken cancellationToken = default)
        where T : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(queue);
        ArgumentNullException.ThrowIfNull(message);
        if (delay <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(delay), delay, "Delay must be positive.");
        }

        try
        {
            await _scheduler.ScheduleSend(
                new Uri($"queue:{queue}", UriKind.Absolute), delay, message, cancellationToken).ConfigureAwait(false);
            return true;
        }
#pragma warning disable CA1031 // Degradation path: an unavailable scheduler must backstop to the sweeper, never fault the caller.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogWarning(ex, "Deferred send to {Queue} unavailable; falling back to sweeper recovery.", queue);
            return false;
        }
    }
}
