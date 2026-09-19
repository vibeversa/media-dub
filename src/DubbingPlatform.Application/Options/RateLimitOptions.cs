using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;

namespace DubbingPlatform.Application.Options;

/// <summary>
/// Rate-limit budgets. Binds to the <c>RateLimit</c> section.
/// </summary>
public sealed class RateLimitOptions
{
    public const string SectionName = "RateLimit";

    [Range(1, int.MaxValue)]
    public int RequestsPerMin { get; set; } = 60;

    [Range(1, int.MaxValue)]
    public int TokensPerMin { get; set; } = 100000;

    [Range(1, int.MaxValue)]
    public int CharsPerMin { get; set; } = 500000;

    [Range(1, int.MaxValue)]
    public int AudioSecondsPerMin { get; set; } = 3600;

    [Range(1, 10000)]
    public int Concurrency { get; set; } = 10;
}

/// <summary>
/// Fail-fast startup validation for <see cref="RateLimitOptions"/>.
/// </summary>
public sealed class RateLimitOptionsValidator : IValidateOptions<RateLimitOptions>
{
    public ValidateOptionsResult Validate(string? name, RateLimitOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var limits = new (string Name, int Value)[]
        {
            (nameof(RateLimitOptions.RequestsPerMin), options.RequestsPerMin),
            (nameof(RateLimitOptions.TokensPerMin), options.TokensPerMin),
            (nameof(RateLimitOptions.CharsPerMin), options.CharsPerMin),
            (nameof(RateLimitOptions.AudioSecondsPerMin), options.AudioSecondsPerMin),
        };

        foreach (var (property, value) in limits)
        {
            if (value < 1)
            {
                return ValidateOptionsResult.Fail($"{nameof(RateLimitOptions)}.{property} must be at least 1.");
            }
        }

        if (options.Concurrency < 1 || options.Concurrency > 10000)
        {
            return ValidateOptionsResult.Fail($"{nameof(RateLimitOptions)}.{nameof(RateLimitOptions.Concurrency)} must be in 1..10000.");
        }

        return ValidateOptionsResult.Success;
    }
}
