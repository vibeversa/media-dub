using System.Globalization;
using System.Text.Json;
using DubbingPlatform.Application.Abstractions;
using DubbingPlatform.Application.Errors;
using DubbingPlatform.Application.Exceptions;
using DubbingPlatform.Application.Options;
using DubbingPlatform.Application.Storage;
using DubbingPlatform.Domain.Exceptions;
using DubbingPlatform.Infrastructure.Processes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DubbingPlatform.Infrastructure.Media;

/// <summary>
/// FFprobe media inspection via <see cref="ProcessRunner"/> (ArgumentList only,
/// never shell concatenation). Accepts either a local file path (probed
/// directly) or an S3 storage key (stream-downloaded to
/// <c>Path.GetTempPath()/dubbing-ffprobe-*</c> and deleted afterwards).
/// Runs <c>ffprobe -v quiet -print_format json -show_format -show_streams</c>
/// with <c>Media:FfmpegTimeoutSec</c>; timeouts retry once then fail.
/// Exit≠0, timeout, or unparseable JSON throw <c>MEDIA_CORRUPT</c> (422, fail
/// fast, no transport retry); sniffed container/codecs flow to
/// <c>MediaValidator</c> for <c>MEDIA_UNSUPPORTED</c> decisions. Never trusts
/// client MIME/extension.
/// </summary>
public sealed class FFprobeService : IFFprobeService
{
    private static readonly IReadOnlyList<string> ProbeArgsPrefix =
        ["-v", "quiet", "-print_format", "json", "-show_format", "-show_streams"];

    private readonly IArtifactStorage _storage;
    private readonly ProcessRunner _runner;
    private readonly MediaOptions _media;
    private readonly ILogger<FFprobeService> _logger;

