using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;

namespace DubbingPlatform.Application.Options;

/// <summary>
/// CORS allowlist (Task 037, R4). Binds to the <c>Cors</c> section.
/// Empty means same-origin only (fail closed); credentialed requests never
/// use a wildcard. Origins must be absolute http(s) URIs without trailing
/// slashes or paths.
/// </summary>
public sealed class CorsOptions
{
    public const string SectionName = "Cors";

    public string[] AllowedOrigins { get; set; } = [];
}

/// <summary>
/// Fail-fast startup validation for <see cref="CorsOptions"/>.
/// </summary>
public sealed class CorsOptionsValidator : IValidateOptions<CorsOptions>
{
    public ValidateOptionsResult Validate(string? name, CorsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        foreach (var origin in options.AllowedOrigins ?? [])
        {
            if (string.IsNullOrWhiteSpace(origin))
            {
                return ValidateOptionsResult.Fail($"{nameof(CorsOptions)}.{nameof(CorsOptions.AllowedOrigins)} must not contain empty entries.");
            }

            var trimmed = origin.Trim();
            if (string.Equals(trimmed, "*", StringComparison.Ordinal))
            {
                return ValidateOptionsResult.Fail($"{nameof(CorsOptions)}.{nameof(CorsOptions.AllowedOrigins)} must not be a wildcard; list explicit origins.");
            }

            if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                return ValidateOptionsResult.Fail($"{nameof(CorsOptions)}.{nameof(CorsOptions.AllowedOrigins)} entry '{trimmed}' must be an absolute http(s) URI.");
            }

            if (!string.IsNullOrEmpty(uri.AbsolutePath) && !string.Equals(uri.AbsolutePath, "/", StringComparison.Ordinal))
            {
                return ValidateOptionsResult.Fail($"{nameof(CorsOptions)}.{nameof(CorsOptions.AllowedOrigins)} entry '{trimmed}' must not contain a path.");
            }
        }

        return ValidateOptionsResult.Success;
    }
}
