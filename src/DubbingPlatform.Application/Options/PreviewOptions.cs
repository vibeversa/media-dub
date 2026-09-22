using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;

namespace DubbingPlatform.Application.Options;

/// <summary>
/// Preview-lane budgets. Binds to the <c>Preview</c> section.
/// <c>VideoPreviewEnabled</c> is the plan flag <c>preview.video.enabled</c>:
/// video proxies are only generated when the source has a video track and
/// this flag is true (config key <c>Preview:VideoPreviewEnabled</c>).
/// Daily and per-minute caps are enforced per tenant inside
/// <c>Previews.VoicePreviewService</c>; denials fail fast with
/// <c>PREVIEW_QUOTA_EXCEEDED</c> (429) before any provider call.
/// </summary>
public sealed class PreviewOptions
{
    public const string SectionName = "Preview";

    [Range(1, 1000000)]
    public int MaxPreviewsPerDayPerTenant { get; set; } = 200;

    [Range(1, 100000)]
    public int MaxPreviewsPerMinutePerTenant { get; set; } = 10;

    public bool VideoPreviewEnabled { get; set; } = false;
}

/// <summary>
/// Fail-fast startup validation for <see cref="PreviewOptions"/>.
/// </summary>
public sealed class PreviewOptionsValidator : IValidateOptions<PreviewOptions>
{
    public ValidateOptionsResult Validate(string? name, PreviewOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.MaxPreviewsPerDayPerTenant < 1)
        {
            return ValidateOptionsResult.Fail($"{nameof(PreviewOptions)}.{nameof(PreviewOptions.MaxPreviewsPerDayPerTenant)} must be at least 1.");
        }

        if (options.MaxPreviewsPerMinutePerTenant < 1)
        {
            return ValidateOptionsResult.Fail($"{nameof(PreviewOptions)}.{nameof(PreviewOptions.MaxPreviewsPerMinutePerTenant)} must be at least 1.");
        }

        return ValidateOptionsResult.Success;
    }
}
