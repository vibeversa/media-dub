using System.Globalization;
using System.Text.Json;
using DubbingPlatform.Application.Abstractions;
using DubbingPlatform.Application.Errors;
using DubbingPlatform.Application.Exceptions;
using DubbingPlatform.Application.Options;
using DubbingPlatform.Domain.Exceptions;
using DubbingPlatform.Infrastructure.Observability;
using DubbingPlatform.Infrastructure.Processes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DubbingPlatform.Infrastructure.Media;

/// <summary>
/// Hardened FFmpeg audio extraction via <see cref="ProcessRunner"/>
/// (<c>ArgumentList</c> only, never shell concatenation). Canonical audio is
/// 48kHz 24-bit FLAC (<c>-sample_fmt s32 -c:a flac</c>; FLAC stores the
/// 24-bit samples losslessly — the <c>s32</c> container samples are
/// effectively 24-bit audio) with the source channel layout preserved (no
/// <c>-ac</c> downmix unless a provider stage requests it later) and video
/// dropped (<c>-vn</c>). The optional 32-bit float working copy
/// (<c>flt→pcm_f32le</c>) is produced only when <c>needsFloatWork</c> is true.
/// <c>-threads {CpuThreads}</c> from <c>Media:CpuThreads</c> is placed before
/// the inputs (it must precede outputs to be valid FFmpeg syntax). Exact args
/// are logged (paths/flags only, never secrets). Timeouts retry once, then
/// throw <c>TimeoutException</c> (transient); non-zero exits throw
/// <c>INTERNAL_ERROR</c> (permanent, the worker records <c>Failed</c>);
/// missing/decodable-less output throws <c>MEDIA_CORRUPT</c>.
/// </summary>
public sealed class FFmpegService : IFFmpegService
{
    private readonly ProcessRunner _runner;
    private readonly IFFprobeService _ffprobe;
    private readonly MediaOptions _media;
    private readonly ILogger<FFmpegService> _logger;

