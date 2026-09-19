using System.Collections.Concurrent;
using DubbingPlatform.Application.Abstractions.Providers;
using DubbingPlatform.Application.Errors;
using DubbingPlatform.Application.Exceptions;
using DubbingPlatform.Application.Options;
using DubbingPlatform.Domain.Enums;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace DubbingPlatform.Infrastructure.Providers;

/// <summary>
/// In-memory health tracker with best-effort Redis mirror. Memory is
/// authoritative per replica; Redis (when a multiplexer is wired) receives
/// fire-and-forget counters for cross-instance visibility and never affects
/// routing decisions. Unhealthy when the circuit is open, when rate-limited
/// until a future instant, or when the error rate over recent calls exceeds 20%
/// (minimum 5 calls to avoid single-failure flapping). Config validation is
/// separate: <see cref="ValidateConfig"/> checks credentials for enabled or
/// routed non-mock providers and throws <c>PROVIDER_CONFIGURATION_ERROR</c>.
/// </summary>
public sealed class ProviderHealthTracker : IProviderHealthTracker
{
    private const int WindowSize = 100;

    private const int MinCallsForErrorRate = 5;

    private const double MaxErrorRate = 0.2;

    private const int ConsecutiveFailuresToOpen = 5;

    private readonly IOptions<ProviderOptions> _providers;
    private readonly IOptions<RetryOptions> _retry;
    private readonly IConnectionMultiplexer? _redis;
    private readonly IOptions<AzureProviderOptions>? _azure;
    private readonly IOptions<OpenAiProviderOptions>? _openai;
    private readonly IOptions<GoogleProviderOptions>? _google;
    private readonly ConcurrentDictionary<ProviderType, HealthWindow> _windows = new();

    public ProviderHealthTracker(
        IOptions<ProviderOptions> providers,
        IOptions<RetryOptions> retry,
        IConnectionMultiplexer? redis = null,
        IOptions<AzureProviderOptions>? azure = null,
        IOptions<OpenAiProviderOptions>? openai = null,
        IOptions<GoogleProviderOptions>? google = null)
    {
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(retry);
        _providers = providers;
        _retry = retry;
        _redis = redis;
        _azure = azure;
        _openai = openai;
        _google = google;
    }

    public bool IsHealthy(ProviderType provider)
    {
        var window = GetWindow(provider);
        lock (window.Sync)
        {
            if (window.CircuitOpen)
            {
                return false;
            }

            if (window.RateLimitedUntil.HasValue && window.RateLimitedUntil.Value > DateTimeOffset.UtcNow)
            {
                return false;
            }

            if (window.Outcomes.Count >= MinCallsForErrorRate)
            {
                var errors = window.Outcomes.Count(success => !success);
                var rate = (double)errors / window.Outcomes.Count;
                if (rate > MaxErrorRate)
                {
                    return false;
                }
            }

            return true;
        }
    }

    public ProviderHealthState GetState(ProviderType provider)
    {
        var window = GetWindow(provider);
        lock (window.Sync)
        {
            var total = window.Outcomes.Count;
            var errors = window.Outcomes.Count(success => !success);
            var rate = total == 0 ? 0.0 : (double)errors / total;
            var now = DateTimeOffset.UtcNow;
            var rateLimited = window.RateLimitedUntil.HasValue && window.RateLimitedUntil.Value > now;
            var healthy = IsHealthyLocked(window, now);
            var configValid = IsConfigValid(provider);
            return new ProviderHealthState(
                provider,
                configValid,
                !window.CircuitOpen && !rateLimited,
                window.CircuitOpen,
                rate,
                total,
                rateLimited,
                window.RateLimitedUntil,
                healthy && configValid);
        }
    }

    public void RecordSuccess(ProviderType provider)
    {
        var window = GetWindow(provider);
        lock (window.Sync)
        {
            window.Outcomes.Enqueue(true);
            while (window.Outcomes.Count > WindowSize)
            {
                window.Outcomes.Dequeue();
            }

            window.ConsecutiveFailures = 0;
        }

        MirrorToRedis(provider, "success");
    }

    public void RecordFailure(ProviderType provider, OutcomeClass outcome)
    {
        var window = GetWindow(provider);
        lock (window.Sync)
        {
            window.Outcomes.Enqueue(false);
            while (window.Outcomes.Count > WindowSize)
            {
                window.Outcomes.Dequeue();
            }

            window.ConsecutiveFailures++;
            if (window.ConsecutiveFailures >= ConsecutiveFailuresToOpen)
            {
                window.CircuitOpen = true;
            }

            if (outcome == OutcomeClass.ProviderRateLimited)
            {
                window.RateLimitedUntil = DateTimeOffset.UtcNow.AddSeconds(Math.Max(1, _retry.Value.RateLimitDelaySec));
            }
        }

        MirrorToRedis(provider, "failure");
    }

