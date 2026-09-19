using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;

namespace DubbingPlatform.Application.Options;

/// <summary>
/// Voice assignment policy. Binds to the <c>Voices</c> section.
/// <c>CloningEnabled</c> is the global kill-switch for cloned voices (default
/// false: any cloned selection fails with <c>CONSENT_REQUIRED</c> even when a
/// consent row exists); <c>DefaultProvider</c> selects the provider for the
/// synthetic fallback inventory (used only when no TTS descriptor carries a
/// voice inventory) and prefers that provider when an override voiceId exists
/// in multiple providers.
/// </summary>
public sealed class VoiceOptions
{
    public const string SectionName = "Voices";

    public bool CloningEnabled { get; set; }

    [Required]
    [MinLength(1)]
    [MaxLength(128)]
    public string DefaultProvider { get; set; } = "mock";
}

/// <summary>
/// Fail-fast startup validation for <see cref="VoiceOptions"/>.
/// </summary>
public sealed class VoiceOptionsValidator : IValidateOptions<VoiceOptions>
{
    public ValidateOptionsResult Validate(string? name, VoiceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.DefaultProvider))
        {
            return ValidateOptionsResult.Fail($"{nameof(VoiceOptions)}.{nameof(VoiceOptions.DefaultProvider)} must not be empty.");
        }

        if (!ProviderOptionNames.TryParseProvider(options.DefaultProvider, out _))
        {
            return ValidateOptionsResult.Fail($"{nameof(VoiceOptions)}.{nameof(VoiceOptions.DefaultProvider)} has unknown provider '{options.DefaultProvider}'.");
        }

        return ValidateOptionsResult.Success;
    }
}
