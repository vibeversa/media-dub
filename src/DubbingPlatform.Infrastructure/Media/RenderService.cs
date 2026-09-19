using System.Globalization;
using System.Text.Json;
using DubbingPlatform.Application.Abstractions;
using DubbingPlatform.Application.Errors;
using DubbingPlatform.Application.Exceptions;
using DubbingPlatform.Domain.Exceptions;
using Microsoft.Extensions.Logging;

namespace DubbingPlatform.Infrastructure.Media;

/// <summary>
/// Outcome of one final render. <see cref="FfmpegArgs"/> is the exact argument
/// list passed via <c>ArgumentList</c> (never shell) so the mix decision is
/// auditable. <see cref="ReencodeReason"/> is null for stream-copy renders and
/// audio-only outputs.
/// </summary>
public sealed record RenderResult(
    string OutputPath,
    string OutputFormat,
    string Container,
    int DurationMs,
    int SourceDurationMs,
    double ToleranceMs,
    bool VideoIncluded,
    bool CopyVideo,
    string? ReencodeReason,
    IReadOnlyList<string> FfmpegArgs,
    double? Fps);

/// <summary>
/// Final rendering: muxes the mixed dub audio with the source video (stream
/// copy when the video codec is H.264, else H.264 re-encode) or produces an
/// audio-only output (WAV/MP3/AAC). All invocations use <c>ArgumentList</c>
/// only (never shell) via <see cref="IFFmpegService"/>; inputs are probed via
/// <see cref="IFFprobeService"/> before and after so corrupt media fails with
/// <c>MEDIA_CORRUPT</c> and duration violations fail with
/// <c>VALIDATION_FAILED</c> (no <c>OutputAsset</c> is created). Video renders
/// pad short dub audio to the source video duration with <c>apad+atrim</c> and
/// trim with <c>-shortest</c>; audio-only renders transcode duration-preserving.
/// Tolerances: video <c>1/fps*1000</c> ms (40 ms fallback for 25 fps when fps
/// is unknown), audio-only 100 ms. Only counts, durations, codecs, and levels
/// are logged (never media bytes or secrets).
/// </summary>
public sealed class RenderService
{
    /// <summary>Re-encode reason when the source video codec is not H.264.</summary>
    public const string ReencodeVideoCodec = "video-codec-incompatible";

    /// <summary>Audio-only tolerance in milliseconds.</summary>
    public const double AudioOnlyToleranceMs = 100.0;

    /// <summary>FPS fallback tolerance (40 ms = one frame at 25 fps).</summary>
    public const double FallbackFrameToleranceMs = 40.0;

    private readonly IFFmpegService _ffmpeg;
    private readonly IFFprobeService _ffprobe;
    private readonly ILogger<RenderService> _logger;

    public RenderService(
        IFFmpegService ffmpeg,
        IFFprobeService ffprobe,
        ILogger<RenderService> logger)
    {
        ArgumentNullException.ThrowIfNull(ffmpeg);
        ArgumentNullException.ThrowIfNull(ffprobe);
        ArgumentNullException.ThrowIfNull(logger);
        _ffmpeg = ffmpeg;
        _ffprobe = ffprobe;
        _logger = logger;
    }

    /// <summary>
    /// Parses <c>settings.outputFormat</c> (mp4|wav|mp3|aac, case-insensitive).
    /// Pure; unknown or absent values fail closed to mp4 when a source video
    /// exists else wav (mirrors context settings parsing: settings never fail
    /// a run).
    /// </summary>
    public static string ParseOutputFormat(string? settingsJson, bool hasVideo)
    {
        var fallback = hasVideo ? "mp4" : "wav";
        if (string.IsNullOrWhiteSpace(settingsJson))
        {
            return fallback;
        }

        try
        {
            using var document = JsonDocument.Parse(settingsJson);
            if (document.RootElement.ValueKind is not JsonValueKind.Object)
            {
                return fallback;
            }

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!string.Equals(property.Name, "outputFormat", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (property.Value.ValueKind != JsonValueKind.String)
                {
                    return fallback;
                }

                var value = (property.Value.GetString() ?? string.Empty).Trim().ToLowerInvariant();
                return value switch
                {
                    "mp4" => "mp4",
                    "wav" => "wav",
                    "mp3" => "mp3",
                    "aac" => "aac",
                    _ => fallback,
                };
            }

            return fallback;
        }
        catch (JsonException)
        {
            return fallback;
        }
    }

