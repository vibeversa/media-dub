using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;

namespace DubbingPlatform.Application.Options;

/// <summary>
/// One configured capability descriptor. Binds to <c>Providers:Descriptors[]</c>.
/// Global (applies to all tenants); tenant rows in the descriptor table override
/// by higher <see cref="Version"/> per (provider, capability).
/// </summary>
public sealed class ProviderDescriptorOption
{
    public string Provider { get; set; } = "mock";

    public string Capability { get; set; } = "Transcription";

    public string Model { get; set; } = "mock-1";

    public int Version { get; set; } = 1;

    public string[] SupportedLanguages { get; set; } = [];

    public string[] SupportedFormats { get; set; } = [];

    public long MaxInputBytes { get; set; }

    public int MaxDurationMs { get; set; }

    public bool WordTimestamps { get; set; }

    public bool Diarization { get; set; }

    public bool VoiceCloning { get; set; }

    public string Region { get; set; } = "global";

    public string PrivacyClass { get; set; } = "standard";
}

/// <summary>
/// Provider routing configuration. Binds to the <c>Providers</c> section.
/// No hardcoded provider precedence lives in code; routing is driven entirely by
/// <see cref="RoutePriority"/> and <see cref="Enabled"/>.
/// </summary>
public sealed class ProviderOptions
{
    public const string SectionName = "Providers";

    [Required]
    [MinLength(1)]
    [MaxLength(128)]
    public string DefaultProvider { get; set; } = "mock";

    /// <summary>
    /// Ordered provider names per capability (for example transcription).
    /// Keys are matched case-insensitively against capability names.
    /// </summary>
    public Dictionary<string, string[]> RoutePriority { get; set; } = new(StringComparer.Ordinal)
    {
        ["transcription"] = ["mock"],
        ["translation"] = ["mock"],
        ["tts"] = ["mock"],
    };

    /// <summary>
    /// Per-provider enablement flags.
    /// </summary>
    public Dictionary<string, bool> Enabled { get; set; } = new(StringComparer.Ordinal)
    {
        ["mock"] = true,
    };

    /// <summary>
    /// Global capability descriptors (<c>Providers:Descriptors[]</c>).
    /// Empty means mock-only synthesis by the descriptor store.
    /// </summary>
    public List<ProviderDescriptorOption> Descriptors { get; set; } = [];

    [Secret]
    [MaxLength(512)]
    public string AzureApiKey { get; set; } = string.Empty;

    [Secret]
    [MaxLength(512)]
    public string OpenAIApiKey { get; set; } = string.Empty;

    [Secret]
    [MaxLength(512)]
    public string GoogleApiKey { get; set; } = string.Empty;
}