    public FFprobeService(
        IArtifactStorage storage,
        ProcessRunner runner,
        IOptions<MediaOptions> media,
        ILogger<FFprobeService> logger)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(media);
        ArgumentNullException.ThrowIfNull(logger);
        _storage = storage;
        _runner = runner;
        _media = media.Value;
        _logger = logger;
    }

    public async Task<FfprobeResult> ProbeAsync(string storageKeyOrLocalPath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(storageKeyOrLocalPath))
        {
            throw new DomainException("Storage key or local path must not be empty.");
        }

        var input = storageKeyOrLocalPath.Trim();
        string? downloadedTemp = null;
        string filePath = input;
        try
        {
            if (!File.Exists(input))
            {
                StorageKeyBuilder.ValidateKey(input);
                downloadedTemp = Path.Combine(Path.GetTempPath(), string.Concat("dubbing-ffprobe-", Guid.NewGuid().ToString("N"), ".tmp"));
                await DownloadToTempAsync(input, downloadedTemp, cancellationToken).ConfigureAwait(false);
                filePath = downloadedTemp;
            }

            return await ProbeFileWithRetryAsync(filePath, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            DeleteTempQuietly(downloadedTemp);
        }
    }

    /// <summary>
    /// Parses ffprobe JSON stdout into a result. Throws <c>MEDIA_CORRUPT</c>
    /// when required sections are missing or malformed.
    /// </summary>
    public static FfprobeResult Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new ErrorCodeException(ErrorCodes.MediaCorrupt, "FFprobe returned empty output; media is corrupt or undecodable.");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new ErrorCodeException(ErrorCodes.MediaCorrupt, "FFprobe output is not valid JSON; media is corrupt or undecodable.", ex);
        }

        using (document)
        {
            var root = document.RootElement;
            if (!root.TryGetProperty("streams", out var streamsElement) || streamsElement.ValueKind != JsonValueKind.Array)
            {
                throw new ErrorCodeException(ErrorCodes.MediaCorrupt, "FFprobe output has no streams; media is corrupt or undecodable.");
            }

            if (!root.TryGetProperty("format", out var formatElement) || formatElement.ValueKind != JsonValueKind.Object)
            {
                throw new ErrorCodeException(ErrorCodes.MediaCorrupt, "FFprobe output has no format; media is corrupt or undecodable.");
            }

            var formatName = formatElement.TryGetProperty("format_name", out var formatNameElement) && formatNameElement.ValueKind == JsonValueKind.String
                ? formatNameElement.GetString() ?? string.Empty
                : string.Empty;
            var container = NormalizeContainer(formatName);

            long durationMs = 0;
            if (formatElement.TryGetProperty("duration", out var durationElement))
            {
                var durationText = durationElement.ValueKind switch
                {
                    JsonValueKind.String => durationElement.GetString(),
                    JsonValueKind.Number => durationElement.GetDouble().ToString(CultureInfo.InvariantCulture),
                    _ => null,
                };
                if (!string.IsNullOrWhiteSpace(durationText)
                    && double.TryParse(durationText.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
                    && !double.IsNaN(seconds) && !double.IsInfinity(seconds) && seconds >= 0)
                {
                    durationMs = (long)Math.Round(seconds * 1000.0, MidpointRounding.AwayFromZero);
                }
            }

            var streams = new List<FfprobeStream>();
            foreach (var item in streamsElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                streams.Add(ParseStream(item));
            }

            return new FfprobeResult(container, durationMs, streams);
        }
    }

    private async Task<FfprobeResult> ProbeFileWithRetryAsync(string filePath, CancellationToken cancellationToken)
    {
        try
        {
            return await ProbeFileOnceAsync(filePath, cancellationToken).ConfigureAwait(false);
        }
        catch (ErrorCodeException ex) when (string.Equals(ex.ErrorCode, ErrorCodes.MediaCorrupt, StringComparison.Ordinal) && IsTimeout(ex))
        {
            _logger.LogWarning("FFprobe timed out for file; retrying once.");
            return await ProbeFileOnceAsync(filePath, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<FfprobeResult> ProbeFileOnceAsync(string filePath, CancellationToken cancellationToken)
    {
        string workingDir;
        try
        {
            workingDir = ProcessRunner.CreateTempWorkingDir();
        }
        catch (IOException ex)
        {
            throw new ErrorCodeException(ErrorCodes.StorageUnavailable, "Media inspection cannot create temp dir; retry the operation.", ex);
        }

        try
        {
            var args = new List<string>(ProbeArgsPrefix) { filePath };
            var timeout = TimeSpan.FromSeconds(Math.Max(1, _media.FfmpegTimeoutSec));
            var result = await _runner.RunAsync("ffprobe", args, workingDir, timeout, cancellationToken).ConfigureAwait(false);

            if (result.TimedOut)
            {
                throw new ErrorCodeException(ErrorCodes.MediaCorrupt, "FFprobe timed out; media is corrupt or undecodable.");
            }

            if (result.ExitCode != 0)
            {
                throw new ErrorCodeException(ErrorCodes.MediaCorrupt, $"FFprobe failed (exit {result.ExitCode}); media is corrupt or undecodable.");
            }

            return Parse(result.StdOut);
        }
        finally
        {
            try
            {
                Directory.Delete(workingDir, recursive: true);
            }
            catch (Exception)
            {
                // Best effort cleanup.
            }
        }
    }

    private async Task DownloadToTempAsync(string storageKey, string tempPath, CancellationToken cancellationToken)
    {
        Stream download;
        try
        {
            download = await _storage.DownloadAsync(storageKey, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is DomainException || ex is AppException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new ErrorCodeException(ErrorCodes.StorageUnavailable, "Storage is temporarily unavailable; retry the operation.", ex);
        }

        using (download)
        {
            using var temp = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await download.CopyToAsync(temp, cancellationToken).ConfigureAwait(false);
            await temp.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static FfprobeStream ParseStream(JsonElement item)
    {
        var codecType = item.TryGetProperty("codec_type", out var codecTypeElement) && codecTypeElement.ValueKind == JsonValueKind.String
            ? codecTypeElement.GetString() ?? "unknown"
            : "unknown";
        var codec = item.TryGetProperty("codec_name", out var codecElement) && codecElement.ValueKind == JsonValueKind.String
            ? codecElement.GetString() ?? "unknown"
            : "unknown";

        int? width = TryGetInt(item, "width");
        int? height = TryGetInt(item, "height");
        double? fps = TryGetFps(item);
        int? sampleRate = TryGetSampleRate(item);
        int? channels = TryGetInt(item, "channels");
        string? layout = item.TryGetProperty("channel_layout", out var layoutElement) && layoutElement.ValueKind == JsonValueKind.String
            ? layoutElement.GetString()
            : null;

        return new FfprobeStream(codecType, codec, width, height, fps, sampleRate, channels, layout);
    }

    private static int? TryGetInt(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var element))
        {
            return null;
        }

        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var number))
        {
            return number;
        }

        if (element.ValueKind == JsonValueKind.String
            && int.TryParse(element.GetString()?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static int? TryGetSampleRate(JsonElement item)
    {
        if (!item.TryGetProperty("sample_rate", out var element))
        {
            return null;
        }

        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var number))
        {
            return number;
        }

        if (element.ValueKind == JsonValueKind.String
            && double.TryParse(element.GetString()?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            && !double.IsNaN(parsed) && !double.IsInfinity(parsed) && parsed > 0)
        {
            return (int)Math.Round(parsed, MidpointRounding.AwayFromZero);
        }

        return null;
    }

    private static double? TryGetFps(JsonElement item)
    {
        if (!item.TryGetProperty("avg_frame_rate", out var element) || element.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var text = (element.GetString() ?? string.Empty).Trim();
        var slash = text.IndexOf('/');
        if (slash < 0)
        {
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var flat) && flat > 0)
            {
                return flat;
            }

            return null;
        }

        var numeratorText = text.Substring(0, slash).Trim();
        var denominatorText = text.Substring(slash + 1).Trim();
        if (double.TryParse(numeratorText, NumberStyles.Float, CultureInfo.InvariantCulture, out var numerator)
            && double.TryParse(denominatorText, NumberStyles.Float, CultureInfo.InvariantCulture, out var denominator)
            && denominator != 0 && numerator > 0)
        {
            var fps = numerator / denominator;
            return double.IsNaN(fps) || double.IsInfinity(fps) ? null : fps;
        }

        return null;
    }

    internal static string NormalizeContainer(string formatName)
    {
        if (string.IsNullOrWhiteSpace(formatName))
        {
            return "unknown";
        }

        var tokens = formatName.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var token in tokens)
        {
            var mapped = MapFormatToken(token);
            if (!string.Equals(mapped, "unknown", StringComparison.Ordinal))
            {
                return mapped;
            }
        }

        return tokens.Length > 0 ? tokens[0].Trim().ToLowerInvariant() : "unknown";
    }

    private static string MapFormatToken(string token)
    {
        var normalized = token.Trim().ToLowerInvariant();
        return normalized switch
        {
            "mp4" => "mp4",
            "mov" => "mov",
            "m4a" => "mp4",
            "3gp" => "mp4",
            "matroska" => "mkv",
            "webm" => "mkv",
            "mkv" => "mkv",
            "wav" => "wav",
            "mp3" => "mp3",
            "flac" => "flac",
            _ => "unknown",
        };
    }

    private static bool IsTimeout(ErrorCodeException exception)
    {
        return exception.Message.Contains("timed out", StringComparison.OrdinalIgnoreCase);
    }

    private static void DeleteTempQuietly(string? tempPath)
    {
        if (string.IsNullOrEmpty(tempPath))
        {
            return;
        }

        try
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
        catch (Exception)
        {
            // Best effort; OS temp cleaners cover leftovers.
        }
    }
}
