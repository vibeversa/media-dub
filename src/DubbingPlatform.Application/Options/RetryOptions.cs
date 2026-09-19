using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;

namespace DubbingPlatform.Application.Options;

/// <summary>
/// Retry budgets for transports, providers, fallbacks, and stages.
/// Binds to the <c>Retry</c> section.
/// </summary>
public sealed class RetryOptions
{
    public const string SectionName = "Retry";

    [Range(1, 10)]
    public int TransportMaxAttempts { get; set; } = 5;

    [Range(1, 10)]
    public int ProviderRequestMaxAttempts { get; set; } = 3;

    [Range(1, 10)]
    public int FallbackMaxAttempts { get; set; } = 2;

    [Range(1, 10)]
    public int LogicalStageMaxAttempts { get; set; } = 3;

    [Range(1, 10)]
    public int ManualRetryMaxAttempts { get; set; } = 3;

    [Range(0, 3600)]
    public int RateLimitDelaySec { get; set; } = 30;

    /// <summary>
    /// Per-stage attempt overrides keyed by stage name. Empty means no overrides.
    /// </summary>
    public Dictionary<string, int> PerStageMaxAttempts { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>
/// Fail-fast startup validation for <see cref="RetryOptions"/>.
/// </summary>
public sealed class RetryOptionsValidator : IValidateOptions<RetryOptions>
{
    public ValidateOptionsResult Validate(string? name, RetryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var attempts = new (string Name, int Value)[]
        {
            (nameof(RetryOptions.TransportMaxAttempts), options.TransportMaxAttempts),
            (nameof(RetryOptions.ProviderRequestMaxAttempts), options.ProviderRequestMaxAttempts),
            (nameof(RetryOptions.FallbackMaxAttempts), options.FallbackMaxAttempts),
            (nameof(RetryOptions.LogicalStageMaxAttempts), options.LogicalStageMaxAttempts),
            (nameof(RetryOptions.ManualRetryMaxAttempts), options.ManualRetryMaxAttempts),
        };

        foreach (var (property, value) in attempts)
        {
            if (value < 1 || value > 10)
            {
                return ValidateOptionsResult.Fail($"{nameof(RetryOptions)}.{property} must be in 1..10.");
            }
        }

        if (options.RateLimitDelaySec < 0 || options.RateLimitDelaySec > 3600)
        {
            return ValidateOptionsResult.Fail($"{nameof(RetryOptions)}.{nameof(RetryOptions.RateLimitDelaySec)} must be in 0..3600.");
        }

        if (options.PerStageMaxAttempts is null)
        {
            return ValidateOptionsResult.Fail($"{nameof(RetryOptions)}.{nameof(RetryOptions.PerStageMaxAttempts)} must not be null.");
        }

        foreach (var pair in options.PerStageMaxAttempts)
        {
            if (string.IsNullOrWhiteSpace(pair.Key))
            {
                return ValidateOptionsResult.Fail($"{nameof(RetryOptions)}.{nameof(RetryOptions.PerStageMaxAttempts)} must not contain empty stage names.");
            }

            if (pair.Value < 1 || pair.Value > 10)
            {
                return ValidateOptionsResult.Fail($"{nameof(RetryOptions)}.{nameof(RetryOptions.PerStageMaxAttempts)}['{pair.Key}'] must be in 1..10.");
            }
        }

        return ValidateOptionsResult.Success;
    }
}
