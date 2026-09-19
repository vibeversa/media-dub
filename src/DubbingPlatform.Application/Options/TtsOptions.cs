using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;

namespace DubbingPlatform.Application.Options;

/// <summary>
/// TTS generation tuning. Binds to the <c>Tts</c> section.
/// <c>MaxAttempts</c> bounds total provider calls per segment (primary plus at
/// most one fallback; retryable rate-limit/timeout codes propagate to the saga
/// for the next attempt while attempts remain, otherwise the unit routes to
/// review); <c>EstimatorEnabled</c> (default true) runs the deterministic
/// duration estimator and applies SSML prosody pre-adjustment before the first
/// paid call; <c>PreviewEnabled</c> (default true) allows
/// <c>isPreview=true</c> timing probes (Task 29); preview and final artifacts
/// are distinct immutable <c>GeneratedAudioArtifact</c> rows.
/// </summary>
public sealed class TtsOptions
{
    public const string SectionName = "Tts";

    [Range(1, 10)]
    public int MaxAttempts { get; set; } = 3;

    public bool EstimatorEnabled { get; set; } = true;

    public bool PreviewEnabled { get; set; } = true;
}

/// <summary>
/// Fail-fast startup validation for <see cref="TtsOptions"/>.
/// </summary>
public sealed class TtsOptionsValidator : IValidateOptions<TtsOptions>
{
    public ValidateOptionsResult Validate(string? name, TtsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.MaxAttempts < 1 || options.MaxAttempts > 10)
        {
            return ValidateOptionsResult.Fail($"{nameof(TtsOptions)}.{nameof(TtsOptions.MaxAttempts)} must be in 1..10.");
        }

        return ValidateOptionsResult.Success;
    }
}
