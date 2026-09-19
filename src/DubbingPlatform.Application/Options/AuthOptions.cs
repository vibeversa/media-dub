using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;

namespace DubbingPlatform.Application.Options;

/// <summary>
/// JWT authentication settings. Binds to the <c>Auth</c> section.
/// Production uses OIDC <see cref="Authority"/>; hermetic tests and local
/// development may set <see cref="SigningKey"/> (HS256) instead. At least one
/// should be set in any environment that serves authenticated traffic; an empty
/// authority with an empty key still boots (mock/health/OpenAPI) but every
/// authenticated request fails closed with 401.
/// </summary>
public sealed class AuthOptions
{
    public const string SectionName = "Auth";

    [MaxLength(2048)]
    public string Authority { get; set; } = string.Empty;

    [Required]
    [MinLength(1)]
    [MaxLength(256)]
    public string Audience { get; set; } = "dubbing-api";

    public bool RequireHttps { get; set; } = false;

    /// <summary>
    /// Gets or sets the HS256 symmetric signing key for test/local JWT validation.
    /// From env/secret manager only (<c>Auth__SigningKey</c>); never logged.
    /// Minimum 32 chars when set.
    /// </summary>
    [Secret]
    [MaxLength(512)]
    public string SigningKey { get; set; } = string.Empty;
}

/// <summary>
/// Fail-fast startup validation for <see cref="AuthOptions"/>.
/// </summary>
public sealed class AuthOptionsValidator : IValidateOptions<AuthOptions>
{
    public ValidateOptionsResult Validate(string? name, AuthOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.Audience))
        {
            return ValidateOptionsResult.Fail($"{nameof(AuthOptions)}.{nameof(AuthOptions.Audience)} must not be empty.");
        }

        if (!string.IsNullOrEmpty(options.Authority) &&
            !Uri.TryCreate(options.Authority, UriKind.Absolute, out _))
        {
            return ValidateOptionsResult.Fail($"{nameof(AuthOptions)}.{nameof(AuthOptions.Authority)} must be an absolute URI when set.");
        }

        if (options.RequireHttps &&
            !string.IsNullOrEmpty(options.Authority) &&
            !options.Authority.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return ValidateOptionsResult.Fail($"{nameof(AuthOptions)}.{nameof(AuthOptions.Authority)} must use https when {nameof(AuthOptions)}.{nameof(AuthOptions.RequireHttps)} is enabled.");
        }

        if (!string.IsNullOrEmpty(options.SigningKey) && options.SigningKey.Length < 32)
        {
            return ValidateOptionsResult.Fail($"{nameof(AuthOptions)}.{nameof(AuthOptions.SigningKey)} must be at least 32 chars when set.");
        }

        return ValidateOptionsResult.Success;
    }
}
