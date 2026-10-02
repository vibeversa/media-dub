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

    /// <summary>
    /// Per-tenant cap on in-flight provider calls, counted across every
    /// provider (GAP-016 second fairness cap). <see cref="Concurrency"/> is the
    /// per (tenant, provider) window; this is the tenant-wide budget that keeps
    /// one tenant from consuming the whole shared provider quota while other
    /// tenants wait. Must be greater than or equal to
    /// <see cref="Concurrency"/> so the tenant cap never binds before the
    /// per-provider window does.
    /// </summary>
    [Range(1, 10000)]
    public int TenantConcurrency { get; set; } = 50;
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

        if (options.TenantConcurrency < 1 || options.TenantConcurrency > 10000)
        {
            return ValidateOptionsResult.Fail($"{nameof(RateLimitOptions)}.{nameof(RateLimitOptions.TenantConcurrency)} must be in 1..10000.");
        }

        if (options.TenantConcurrency < options.Concurrency)
        {
            return ValidateOptionsResult.Fail(
                $"{nameof(RateLimitOptions)}.{nameof(RateLimitOptions.TenantConcurrency)} must be at least {nameof(RateLimitOptions.Concurrency)}.");
        }

        return ValidateOptionsResult.Success;
    }
}
