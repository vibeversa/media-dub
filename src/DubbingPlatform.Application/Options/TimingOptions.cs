using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;

namespace DubbingPlatform.Application.Options;

/// <summary>
/// Timing and synchronization tolerances. Binds to the <c>Timing</c> section.
/// </summary>
public sealed class TimingOptions
{
    public const string SectionName = "Timing";

    [Range(0, int.MaxValue)]
    public int PreferredToleranceMs { get; set; } = 50;

    [Range(0, int.MaxValue)]
    public int MaxToleranceMs { get; set; } = 100;

    [Range(0.0, 100.0)]
    public double MaxRateChangePercent { get; set; } = 15.0;

    [Range(1.0, 2.0)]
    public double MaxStretchFactor { get; set; } = 1.15;

    [Range(1, 10)]
    public int MaxCandidates { get; set; } = 3;

    [Range(1, 10)]
    public int MaxTtsPreviewAttempts { get; set; } = 3;

    [Range(0, 10)]
    public int MaxRewrites { get; set; } = 2;
}

/// <summary>
/// Fail-fast startup validation for <see cref="TimingOptions"/>.
/// </summary>
public sealed class TimingOptionsValidator : IValidateOptions<TimingOptions>
{
    public ValidateOptionsResult Validate(string? name, TimingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.PreferredToleranceMs < 0)
        {
            return ValidateOptionsResult.Fail($"{nameof(TimingOptions)}.{nameof(TimingOptions.PreferredToleranceMs)} must not be negative.");
        }

        if (options.MaxToleranceMs < options.PreferredToleranceMs)
        {
            return ValidateOptionsResult.Fail($"{nameof(TimingOptions)}.{nameof(TimingOptions.MaxToleranceMs)} must be at least {nameof(TimingOptions)}.{nameof(TimingOptions.PreferredToleranceMs)}.");
        }

        if (double.IsNaN(options.MaxRateChangePercent) || options.MaxRateChangePercent < 0.0 || options.MaxRateChangePercent > 100.0)
        {
            return ValidateOptionsResult.Fail($"{nameof(TimingOptions)}.{nameof(TimingOptions.MaxRateChangePercent)} must be in 0..100.");
        }

        if (double.IsNaN(options.MaxStretchFactor) || options.MaxStretchFactor < 1.0 || options.MaxStretchFactor > 2.0)
        {
            return ValidateOptionsResult.Fail($"{nameof(TimingOptions)}.{nameof(TimingOptions.MaxStretchFactor)} must be in 1.0..2.0.");
        }

        if (options.MaxCandidates < 1 || options.MaxCandidates > 10)
        {
            return ValidateOptionsResult.Fail($"{nameof(TimingOptions)}.{nameof(TimingOptions.MaxCandidates)} must be in 1..10.");
        }

        if (options.MaxTtsPreviewAttempts < 1 || options.MaxTtsPreviewAttempts > 10)
        {
            return ValidateOptionsResult.Fail($"{nameof(TimingOptions)}.{nameof(TimingOptions.MaxTtsPreviewAttempts)} must be in 1..10.");
        }

        if (options.MaxRewrites < 0 || options.MaxRewrites > 10)
        {
            return ValidateOptionsResult.Fail($"{nameof(TimingOptions)}.{nameof(TimingOptions.MaxRewrites)} must be in 0..10.");
        }

        return ValidateOptionsResult.Success;
    }
}