    public void RecordRateLimited(ProviderType provider, TimeSpan? retryAfter = null)
    {
        var window = GetWindow(provider);
        lock (window.Sync)
        {
            window.Outcomes.Enqueue(false);
            while (window.Outcomes.Count > WindowSize)
            {
                window.Outcomes.Dequeue();
            }

            window.ConsecutiveFailures++;
            var delay = retryAfter ?? TimeSpan.FromSeconds(Math.Max(1, _retry.Value.RateLimitDelaySec));
            if (delay < TimeSpan.Zero)
            {
                delay = TimeSpan.Zero;
            }

            window.RateLimitedUntil = DateTimeOffset.UtcNow.Add(delay);
        }

        MirrorToRedis(provider, "ratelimited");
    }

    public void OpenCircuit(ProviderType provider)
    {
        GetWindow(provider).CircuitOpen = true;
    }

    public void ResetCircuit(ProviderType provider)
    {
        var window = GetWindow(provider);
        lock (window.Sync)
        {
            window.CircuitOpen = false;
            window.ConsecutiveFailures = 0;
            window.RateLimitedUntil = null;
        }
    }

    public void ValidateConfig(ProviderType provider)
    {
        if (provider is ProviderType.Mock or ProviderType.LocalInference)
        {
            return;
        }

        var options = _providers.Value;
        var name = ProviderOptionNames.NormalizeProvider(provider);
        if (!IsReferenced(options, name, provider))
        {
            return;
        }

        var missing = provider switch
        {
            ProviderType.Azure => IsAzureMissing(options),
            ProviderType.OpenAI => IsOpenAiMissing(options),
            ProviderType.Google => IsGoogleMissing(options),
            _ => false,
        };

        if (missing)
        {
            throw new ErrorCodeException(
                ErrorCodes.ProviderConfigurationError,
                $"Provider '{name}' is enabled or routed but its API key is not configured.");
        }
    }

    private bool IsAzureMissing(ProviderOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.AzureApiKey))
        {
            return false;
        }

        var azure = _azure?.Value;
        if (azure is null)
        {
            return true;
        }

        return string.IsNullOrWhiteSpace(azure.SpeechKey) && string.IsNullOrWhiteSpace(azure.TranslatorKey);
    }

    private bool IsOpenAiMissing(ProviderOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.OpenAIApiKey))
        {
            return false;
        }

        var openai = _openai?.Value;
        if (openai is null)
        {
            return true;
        }

        return string.IsNullOrWhiteSpace(openai.ApiKey);
    }

    private bool IsGoogleMissing(ProviderOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.GoogleApiKey))
        {
            return false;
        }

        var google = _google?.Value;
        if (google is null)
        {
            return true;
        }

        return string.IsNullOrWhiteSpace(google.ApiKey);
    }

    private static bool IsHealthyLocked(HealthWindow window, DateTimeOffset now)
    {
        if (window.CircuitOpen)
        {
            return false;
        }

        if (window.RateLimitedUntil.HasValue && window.RateLimitedUntil.Value > now)
        {
            return false;
        }

        if (window.Outcomes.Count >= MinCallsForErrorRate)
        {
            var errors = window.Outcomes.Count(success => !success);
            if ((double)errors / window.Outcomes.Count > MaxErrorRate)
            {
                return false;
            }
        }

        return true;
    }

    private bool IsConfigValid(ProviderType provider)
    {
        try
        {
            ValidateConfig(provider);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool IsReferenced(ProviderOptions options, string normalized, ProviderType provider)
    {
        if (options.Enabled is not null)
        {
            foreach (var pair in options.Enabled)
            {
                if (!pair.Value || string.IsNullOrWhiteSpace(pair.Key))
                {
                    continue;
                }

                if (string.Equals(pair.Key.Trim(), normalized, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(pair.Key.Trim(), provider.ToString(), StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        if (options.RoutePriority is not null)
        {
            foreach (var pair in options.RoutePriority)
            {
                if (pair.Value is null)
                {
                    continue;
                }

                foreach (var entry in pair.Value)
                {
                    if (string.Equals(entry?.Trim(), normalized, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(entry?.Trim(), provider.ToString(), StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    private HealthWindow GetWindow(ProviderType provider)
    {
        return _windows.GetOrAdd(provider, _ => new HealthWindow());
    }

    private void MirrorToRedis(ProviderType provider, string outcome)
    {
        var redis = _redis;
        if (redis is null)
        {
            return;
        }

        try
        {
            var database = redis.GetDatabase();
            var key = string.Concat("provider:health:", ProviderOptionNames.NormalizeProvider(provider), ":", outcome);
            database.StringIncrement(key);
            database.KeyExpire(key, TimeSpan.FromHours(1));
        }
        catch (Exception)
        {
            // Mirror only: routing never depends on Redis availability.
        }
    }

    private sealed class HealthWindow
    {
        public readonly object Sync = new();

        public readonly Queue<bool> Outcomes = new();

        public int ConsecutiveFailures;

        public bool CircuitOpen;

        public DateTimeOffset? RateLimitedUntil;
    }
}