    /// <summary>
    /// Whether settings JSON explicitly carries <c>outputFormat</c>. Pure.
    /// </summary>
    public static bool HasOutputFormat(string? settingsJson)
    {
        if (string.IsNullOrWhiteSpace(settingsJson))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(settingsJson);
            if (document.RootElement.ValueKind is not JsonValueKind.Object)
            {
                return false;
            }

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (string.Equals(property.Name, "outputFormat", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Tolerance for a render: one video frame (<c>1/fps*1000</c> ms, 40 ms
    /// fallback) when a source video exists, else 100 ms audio-only. Pure.
    /// </summary>
    public static double ToleranceMsFor(bool hasVideo, double? fps)
    {
        if (!hasVideo)
        {
            return AudioOnlyToleranceMs;
        }

        if (fps.HasValue && !double.IsNaN(fps.Value) && !double.IsInfinity(fps.Value) && fps.Value > 0)
        {
            return 1000.0 / fps.Value;
        }

        return FallbackFrameToleranceMs;
    }

    /// <summary>
    /// Whether the output duration is within tolerance of the source. Pure.
    /// </summary>
    public static bool IsWithinTolerance(long outputMs, long sourceMs, double toleranceMs)
    {
        return Math.Abs(outputMs - sourceMs) <= toleranceMs;
    }

    /// <summary>
    /// Whether the source video stream can be stream-copied (H.264). Pure;
    /// anything else requires an H.264 re-encode.
    /// </summary>
    public static bool IsCopySafe(string? videoCodec)
    {
        return string.Equals((videoCodec ?? string.Empty).Trim(), "h264", StringComparison.OrdinalIgnoreCase)
            || string.Equals((videoCodec ?? string.Empty).Trim(), "avc", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// File extension for an output format (leading dot). Pure.
    /// </summary>
    public static string ExtensionFor(string outputFormat)
    {
        return outputFormat switch
        {
            "mp3" => ".mp3",
            "wav" => ".wav",
            "aac" => ".m4a",
            _ => ".mp4",
        };
    }

    /// <summary>
    /// Content type for an output format. Pure.
    /// </summary>
    public static string ContentTypeFor(string outputFormat)
    {
        return outputFormat switch
        {
            "mp3" => "audio/mpeg",
            "wav" => "audio/wav",
            "aac" => "audio/mp4",
            _ => "video/mp4",
        };
    }

    /// <summary>
    /// Container label for an output format (registered on OutputAsset). Pure.
    /// </summary>
    public static string ContainerFor(string outputFormat)
    {
        return outputFormat switch
        {
            "mp3" => "mp3",
            "wav" => "wav",
            "aac" => "m4a",
            _ => "mp4",
        };
    }

    /// <summary>
    /// Builds the video-mux argument list (copy or H.264 re-encode). Pure;
    /// <paramref name="audioPadFilter"/> is the <c>apad+atrim</c> pad/trim to
    /// the source video duration (never null for video renders).
    /// </summary>
    public static IReadOnlyList<string> BuildVideoArgs(
        string sourceVideoPath,
        string mixedAudioPath,
        string outputPath,
        bool copyVideo,
        string audioPadFilter)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceVideoPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(mixedAudioPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(audioPadFilter);

        var args = new List<string>
        {
            "-y", "-i", sourceVideoPath, "-i", mixedAudioPath,
            "-filter:a", audioPadFilter,
        };

        if (copyVideo)
        {
            args.Add("-c:v");
            args.Add("copy");
        }
        else
        {
            args.Add("-c:v");
            args.Add("libx264");
            args.Add("-preset");
            args.Add("slow");
            args.Add("-crf");
            args.Add("18");
        }

        args.Add("-c:a");
        args.Add("aac");
        args.Add("-b:a");
        args.Add("192k");
        args.Add("-shortest");
        args.Add(outputPath);
        return args;
    }

    /// <summary>
    /// Builds the audio-only argument list for <paramref name="outputFormat"/>
    /// (wav|mp3|aac). Pure.
    /// </summary>
    public static IReadOnlyList<string> BuildAudioArgs(
        string mixedAudioPath,
        string outputPath,
        string outputFormat)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mixedAudioPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputFormat);

        return outputFormat switch
        {
            "mp3" => ["-y", "-i", mixedAudioPath, "-c:a", "libmp3lame", "-b:a", "192k", "-ar", "48000", outputPath],
            "aac" => ["-y", "-i", mixedAudioPath, "-c:a", "aac", "-b:a", "192k", "-ar", "48000", outputPath],
            _ => ["-y", "-i", mixedAudioPath, "-c:a", "pcm_s24le", "-ar", "48000", outputPath],
        };
    }

    /// <summary>
    /// Renders the final output. See class docs for mux/encode, pad/trim, and
    /// tolerance semantics.
    /// </summary>
    public async Task<RenderResult> RenderAsync(
        string? sourceVideoPath,
        string mixedAudioPath,
        string outputPath,
        string outputFormat,
        double? fps,
        CancellationToken cancellationToken = default)
    {
        var format = (outputFormat ?? string.Empty).Trim().ToLowerInvariant();
        if (format is not ("mp4" or "wav" or "mp3" or "aac"))
        {
            throw new DomainException($"Output format '{outputFormat}' must be one of mp4|wav|mp3|aac.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(mixedAudioPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        if (!File.Exists(mixedAudioPath.Trim()))
        {
            throw new ErrorCodeException(ErrorCodes.ArtifactUnavailable, "Mixed audio for rendering is unavailable.");
        }

        var hasVideo = !string.IsNullOrWhiteSpace(sourceVideoPath);
        if (hasVideo && !File.Exists(sourceVideoPath!.Trim()))
        {
            throw new ErrorCodeException(ErrorCodes.ArtifactUnavailable, "Source video for rendering is unavailable.");
        }

        if (string.Equals(format, "mp4", StringComparison.Ordinal) && !hasVideo)
        {
            throw new ErrorCodeException(ErrorCodes.ValidationFailed, "MP4 output requires a source video; audio-only runs must use wav|mp3|aac.");
        }

        var workDir = Path.GetDirectoryName(Path.GetFullPath(outputPath.Trim()));
        if (string.IsNullOrWhiteSpace(workDir))
        {
            throw new DomainException("Output path must include a directory.");
        }

        FfprobeResult mixedProbe;
        try
        {
            mixedProbe = await _ffprobe.ProbeAsync(mixedAudioPath.Trim(), cancellationToken).ConfigureAwait(false);
        }
        catch (ErrorCodeException ex) when (string.Equals(ex.ErrorCode, ErrorCodes.MediaCorrupt, StringComparison.Ordinal))
        {
            throw new ErrorCodeException(ErrorCodes.MediaCorrupt, "Mixed audio is undecodable; render is blocked.", ex);
        }

        if (!mixedProbe.Streams.Any(s => string.Equals(s.CodecType, "audio", StringComparison.OrdinalIgnoreCase)))
        {
            throw new ErrorCodeException(ErrorCodes.MediaCorrupt, "Mixed audio has no audio stream; render is blocked.");
        }

        string? videoCodec = null;
        double? videoFps = fps;
        long sourceDurationMs;
        if (hasVideo)
        {
            FfprobeResult videoProbe;
            try
            {
                videoProbe = await _ffprobe.ProbeAsync(sourceVideoPath!.Trim(), cancellationToken).ConfigureAwait(false);
            }
            catch (ErrorCodeException ex) when (string.Equals(ex.ErrorCode, ErrorCodes.MediaCorrupt, StringComparison.Ordinal))
            {
                throw new ErrorCodeException(ErrorCodes.MediaCorrupt, "Source video is undecodable; render is blocked.", ex);
            }

            var video = videoProbe.Streams.FirstOrDefault(s => string.Equals(s.CodecType, "video", StringComparison.OrdinalIgnoreCase));
            if (video is null)
            {
                throw new ErrorCodeException(ErrorCodes.MediaCorrupt, "Source video has no video stream; render is blocked.");
            }

            videoCodec = video.Codec;
            videoFps ??= video.Fps;
            sourceDurationMs = videoProbe.DurationMs > 0 ? videoProbe.DurationMs : mixedProbe.DurationMs;
        }
        else
        {
            sourceDurationMs = mixedProbe.DurationMs;
        }

        if (sourceDurationMs <= 0)
        {
            throw new ErrorCodeException(ErrorCodes.MediaCorrupt, "Source duration is unknown; render is blocked.");
        }

        IReadOnlyList<string> args;
        bool copyVideo = false;
        string? reencodeReason = null;
        if (hasVideo)
        {
            copyVideo = IsCopySafe(videoCodec);
            reencodeReason = copyVideo ? null : ReencodeVideoCodec;
            var videoSec = (sourceDurationMs / 1000.0).ToString("F3", CultureInfo.InvariantCulture);
            var padFilter = string.Concat("apad=whole_dur=", videoSec, ",atrim=0:", videoSec);
            args = BuildVideoArgs(sourceVideoPath!.Trim(), mixedAudioPath.Trim(), outputPath.Trim(), copyVideo, padFilter);
        }
        else
        {
            args = BuildAudioArgs(mixedAudioPath.Trim(), outputPath.Trim(), format);
        }

        try
        {
            await _ffmpeg.RunAsync(args, workDir, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            throw new ErrorCodeException(ErrorCodes.ResourceExhausted, "Render timed out; retry the operation.", ex);
        }
        catch (IOException ex)
        {
            throw new ErrorCodeException(ErrorCodes.ResourceExhausted, "Render hit disk limits.", ex);
        }

        FileInfo rendered;
        try
        {
            rendered = new FileInfo(outputPath.Trim());
        }
        catch (Exception ex)
        {
            throw new ErrorCodeException(ErrorCodes.MediaCorrupt, "Render output is missing.", ex);
        }

        if (!rendered.Exists || rendered.Length < 1)
        {
            throw new ErrorCodeException(ErrorCodes.MediaCorrupt, "FFmpeg produced no render output.");
        }

        FfprobeResult outputProbe;
        try
        {
            outputProbe = await _ffprobe.ProbeAsync(outputPath.Trim(), cancellationToken).ConfigureAwait(false);
        }
        catch (ErrorCodeException ex) when (string.Equals(ex.ErrorCode, ErrorCodes.MediaCorrupt, StringComparison.Ordinal))
        {
            throw new ErrorCodeException(ErrorCodes.MediaCorrupt, "Rendered output is undecodable.", ex);
        }

        if (!outputProbe.Streams.Any(s => string.Equals(s.CodecType, "audio", StringComparison.OrdinalIgnoreCase)))
        {
            throw new ErrorCodeException(ErrorCodes.MediaCorrupt, "Rendered output has no audio stream.");
        }

        if (hasVideo && !outputProbe.Streams.Any(s => string.Equals(s.CodecType, "video", StringComparison.OrdinalIgnoreCase)))
        {
            throw new ErrorCodeException(ErrorCodes.MediaCorrupt, "Rendered output has no video stream.");
        }

        var toleranceMs = ToleranceMsFor(hasVideo, videoFps);
        if (!IsWithinTolerance(outputProbe.DurationMs, sourceDurationMs, toleranceMs))
        {
            throw new ErrorCodeException(
                ErrorCodes.ValidationFailed,
                string.Concat(
                    "Rendered duration ",
                    outputProbe.DurationMs.ToString(CultureInfo.InvariantCulture),
                    "ms differs from source ",
                    sourceDurationMs.ToString(CultureInfo.InvariantCulture),
                    "ms by more than ",
                    toleranceMs.ToString("F1", CultureInfo.InvariantCulture),
                    "ms; no output registered."));
        }

        var durationMs = (int)Math.Min(Math.Max(outputProbe.DurationMs, 0), int.MaxValue);
        _logger.LogInformation(
            "Rendered {Format} output ({Duration}ms from {Source}ms, tolerance {Tolerance}ms, copy={Copy}).",
            format, durationMs, sourceDurationMs, toleranceMs, copyVideo);

        return new RenderResult(
            outputPath.Trim(), format, ContainerFor(format), durationMs,
            (int)Math.Min(sourceDurationMs, int.MaxValue), toleranceMs,
            hasVideo, copyVideo, reencodeReason, args.ToList(), videoFps);
    }
}
