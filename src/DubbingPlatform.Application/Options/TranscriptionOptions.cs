using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;

namespace DubbingPlatform.Application.Options;

/// <summary>
/// Segment transcription tuning. Binds to the <c>Transcription</c> section.
/// <c>ConfidenceThreshold</c> is the minimum accepted transcript confidence
/// (below it the service retries within <c>MaxAttempts</c>, tries one fallback
/// provider when compatible, then routes to manual review instead of failing
/// the project); <c>MaxAttempts</c> bounds per-segment provider attempts
/// (a <c>Retry:PerStageMaxAttempts[Transcription]</c> entry overrides it);
/// <c>BatchSize</c> documents the dispatcher batch size for segment fan-out
/// (the saga dispatches 50 per batch; this value must match).
/// </summary>
public sealed class TranscriptionOptions
{
    public const string SectionName = "Transcription";

    [Range(0.0, 1.0)]
    public double ConfidenceThreshold { get; set; } = 0.70;

    [Range(1, 10)]
    public int MaxAttempts { get; set; } = 3;

    [Range(1, 500)]
    public int BatchSize { get; set; } = 50;
}

/// <summary>
/// Fail-fast startup validation for <see cref="TranscriptionOptions"/>.
/// </summary>
public sealed class TranscriptionOptionsValidator : IValidateOptions<TranscriptionOptions>
{
    public ValidateOptionsResult Validate(string? name, TranscriptionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (double.IsNaN(options.ConfidenceThreshold) || options.ConfidenceThreshold < 0.0 || options.ConfidenceThreshold > 1.0)
        {
            return ValidateOptionsResult.Fail($"{nameof(TranscriptionOptions)}.{nameof(TranscriptionOptions.ConfidenceThreshold)} must be in 0..1.");
        }

        if (options.MaxAttempts < 1 || options.MaxAttempts > 10)
        {
            return ValidateOptionsResult.Fail($"{nameof(TranscriptionOptions)}.{nameof(TranscriptionOptions.MaxAttempts)} must be in 1..10.");
        }

        if (options.BatchSize < 1 || options.BatchSize > 500)
        {
            return ValidateOptionsResult.Fail($"{nameof(TranscriptionOptions)}.{nameof(TranscriptionOptions.BatchSize)} must be in 1..500.");
        }

        return ValidateOptionsResult.Success;
    }
}