    public FFmpegService(
        ProcessRunner runner,
        IFFprobeService ffprobe,
        IOptions<MediaOptions> media,
        ILogger<FFmpegService> logger)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(ffprobe);
        ArgumentNullException.ThrowIfNull(media);
        ArgumentNullException.ThrowIfNull(logger);
        _runner = runner;
        _ffprobe = ffprobe;
        _media = media.Value;
        _logger = logger;
    }

    public async Task<FfmpegResult> RunAsync(IReadOnlyList<string> args, string workDir, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentException.ThrowIfNullOrWhiteSpace(workDir);
        TraceEnricher.SetCurrent(stage: "ffmpeg");

        var timeout = TimeSpan.FromSeconds(Math.Max(1, _media.FfmpegTimeoutSec));
        _logger.LogInformation("Running ffmpeg with args: {Args}", string.Join(" ", args));

        var first = await _runner.RunAsync("ffmpeg", args, workDir, timeout, cancellationToken).ConfigureAwait(false);
        if (!first.TimedOut)
        {
            return ToResult(first, args);
        }

        _logger.LogWarning("FFmpeg timed out; retrying once.");
        var second = await _runner.RunAsync("ffmpeg", args, workDir, timeout, cancellationToken).ConfigureAwait(false);
        if (second.TimedOut)
        {
            throw new TimeoutException("FFmpeg timed out twice; retry the operation.");
        }

        return ToResult(second, args);
    }

    public IReadOnlyList<string> BuildCanonicalArgs(string sourcePath, string destPath, int cpuThreads)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destPath);
        ArgumentOutOfRangeException.ThrowIfLessThan(cpuThreads, 1);

        return ["-y", "-threads", cpuThreads.ToString(CultureInfo.InvariantCulture), "-i", sourcePath, "-vn", "-ar", "48000", "-sample_fmt", "s32", "-c:a", "flac", destPath];
    }

    public IReadOnlyList<string> BuildFloatArgs(string sourcePath, string destPath, int cpuThreads)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destPath);
        ArgumentOutOfRangeException.ThrowIfLessThan(cpuThreads, 1);

        return ["-y", "-threads", cpuThreads.ToString(CultureInfo.InvariantCulture), "-i", sourcePath, "-vn", "-ar", "48000", "-sample_fmt", "flt", "-c:a", "pcm_f32le", destPath];
    }

    public IReadOnlyList<string> BuildSliceArgs(string sourcePath, string destPath, double startSec, double durationSec, int cpuThreads)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destPath);
        ArgumentOutOfRangeException.ThrowIfNegative(startSec);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(durationSec, 0);
        ArgumentOutOfRangeException.ThrowIfLessThan(cpuThreads, 1);

        return ["-y", "-threads", cpuThreads.ToString(CultureInfo.InvariantCulture), "-ss", startSec.ToString("F3", CultureInfo.InvariantCulture), "-i", sourcePath, "-t", durationSec.ToString("F3", CultureInfo.InvariantCulture), "-vn", "-ar", "16000", "-ac", "1", "-sample_fmt", "s16", "-c:a", "pcm_s16le", destPath];
    }

    public async Task<FfmpegResult> ExtractCanonicalAudioAsync(
        string sourcePath,
        string destFlacPath,
        CancellationToken cancellationToken,
        bool needsFloatWork = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destFlacPath);

        var args = needsFloatWork
            ? BuildFloatArgs(sourcePath, destFlacPath, _media.CpuThreads)
            : BuildCanonicalArgs(sourcePath, destFlacPath, _media.CpuThreads);

        var workDir = Path.GetDirectoryName(Path.GetFullPath(destFlacPath));
        if (string.IsNullOrWhiteSpace(workDir))
        {
            throw new DomainException("Destination path must include a directory.");
        }

        var result = await RunAsync(args, workDir, cancellationToken).ConfigureAwait(false);

        FileInfo output;
        try
        {
            output = new FileInfo(destFlacPath);
        }
        catch (Exception ex)
        {
            throw new ErrorCodeException(ErrorCodes.MediaCorrupt, "Canonical audio output is missing.", ex);
        }

        if (!output.Exists || output.Length < 1)
        {
            throw new ErrorCodeException(ErrorCodes.MediaCorrupt, "FFmpeg produced no canonical audio output.");
        }

        FfprobeResult probe;
        try
        {
            probe = await _ffprobe.ProbeAsync(destFlacPath, cancellationToken).ConfigureAwait(false);
        }
        catch (ErrorCodeException ex) when (string.Equals(ex.ErrorCode, ErrorCodes.MediaCorrupt, StringComparison.Ordinal))
        {
            throw new ErrorCodeException(ErrorCodes.MediaCorrupt, "Canonical audio output is undecodable.", ex);
        }

        if (!probe.Streams.Any(s => string.Equals(s.CodecType, "audio", StringComparison.OrdinalIgnoreCase)))
        {
            throw new ErrorCodeException(ErrorCodes.MediaCorrupt, "Canonical audio output has no audio stream.");
        }

        return result with { OutputPath = destFlacPath };
    }

    public async Task<FfmpegResult> ExtractSegmentSliceAsync(
        string sourcePath,
        string destWavPath,
        int startMs,
        int durationMs,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destWavPath);

        if (startMs < 0 || durationMs <= 0)
        {
            throw new ErrorCodeException(ErrorCodes.ValidationFailed, "Segment audio slice is empty; transcription cannot proceed.");
        }

        var args = BuildSliceArgs(
            sourcePath, destWavPath,
            startMs / 1000.0, durationMs / 1000.0,
            _media.CpuThreads);

        var workDir = Path.GetDirectoryName(Path.GetFullPath(destWavPath));
        if (string.IsNullOrWhiteSpace(workDir))
        {
            throw new DomainException("Destination path must include a directory.");
        }

        var result = await RunAsync(args, workDir, cancellationToken).ConfigureAwait(false);

        FileInfo output;
        try
        {
            output = new FileInfo(destWavPath);
        }
        catch (Exception ex)
        {
            throw new ErrorCodeException(ErrorCodes.MediaCorrupt, "Segment slice output is missing.", ex);
        }

        if (!output.Exists || output.Length < 1)
        {
            throw new ErrorCodeException(ErrorCodes.MediaCorrupt, "FFmpeg produced no segment slice output.");
        }

        return result with { OutputPath = destWavPath };
    }

    /// <summary>
    /// EBU R128 measure pass plus <c>astats</c>/<c>silencedetect</c> probes for
    /// quality control. All invocations use <c>ArgumentList</c> only (never
    /// shell) with the <c>Media:FfmpegTimeoutSec</c> timeout (single attempt;
    /// the QC stage maps failures to findings instead of retrying).
    /// Timeouts and disk failures throw <c>RESOURCE_EXHAUSTED</c>;
    /// non-zero exits and unparseable output throw
    /// <c>PROVIDER_INVALID_RESPONSE</c>. Only counts and levels are logged.
    /// </summary>
    public async Task<SignalLoudness> MeasureSignalLoudnessAsync(
        string file,
        string workDir,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(file);
        ArgumentException.ThrowIfNullOrWhiteSpace(workDir);

        var args = new List<string>
        {
            "-y", "-i", file,
            "-filter:a", "loudnorm=I=-16:TP=-1:LRA=11:print_format=json",
            "-f", "null", "-",
        };

        var stderr = await RunProbeAsync(args, workDir, cancellationToken).ConfigureAwait(false);
        return ParseLoudnormLevels(stderr);
    }

    public async Task<IReadOnlyList<double>> MeasureChannelRmsDbAsync(
        string file,
        string workDir,
        int? startMs,
        int? durationMs,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(file);
        ArgumentException.ThrowIfNullOrWhiteSpace(workDir);
        if (startMs.HasValue && startMs.Value < 0)
        {
            throw new DomainException("Probe start must be >= 0.");
        }

        if (durationMs.HasValue && durationMs.Value <= 0)
        {
            throw new DomainException("Probe duration must be > 0 when set.");
        }

        var args = new List<string> { "-y" };
        if (startMs.HasValue)
        {
            args.Add("-ss");
            args.Add((startMs.Value / 1000.0).ToString("F3", CultureInfo.InvariantCulture));
        }

        args.Add("-i");
        args.Add(file);
        if (durationMs.HasValue)
        {
            args.Add("-t");
            args.Add((durationMs.Value / 1000.0).ToString("F3", CultureInfo.InvariantCulture));
        }

        args.Add("-filter:a");
        args.Add("astats=metadata=1:reset=1");
        args.Add("-f");
        args.Add("null");
        args.Add("-");

        var stderr = await RunProbeAsync(args, workDir, cancellationToken).ConfigureAwait(false);
        var levels = ParseAstatsRms(stderr);
        if (levels.Count == 0)
        {
            throw new ErrorCodeException(ErrorCodes.ProviderInvalidResponse, "Signal level measurement produced no channel levels.");
        }

        return levels;
    }

    public async Task<double> MeasureMaxSilenceSecAsync(
        string file,
        string workDir,
        double silenceThresholdDb,
        double minSilenceSec,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(file);
        ArgumentException.ThrowIfNullOrWhiteSpace(workDir);
        if (double.IsNaN(silenceThresholdDb) || double.IsInfinity(silenceThresholdDb))
        {
            throw new DomainException("Silence threshold must be finite.");
        }

        if (double.IsNaN(minSilenceSec) || minSilenceSec <= 0.0)
        {
            throw new DomainException("Minimum silence duration must be > 0.");
        }

        var args = new List<string>
        {
            "-y", "-i", file,
            "-filter:a", string.Concat(
                "silencedetect=noise=",
                silenceThresholdDb.ToString("F1", CultureInfo.InvariantCulture),
                "dB:d=",
                minSilenceSec.ToString("F1", CultureInfo.InvariantCulture)),
            "-f", "null", "-",
        };

        var stderr = await RunProbeAsync(args, workDir, cancellationToken).ConfigureAwait(false);
        return ParseSilenceMax(stderr);
    }

    private async Task<string> RunProbeAsync(
        IReadOnlyList<string> args,
        string workDir,
        CancellationToken cancellationToken)
    {
        var timeout = TimeSpan.FromSeconds(Math.Max(1, _media.FfmpegTimeoutSec));
        ProcessResult captured;
        try
        {
            captured = await _runner.RunAsync("ffmpeg", args, workDir, timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            throw new ErrorCodeException(ErrorCodes.ResourceExhausted, "Signal measurement hit disk limits.", ex);
        }

        if (captured.TimedOut)
        {
            throw new ErrorCodeException(ErrorCodes.ResourceExhausted, "Signal measurement timed out.");
        }

        if (captured.ExitCode != 0)
        {
            throw new ErrorCodeException(
                ErrorCodes.ProviderInvalidResponse,
                string.Concat("Signal measurement failed with exit code ", captured.ExitCode.ToString(CultureInfo.InvariantCulture), "."));
        }

        return captured.StdErr ?? string.Empty;
    }

    private static SignalLoudness ParseLoudnormLevels(string stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr))
        {
            throw new ErrorCodeException(ErrorCodes.ProviderInvalidResponse, "Loudness measurement produced no output.");
        }

        var start = stderr.IndexOf('{');
        var end = stderr.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            throw new ErrorCodeException(ErrorCodes.ProviderInvalidResponse, "Loudness measurement produced no JSON.");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(stderr.Substring(start, end - start + 1));
        }
        catch (JsonException ex)
        {
            throw new ErrorCodeException(ErrorCodes.ProviderInvalidResponse, "Loudness measurement JSON is malformed.", ex);
        }

        using (document)
        {
            var root = document.RootElement;
            var integrated = ReadLevelNumber(root, "input_i");
            var truePeak = ReadLevelNumber(root, "input_tp");
            if (!integrated.HasValue || !truePeak.HasValue)
            {
                throw new ErrorCodeException(ErrorCodes.ProviderInvalidResponse, "Loudness measurement JSON is missing integrated/true-peak.");
            }

            return new SignalLoudness(integrated.Value, truePeak.Value);
        }
    }

    private static double? ReadLevelNumber(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var element))
        {
            return null;
        }

        if (element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out var number)
            && !double.IsNaN(number) && !double.IsInfinity(number))
        {
            return number;
        }

        if (element.ValueKind == JsonValueKind.String
            && double.TryParse((element.GetString() ?? string.Empty).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            && !double.IsNaN(parsed) && !double.IsInfinity(parsed))
        {
            return parsed;
        }

        return null;
    }

    private static IReadOnlyList<double> ParseAstatsRms(string stderr)
    {
        // astats prints one "Channel: N" header per channel followed by that
        // channel's "RMS level dB:" line, plus a trailing overall RMS line
        // with no channel header. Only channel-tagged values are returned so
        // stereo yields exactly 2 levels (the overall line is ignored).
        var levels = new List<double>();
        if (string.IsNullOrEmpty(stderr))
        {
            return levels;
        }

        const string channelMarker = "Channel:";
        const string rmsMarker = "RMS level dB:";
        int? pendingChannel = null;
        foreach (var line in stderr.Split('\n'))
        {
            var channelAt = line.IndexOf(channelMarker, StringComparison.Ordinal);
            if (channelAt >= 0)
            {
                var number = line.Substring(channelAt + channelMarker.Length).TrimStart();
                var digitEnd = 0;
                while (digitEnd < number.Length && char.IsAsciiDigit(number[digitEnd]))
                {
                    digitEnd++;
                }

                if (digitEnd > 0
                    && int.TryParse(number.Substring(0, digitEnd), NumberStyles.Integer, CultureInfo.InvariantCulture, out var channel)
                    && channel >= 1 && channel <= 64)
                {
                    pendingChannel = channel;
                }

                continue;
            }

            var rmsAt = line.IndexOf(rmsMarker, StringComparison.Ordinal);
            if (rmsAt < 0 || !pendingChannel.HasValue)
            {
                continue;
            }

            pendingChannel = null;
            var rest = line.Substring(rmsAt + rmsMarker.Length).TrimStart();
            var tokenEnd = rest.IndexOfAny([' ', '\t', '\r']);
            var token = (tokenEnd < 0 ? rest : rest.Substring(0, tokenEnd)).Trim();
            if (string.Equals(token, "-inf", StringComparison.OrdinalIgnoreCase)
                || string.Equals(token, "-infinity", StringComparison.OrdinalIgnoreCase))
            {
                levels.Add(double.NegativeInfinity);
            }
            else if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var level)
                && !double.IsNaN(level) && !double.IsInfinity(level))
            {
                levels.Add(level);
            }
        }

        return levels;
    }

    private static double ParseSilenceMax(string stderr)
    {
        var max = 0.0;
        if (string.IsNullOrEmpty(stderr))
        {
            return max;
        }

        const string marker = "silence_duration:";
        var index = 0;
        while (true)
        {
            var found = stderr.IndexOf(marker, index, StringComparison.Ordinal);
            if (found < 0)
            {
                break;
            }

            var rest = stderr.Substring(found + marker.Length).TrimStart();
            var tokenEnd = rest.IndexOfAny([' ', '\t', '\r', '\n', '|']);
            var token = (tokenEnd < 0 ? rest : rest.Substring(0, tokenEnd)).Trim();
            if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var duration)
                && !double.IsNaN(duration) && !double.IsInfinity(duration) && duration > max)
            {
                max = duration;
            }

            index = found + marker.Length;
        }

        return max;
    }

    private static FfmpegResult ToResult(ProcessResult result, IReadOnlyList<string> args)
    {
        if (result.ExitCode != 0)
        {
            var detail = string.IsNullOrWhiteSpace(result.StdErr)
                ? $"FFmpeg failed with exit code {result.ExitCode}."
                : $"FFmpeg failed with exit code {result.ExitCode}: {Truncate(result.StdErr, 500)}";
            throw new ErrorCodeException(ErrorCodes.InternalError, detail);
        }

        return new FfmpegResult(result.ExitCode, args.ToList(), result.Duration, false, string.Empty);
    }

    private static string Truncate(string value, int maxLength)
    {
        return value.Length <= maxLength ? value : value.Substring(0, maxLength);
    }
}
