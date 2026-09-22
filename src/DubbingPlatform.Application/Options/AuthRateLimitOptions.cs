using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;

namespace DubbingPlatform.Application.Options;

/// <summary>
/// Auth rate-limit budgets. Binds to the <c>AuthRateLimit</c> section.
/// Login is limited per IP (5/min default); refresh per user (30/min
/// default). <c>Enabled=false</c> relaxes both for CI/hermetic runs.
/// </summary>
public sealed class AuthRateLimitOptions
{
    public const string SectionName = "AuthRateLimit";

    public bool Enabled { get; set; } = true;

    [Range(1, 10000)]
    public int LoginPerMinutePerIp { get; set; } = 5;

    [Range(1, 10000)]
    public int RefreshPerMinutePerUser { get; set; } = 30;
}

/// <summary>
/// Fail-fast startup validation for <see cref="AuthRateLimitOptions"/>.
/// </summary>
public sealed class AuthRateLimitOptionsValidator : IValidateOptions<AuthRateLimitOptions>
{
    public ValidateOptionsResult Validate(string? name, AuthRateLimitOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.LoginPerMinutePerIp < 1 || options.LoginPerMinutePerIp > 10000)
        {
            return ValidateOptionsResult.Fail($"{nameof(AuthRateLimitOptions)}.{nameof(AuthRateLimitOptions.LoginPerMinutePerIp)} must be in 1..10000.");
        }

        if (options.RefreshPerMinutePerUser < 1 || options.RefreshPerMinutePerUser > 10000)
        {
            return ValidateOptionsResult.Fail($"{nameof(AuthRateLimitOptions)}.{nameof(AuthRateLimitOptions.RefreshPerMinutePerUser)} must be in 1..10000.");
        }

        return ValidateOptionsResult.Success;
    }
}
