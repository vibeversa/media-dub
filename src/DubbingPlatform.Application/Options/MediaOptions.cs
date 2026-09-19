using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;

namespace DubbingPlatform.Application.Options;

/// <summary>
/// Media ingestion and processing limits. Binds to the <c>Media</c> section.
/// </summary>
public sealed class MediaOptions
{
    public const string SectionName = "Media";

    [Range(typeof(long), "1", "9223372036854775807")]
    public long MaxUploadBytes { get; set; } = 5368709120;

    [Range(1, int.MaxValue)]
    public int MaxDurationMs { get; set; } = 7200000;

    [Required]
    [MinLength(1)]
    public string[] AllowedContainers { get; set; } = ["mp4", "mov", "mkv", "wav", "mp3", "flac"];

    [Range(typeof(long), "0", "9223372036854775807")]
    public long MinDiskFreeBytes { get; set; } = 1073741824;

    [Range(1, 86400)]
    public int FfmpegTimeoutSec { get; set; } = 600;

    [Range(1, 64)]
    public int MaxConcurrentMediaJobs { get; set; } = 2;

    [Range(1, 256)]
    public int CpuThreads { get; set; } = 4;

    /// <summary>
    /// Whether stage workers re-hash source bytes from storage before use.
    /// Default true; disable only for trusted local fixtures.
    /// </summary>
    public bool VerifyOnUse { get; set; } = true;

    /// <summary>
    /// Normalized separation-confidence acceptance threshold (0..1).
    /// Raw provider confidences are normalized to 0..1 via the descriptor
    /// ConfidenceSemantics range before comparison; at or above selects
    /// separated stems, below falls back to canonical.
    /// </summary>
    [Range(0.0, 1.0)]
    public double SeparationThreshold { get; set; } = 0.70;
}

/// <summary>
/// Fail-fast startup validation for <see cref="MediaOptions"/>.
/// </summary>
public sealed class MediaOptionsValidator : IValidateOptions<MediaOptions>
{
    public ValidateOptionsResult Validate(string? name, MediaOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.MaxUploadBytes < 1)
        {
            return ValidateOptionsResult.Fail($"{nameof(MediaOptions)}.{nameof(MediaOptions.MaxUploadBytes)} must be at least 1.");
        }

        if (options.MaxDurationMs < 1)
        {
            return ValidateOptionsResult.Fail($"{nameof(MediaOptions)}.{nameof(MediaOptions.MaxDurationMs)} must be at least 1.");
        }

        if (options.AllowedContainers is null || options.AllowedContainers.Length == 0)
        {
            return ValidateOptionsResult.Fail($"{nameof(MediaOptions)}.{nameof(MediaOptions.AllowedContainers)} must contain at least one container.");
        }

        foreach (var container in options.AllowedContainers)
        {
            if (string.IsNullOrWhiteSpace(container))
            {
                return ValidateOptionsResult.Fail($"{nameof(MediaOptions)}.{nameof(MediaOptions.AllowedContainers)} must not contain empty entries.");
            }
        }

        if (options.MinDiskFreeBytes < 0)
        {
            return ValidateOptionsResult.Fail($"{nameof(MediaOptions)}.{nameof(MediaOptions.MinDiskFreeBytes)} must not be negative.");
        }

        if (options.FfmpegTimeoutSec < 1 || options.FfmpegTimeoutSec > 86400)
        {
            return ValidateOptionsResult.Fail($"{nameof(MediaOptions)}.{nameof(MediaOptions.FfmpegTimeoutSec)} must be in 1..86400.");
        }

        if (options.MaxConcurrentMediaJobs < 1 || options.MaxConcurrentMediaJobs > 64)
        {
            return ValidateOptionsResult.Fail($"{nameof(MediaOptions)}.{nameof(MediaOptions.MaxConcurrentMediaJobs)} must be in 1..64.");
        }

        if (options.CpuThreads < 1 || options.CpuThreads > 256)
        {
            return ValidateOptionsResult.Fail($"{nameof(MediaOptions)}.{nameof(MediaOptions.CpuThreads)} must be in 1..256.");
        }

        if (double.IsNaN(options.SeparationThreshold) || options.SeparationThreshold < 0.0 || options.SeparationThreshold > 1.0)
        {
            return ValidateOptionsResult.Fail($"{nameof(MediaOptions)}.{nameof(MediaOptions.SeparationThreshold)} must be in 0..1.");
        }

        return ValidateOptionsResult.Success;
    }
}
