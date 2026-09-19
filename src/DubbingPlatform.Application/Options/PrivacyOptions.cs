using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;

namespace DubbingPlatform.Application.Options;

/// <summary>
/// Data-residency and external-provider policy. Binds to the <c>Privacy</c> section.
/// </summary>
public sealed class PrivacyOptions
{
    public const string SectionName = "Privacy";

    public bool ExternalProvidersAllowed { get; set; } = true;

    [Required]
    [MinLength(1)]
    public string[] AllowedProviders { get; set; } = ["mock"];

    [MaxLength(128)]
    public string? ResidencyConstraint { get; set; }
}

/// <summary>
/// Fail-fast startup validation for <see cref="PrivacyOptions"/>.
/// </summary>
public sealed class PrivacyOptionsValidator : IValidateOptions<PrivacyOptions>
{
    public ValidateOptionsResult Validate(string? name, PrivacyOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.AllowedProviders is null || options.AllowedProviders.Length == 0)
        {
            return ValidateOptionsResult.Fail($"{nameof(PrivacyOptions)}.{nameof(PrivacyOptions.AllowedProviders)} must contain at least one provider.");
        }

        foreach (var provider in options.AllowedProviders)
        {
            if (string.IsNullOrWhiteSpace(provider))
            {
                return ValidateOptionsResult.Fail($"{nameof(PrivacyOptions)}.{nameof(PrivacyOptions.AllowedProviders)} must not contain empty entries.");
            }
        }

        return ValidateOptionsResult.Success;
    }
}
