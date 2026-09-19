using Microsoft.Extensions.Http.Resilience;
using Polly;

namespace DubbingPlatform.Api.Resilience;

/// <summary>
/// Resilience configuration for outbound provider HTTP.
/// Retry uses exponential backoff with jitter for transient faults only
/// (HTTP 429/5xx, timeouts); other 4xx fail fast without retry. The circuit
/// breaker opens after sustained failures and breaks for 30 seconds. The total
/// request timeout is 30 seconds.
/// </summary>
public static class ProviderResilience
{
    public const string HttpClientName = "providers";

    /// <summary>
    /// Configures the standard resilience handler for provider calls.
    /// </summary>
    public static void Configure(HttpStandardResilienceOptions options)
    {
        options.Retry.MaxRetryAttempts = 3;
        options.Retry.BackoffType = DelayBackoffType.Exponential;
        options.Retry.UseJitter = true;
        options.Retry.ShouldHandle = args => new ValueTask<bool>(HttpClientResiliencePredicates.IsTransient(args.Outcome));

        options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(30);
        options.CircuitBreaker.MinimumThroughput = 5;
        options.CircuitBreaker.BreakDuration = TimeSpan.FromSeconds(30);

        options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(30);
    }
}
