using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;

namespace DubbingPlatform.Application.Options;

/// <summary>
/// Audio mixing and loudness tuning. Binds to the <c>Mixing</c> section.
/// <c>Profile</c> selects the loudness target (<c>web</c> = -16 LUFS / -1 dBTP,
/// <c>broadcast</c> = -23 LUFS / -1 dBTP; project <c>settings.loudnessProfile</c>
/// overrides this default per run). <c>DuckDb</c> is the background attenuation
/// applied while dialogue is present (negative dB, e.g. -12). <c>FadeMs</c> is
/// the duck attack/release in milliseconds. <c>TwoPass</c> selects deterministic
/// two-pass loudnorm (measure then linear apply) instead of single-pass dynamic
/// normalization.
/// </summary>
public sealed class MixingOptions
{
    public const string SectionName = "Mixing";

    public string Profile { get; set; } = "web";

    public double DuckDb { get; set; } = -12;

    [Range(0, 2000)]
    public int FadeMs { get; set; } = 150;

    public bool TwoPass { get; set; } = true;
}

/// <summary>
/// Fail-fast startup validation for <see cref="MixingOptions"/>.
/// </summary>
public sealed class MixingOptionsValidator : IValidateOptions<MixingOptions>
{
    public ValidateOptionsResult Validate(string? name, MixingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var profile = (options.Profile ?? string.Empty).Trim().ToLowerInvariant();
        if (!string.Equals(profile, "web", StringComparison.Ordinal)
            && !string.Equals(profile, "broadcast", StringComparison.Ordinal))
        {
            return ValidateOptionsResult.Fail($"{nameof(MixingOptions)}.{nameof(MixingOptions.Profile)} must be 'web' or 'broadcast'.");
        }

        if (double.IsNaN(options.DuckDb) || double.IsInfinity(options.DuckDb) || options.DuckDb > 0.0 || options.DuckDb < -30.0)
        {
            return ValidateOptionsResult.Fail($"{nameof(MixingOptions)}.{nameof(MixingOptions.DuckDb)} must be in -30..0 dB.");
        }

        if (options.FadeMs < 0 || options.FadeMs > 2000)
        {
            return ValidateOptionsResult.Fail($"{nameof(MixingOptions)}.{nameof(MixingOptions.FadeMs)} must be in 0..2000.");
        }

        return ValidateOptionsResult.Success;
    }
}
