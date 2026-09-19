using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;

namespace DubbingPlatform.Application.Options;

/// <summary>
/// Google provider settings. Binds to the <c>Google</c> section.
/// Per-service BaseUrls are optional WireMock overrides; when empty the
/// production endpoints are used.
/// </summary>
public sealed class GoogleProviderOptions
{
    public const string SectionName = "Google";

    [Secret]
    [MaxLength(512)]
    public string ApiKey { get; set; } = string.Empty;

    [MaxLength(128)]
    public string ProjectId { get; set; } = string.Empty;

    [MaxLength(64)]
    public string Location { get; set; } = "global";

    public bool UseGeminiForTranslation { get; set; }

    [MaxLength(128)]
    public string GeminiModel { get; set; } = "gemini-1.5-flash";

    [MaxLength(2048)]
    public string? SpeechBaseUrl { get; set; }

    [MaxLength(2048)]
    public string? TranslateBaseUrl { get; set; }

    [MaxLength(2048)]
    public string? GeminiBaseUrl { get; set; }

    [MaxLength(2048)]
    public string? TtsBaseUrl { get; set; }
}

/// <summary>
/// Shape validation for <see cref="GoogleProviderOptions"/>.
/// Missing keys fail fast via per-adapter <c>ValidateConfig</c> when referenced.
/// </summary>
public sealed class GoogleProviderOptionsValidator : IValidateOptions<GoogleProviderOptions>
{
    public ValidateOptionsResult Validate(string? name, GoogleProviderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.Location))
        {
            return ValidateOptionsResult.Fail($"{nameof(GoogleProviderOptions)}.{nameof(GoogleProviderOptions.Location)} must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(options.GeminiModel))
        {
            return ValidateOptionsResult.Fail($"{nameof(GoogleProviderOptions)}.{nameof(GoogleProviderOptions.GeminiModel)} must not be empty.");
        }

        foreach (var url in new[] { options.SpeechBaseUrl, options.TranslateBaseUrl, options.GeminiBaseUrl, options.TtsBaseUrl })
        {
            if (!string.IsNullOrWhiteSpace(url) && !ProviderEndpointValidator.IsAllowed(url))
            {
                return ValidateOptionsResult.Fail(
                    $"{nameof(GoogleProviderOptions)} BaseUrl '{url}' is not allowed. Use https or http localhost for tests.");
            }
        }

        return ValidateOptionsResult.Success;
    }
}
