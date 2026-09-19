using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;

namespace DubbingPlatform.Application.Options;

/// <summary>
/// Segment translation tuning. Binds to the <c>Translation</c> section.
/// <c>MaxCandidates</c> bounds the total persisted candidates per segment
/// (primary plus alternatives, truncated deterministically);
/// <c>MaxTokens</c> bounds tracked tokens per segment (usage
/// <c>TokensIn+TokensOut</c> when reported, else <c>chars/4</c> estimate);
/// <c>MaxCost</c> bounds tracked USD per segment (usage
/// <c>EstimatedCostUsd</c> when reported, else zero);
/// <c>MaxWallClockSec</c> bounds provider wall-clock per segment via a linked
/// <c>CancellationTokenSource</c>;
/// <c>QualityThreshold</c> is the minimum accepted semantic score (below it one
/// fallback provider is tried when a second compatible invocable descriptor
/// exists, then the unit routes to manual review instead of failing the run).
/// </summary>
public sealed class TranslationOptions
{
    public const string SectionName = "Translation";

    [Range(1, 10)]
    public int MaxCandidates { get; set; } = 3;

    [Range(1, 100000)]
    public int MaxTokens { get; set; } = 4000;

    [Range(0.0, 100000.0)]
    public double MaxCost { get; set; } = 2.0;

    [Range(1, 600)]
    public int MaxWallClockSec { get; set; } = 60;

    [Range(0.0, 1.0)]
    public double QualityThreshold { get; set; } = 0.70;
}

/// <summary>
/// Fail-fast startup validation for <see cref="TranslationOptions"/>.
/// </summary>
public sealed class TranslationOptionsValidator : IValidateOptions<TranslationOptions>
{
    public ValidateOptionsResult Validate(string? name, TranslationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.MaxCandidates < 1 || options.MaxCandidates > 10)
        {
            return ValidateOptionsResult.Fail($"{nameof(TranslationOptions)}.{nameof(TranslationOptions.MaxCandidates)} must be in 1..10.");
        }

        if (options.MaxTokens < 1 || options.MaxTokens > 100000)
        {
            return ValidateOptionsResult.Fail($"{nameof(TranslationOptions)}.{nameof(TranslationOptions.MaxTokens)} must be in 1..100000.");
        }

        if (double.IsNaN(options.MaxCost) || options.MaxCost < 0.0 || options.MaxCost > 100000.0)
        {
            return ValidateOptionsResult.Fail($"{nameof(TranslationOptions)}.{nameof(TranslationOptions.MaxCost)} must be in 0..100000.");
        }

        if (options.MaxWallClockSec < 1 || options.MaxWallClockSec > 600)
        {
            return ValidateOptionsResult.Fail($"{nameof(TranslationOptions)}.{nameof(TranslationOptions.MaxWallClockSec)} must be in 1..600.");
        }

        if (double.IsNaN(options.QualityThreshold) || options.QualityThreshold < 0.0 || options.QualityThreshold > 1.0)
        {
            return ValidateOptionsResult.Fail($"{nameof(TranslationOptions)}.{nameof(TranslationOptions.QualityThreshold)} must be in 0..1.");
        }

        return ValidateOptionsResult.Success;
    }
}