/// <summary>
/// Fail-fast startup validation for <see cref="ProviderOptions"/>.
/// </summary>
public sealed class ProviderOptionsValidator : IValidateOptions<ProviderOptions>
{
    public ValidateOptionsResult Validate(string? name, ProviderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.DefaultProvider))
        {
            return ValidateOptionsResult.Fail($"{nameof(ProviderOptions)}.{nameof(ProviderOptions.DefaultProvider)} must not be empty.");
        }

        if (options.RoutePriority is null || options.RoutePriority.Count == 0)
        {
            return ValidateOptionsResult.Fail($"{nameof(ProviderOptions)}.{nameof(ProviderOptions.RoutePriority)} must contain at least one capability route.");
        }

        foreach (var pair in options.RoutePriority)
        {
            if (string.IsNullOrWhiteSpace(pair.Key))
            {
                return ValidateOptionsResult.Fail($"{nameof(ProviderOptions)}.{nameof(ProviderOptions.RoutePriority)} must not contain empty capability names.");
            }

            if (pair.Value is null || pair.Value.Length == 0)
            {
                return ValidateOptionsResult.Fail($"{nameof(ProviderOptions)}.{nameof(ProviderOptions.RoutePriority)}['{pair.Key}'] must list at least one provider.");
            }

            foreach (var provider in pair.Value)
            {
                if (string.IsNullOrWhiteSpace(provider))
                {
                    return ValidateOptionsResult.Fail($"{nameof(ProviderOptions)}.{nameof(ProviderOptions.RoutePriority)}['{pair.Key}'] must not contain empty provider names.");
                }
            }
        }

        if (options.Enabled is null)
        {
            return ValidateOptionsResult.Fail($"{nameof(ProviderOptions)}.{nameof(ProviderOptions.Enabled)} must not be null.");
        }

        foreach (var pair in options.Enabled)
        {
            if (string.IsNullOrWhiteSpace(pair.Key))
            {
                return ValidateOptionsResult.Fail($"{nameof(ProviderOptions)}.{nameof(ProviderOptions.Enabled)} must not contain empty provider names.");
            }
        }

        if (options.Descriptors is null)
        {
            return ValidateOptionsResult.Fail($"{nameof(ProviderOptions)}.{nameof(ProviderOptions.Descriptors)} must not be null.");
        }

        foreach (var descriptor in options.Descriptors)
        {
            if (descriptor is null)
            {
                return ValidateOptionsResult.Fail($"{nameof(ProviderOptions)}.{nameof(ProviderOptions.Descriptors)} must not contain null entries.");
            }

            if (!ProviderOptionNames.TryParseProvider(descriptor.Provider, out _))
            {
                return ValidateOptionsResult.Fail($"{nameof(ProviderOptions)}.{nameof(ProviderOptions.Descriptors)} has unknown provider '{descriptor.Provider}'.");
            }

            if (!Enum.TryParse<DubbingPlatform.Domain.Enums.ProviderCapability>(descriptor.Capability, ignoreCase: true, out _))
            {
                return ValidateOptionsResult.Fail($"{nameof(ProviderOptions)}.{nameof(ProviderOptions.Descriptors)} has unknown capability '{descriptor.Capability}'.");
            }

            if (string.IsNullOrWhiteSpace(descriptor.Model))
            {
                return ValidateOptionsResult.Fail($"{nameof(ProviderOptions)}.{nameof(ProviderOptions.Descriptors)} model must not be empty.");
            }

            if (descriptor.Version < 1)
            {
                return ValidateOptionsResult.Fail($"{nameof(ProviderOptions)}.{nameof(ProviderOptions.Descriptors)} version must be >= 1.");
            }

            if (descriptor.MaxInputBytes < 0 || descriptor.MaxDurationMs < 0)
            {
                return ValidateOptionsResult.Fail($"{nameof(ProviderOptions)}.{nameof(ProviderOptions.Descriptors)} limits must be >= 0 (0 = unbounded).");
            }

            if (string.IsNullOrWhiteSpace(descriptor.Region) || string.IsNullOrWhiteSpace(descriptor.PrivacyClass))
            {
                return ValidateOptionsResult.Fail($"{nameof(ProviderOptions)}.{nameof(ProviderOptions.Descriptors)} region/privacyClass must not be empty.");
            }
        }

        var referenced = CollectReferencedProviders(options);
        if (referenced.Contains("azure") && string.IsNullOrWhiteSpace(options.AzureApiKey))
        {
            return ValidateOptionsResult.Fail($"{nameof(ProviderOptions)}.{nameof(ProviderOptions.AzureApiKey)} is required when 'azure' is enabled or routed.");
        }

        if (referenced.Contains("openai") && string.IsNullOrWhiteSpace(options.OpenAIApiKey))
        {
            return ValidateOptionsResult.Fail($"{nameof(ProviderOptions)}.{nameof(ProviderOptions.OpenAIApiKey)} is required when 'openai' is enabled or routed.");
        }

        if ((referenced.Contains("google") || referenced.Contains("gemini")) && string.IsNullOrWhiteSpace(options.GoogleApiKey))
        {
            return ValidateOptionsResult.Fail($"{nameof(ProviderOptions)}.{nameof(ProviderOptions.GoogleApiKey)} is required when 'google' is enabled or routed.");
        }

        return ValidateOptionsResult.Success;
    }

    private static HashSet<string> CollectReferencedProviders(ProviderOptions options)
    {
        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (options.Enabled is not null)
        {
            foreach (var pair in options.Enabled)
            {
                if (pair.Value && !string.IsNullOrWhiteSpace(pair.Key))
                {
                    referenced.Add(pair.Key.Trim());
                }
            }
        }

        if (options.RoutePriority is not null)
        {
            foreach (var pair in options.RoutePriority)
            {
                if (pair.Value is null)
                {
                    continue;
                }

                foreach (var provider in pair.Value)
                {
                    if (!string.IsNullOrWhiteSpace(provider))
                    {
                        referenced.Add(provider.Trim());
                    }
                }
            }
        }

        return referenced;
    }
}

/// <summary>
/// Provider/capability name normalization shared by options validation,
/// descriptor loading, and routing. No precedence lives here: ordering always
/// comes from <c>Providers:RoutePriority</c>.
/// Aliases: <c>local</c> and <c>localinference</c> both mean
/// <see cref="DubbingPlatform.Domain.Enums.ProviderType.LocalInference"/>;
/// <c>gemini</c> means <see cref="DubbingPlatform.Domain.Enums.ProviderType.Google"/>.
/// </summary>
public static class ProviderOptionNames
{
    public static bool TryParseProvider(string? name, out DubbingPlatform.Domain.Enums.ProviderType provider)
    {
        provider = default;
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var normalized = name.Trim();
        if (string.Equals(normalized, "local", StringComparison.OrdinalIgnoreCase))
        {
            provider = DubbingPlatform.Domain.Enums.ProviderType.LocalInference;
            return true;
        }

        if (string.Equals(normalized, "gemini", StringComparison.OrdinalIgnoreCase))
        {
            provider = DubbingPlatform.Domain.Enums.ProviderType.Google;
            return true;
        }

        return Enum.TryParse(normalized, ignoreCase: true, out provider)
            && Enum.IsDefined(typeof(DubbingPlatform.Domain.Enums.ProviderType), provider);
    }

    public static string NormalizeProvider(DubbingPlatform.Domain.Enums.ProviderType provider)
    {
        return provider.ToString().ToLowerInvariant();
    }
}
