using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;

namespace DubbingPlatform.Application.Options;

/// <summary>
/// OpenAI provider settings. Binds to the <c>OpenAI</c> section.
/// <c>BaseUrl</c> defaults to the public API and is overridable to a WireMock
/// URL in tests. Keys are secrets and never logged or hashed.
/// </summary>
public sealed class OpenAiProviderOptions
{
    public const string SectionName = "OpenAI";

    public const string DefaultBaseUrl = "https://api.openai.com/v1";

    [Secret]
    [MaxLength(512)]
    public string ApiKey { get; set; } = string.Empty;

    [MaxLength(128)]
    public string Model { get; set; } = "whisper-1";

    [MaxLength(128)]
    public string ChatModel { get; set; } = "gpt-4o-mini";

    [MaxLength(128)]
    public string TtsModel { get; set; } = "tts-1";

    [MaxLength(2048)]
    public string BaseUrl { get; set; } = DefaultBaseUrl;
}

/// <summary>
/// Shape validation for <see cref="OpenAiProviderOptions"/>.
/// Missing keys fail fast via per-adapter <c>ValidateConfig</c> when referenced.
/// </summary>
public sealed class OpenAiProviderOptionsValidator : IValidateOptions<OpenAiProviderOptions>
{
    public ValidateOptionsResult Validate(string? name, OpenAiProviderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.Model))
        {
            return ValidateOptionsResult.Fail($"{nameof(OpenAiProviderOptions)}.{nameof(OpenAiProviderOptions.Model)} must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(options.ChatModel))
        {
            return ValidateOptionsResult.Fail($"{nameof(OpenAiProviderOptions)}.{nameof(OpenAiProviderOptions.ChatModel)} must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(options.TtsModel))
        {
            return ValidateOptionsResult.Fail($"{nameof(OpenAiProviderOptions)}.{nameof(OpenAiProviderOptions.TtsModel)} must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(options.BaseUrl))
        {
            return ValidateOptionsResult.Fail($"{nameof(OpenAiProviderOptions)}.{nameof(OpenAiProviderOptions.BaseUrl)} must not be empty.");
        }

        if (!ProviderEndpointValidator.IsAllowed(options.BaseUrl))
        {
            return ValidateOptionsResult.Fail(
                $"{nameof(OpenAiProviderOptions)}.{nameof(OpenAiProviderOptions.BaseUrl)} is not allowed. Use https or http localhost for tests.");
        }

        return ValidateOptionsResult.Success;
    }
}
