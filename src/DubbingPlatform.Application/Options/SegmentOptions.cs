using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;

namespace DubbingPlatform.Application.Options;

/// <summary>
/// Voice-activity segmentation tuning. Binds to the <c>Segment</c> section.
/// <c>MergePauseMs</c> merges speech gaps shorter than this threshold;
/// <c>MaxSegmentMs</c> splits longer speech into bounded chunks;
/// <c>MaxSegments</c> caps the per-run segment count (enforced together with
/// <c>Quota:MaxSegmentCount</c>; exceeding either fails with
/// <c>QUOTA_EXCEEDED</c> and no partial insert).
/// </summary>
public sealed class SegmentOptions
{
    public const string SectionName = "Segment";

    [Range(0, 5000)]
    public int MergePauseMs { get; set; } = 300;

    [Range(1000, 300000)]
    public int MaxSegmentMs { get; set; } = 30000;

    [Range(1, 1000000)]
    public int MaxSegments { get; set; } = 2000;
}

/// <summary>
/// Fail-fast startup validation for <see cref="SegmentOptions"/>.
/// </summary>
public sealed class SegmentOptionsValidator : IValidateOptions<SegmentOptions>
{
    public ValidateOptionsResult Validate(string? name, SegmentOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.MergePauseMs < 0 || options.MergePauseMs > 5000)
        {
            return ValidateOptionsResult.Fail($"{nameof(SegmentOptions)}.{nameof(SegmentOptions.MergePauseMs)} must be in 0..5000.");
        }

        if (options.MaxSegmentMs < 1000 || options.MaxSegmentMs > 300000)
        {
            return ValidateOptionsResult.Fail($"{nameof(SegmentOptions)}.{nameof(SegmentOptions.MaxSegmentMs)} must be in 1000..300000.");
        }

        if (options.MaxSegments < 1 || options.MaxSegments > 1000000)
        {
            return ValidateOptionsResult.Fail($"{nameof(SegmentOptions)}.{nameof(SegmentOptions.MaxSegments)} must be in 1..1000000.");
        }

        return ValidateOptionsResult.Success;
    }
}
