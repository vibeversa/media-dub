using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;

namespace DubbingPlatform.Application.Options;

/// <summary>
/// Diarization policy. Binds from the nested <c>Media:Diarization</c> section
/// (for example <c>"Media": { "Diarization": { "FallbackToSingleSpeaker": true } }</c>).
/// When <see cref="FallbackToSingleSpeaker"/> is true (default), a permanent
/// diarization provider failure falls back to a single project-scoped speaker
/// and completes with a <c>DIARIZATION_FALLBACK</c> warning; when false the
/// stage fails with <c>PROVIDER_FAILED</c>.
/// </summary>
public sealed class DiarizationOptions
{
    public const string SectionName = "Media:Diarization";

    public bool FallbackToSingleSpeaker { get; set; } = true;
}

/// <summary>
/// Fail-fast startup validation for <see cref="DiarizationOptions"/>.
/// </summary>
public sealed class DiarizationOptionsValidator : IValidateOptions<DiarizationOptions>
{
    public ValidateOptionsResult Validate(string? name, DiarizationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return ValidateOptionsResult.Success;
    }
}
