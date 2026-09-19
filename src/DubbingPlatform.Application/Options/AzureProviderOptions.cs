using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;

namespace DubbingPlatform.Application.Options;

/// <summary>
/// Azure provider settings. Binds to the <c>Azure</c> section.
/// Keys come from env/secret manager only and are marked <see cref="SecretAttribute"/>.
/// Empty keys pass startup validation so mock-only boots work; per-adapter
/// <c>ValidateConfig</c> and the startup validator fail fast with
/// <c>PROVIDER_CONFIGURATION_ERROR</c> when Azure is enabled or routed without keys.
/// </summary>
public sealed class AzureProviderOptions
{
    public const string SectionName = "Azure";

    [Secret]
    [MaxLength(512)]
    public string SpeechKey { get; set; } = string.Empty;

    [MaxLength(128)]
    public string SpeechRegion { get; set; } = string.Empty;

    [Secret]
    [MaxLength(512)]
    public string TranslatorKey { get; set; } = string.Empty;

    [MaxLength(128)]
    public string TranslatorRegion { get; set; } = string.Empty;

    [MaxLength(2048)]
    public string? SpeechBaseUrl { get; set; }

    [MaxLength(2048)]
    public string? TranslatorBaseUrl { get; set; }

    [MaxLength(32)]
    public string ApiVersion { get; set; } = "3.0";
}

/// <summary>
/// Fail-fast startup validation for <see cref="AzureProviderOptions"/>.
/// Only validates shape (lengths, BaseUrl allowlist); missing keys are enforced
/// by per-adapter <c>ValidateConfig</c> when Azure is referenced.
/// </summary>
public sealed class AzureProviderOptionsValidator : IValidateOptions<AzureProviderOptions>
{
    public ValidateOptionsResult Validate(string? name, AzureProviderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        foreach (var url in new[] { options.SpeechBaseUrl, options.TranslatorBaseUrl })
        {
            if (!string.IsNullOrWhiteSpace(url) && !ProviderEndpointValidator.IsAllowed(url))
            {
                return ValidateOptionsResult.Fail(
                    $"{nameof(AzureProviderOptions)} BaseUrl '{url}' is not allowed. Use https or http localhost for tests.");
            }
        }

        if (options.SpeechKey is not null && options.SpeechKey.Length > 512)
        {
            return ValidateOptionsResult.Fail($"{nameof(AzureProviderOptions)}.{nameof(AzureProviderOptions.SpeechKey)} must be <= 512 chars.");
        }

        if (options.TranslatorKey is not null && options.TranslatorKey.Length > 512)
        {
            return ValidateOptionsResult.Fail($"{nameof(AzureProviderOptions)}.{nameof(AzureProviderOptions.TranslatorKey)} must be <= 512 chars.");
        }

        return ValidateOptionsResult.Success;
    }
}
