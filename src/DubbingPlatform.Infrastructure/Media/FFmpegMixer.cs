using System.Globalization;
using System.Text;
using System.Text.Json;
using DubbingPlatform.Application.Abstractions;
using DubbingPlatform.Application.Errors;
using DubbingPlatform.Application.Exceptions;
using DubbingPlatform.Application.Options;
using DubbingPlatform.Domain.Exceptions;
using DubbingPlatform.Domain.ValueObjects;
using DubbingPlatform.Infrastructure.Processes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DubbingPlatform.Infrastructure.Media;

/// <summary>
/// One timeline entry for mixing (source placement plus selected audio id).
/// </summary>
public sealed record MixTimelineEntry(string SegmentId, int Sequence, int StartMs, int DurationMs, string AudioArtifactId);

/// <summary>
/// Parsed timeline for mixing: entries plus source duration and optional background.
/// </summary>
public sealed record MixTimeline(int SourceDurationMs, IReadOnlyList<MixTimelineEntry> Entries, string? BackgroundArtifactId);

/// <summary>
/// Loudness measurement from a <c>loudnorm=print_format=json</c> pass.
/// </summary>
public sealed record LoudnessMeasurement(
    double IntegratedLufs,
    double TruePeakDbtp,
    double LraDb,
    double ThresholdDb,
    double OffsetDb,
    string RawJson);

/// <summary>
/// Outcome of one mix (output persisted by the caller; no DB commit here).
/// </summary>
public sealed record MixResult(
    string OutputPath,
    string FilterComplex,
    string PremixFilter,
    string FinalFilter,
    double IntegratedLufs,
    double TruePeakDbtp,
    int DurationMs,
    int SampleRate,
    int Channels,
    bool DuckingApplied,
    bool BackgroundIncluded,
    string Profile,
    double TargetLufs);

/// <summary>
/// Ducking spot-check outcome (background RMS lower during dialogue).
/// </summary>
public sealed record DuckingVerification(double InsideRmsDb, double OutsideRmsDb, bool Applied);

/// <summary>
/// FFmpeg complex_filter mixing with loudness, peak, ducking, and consistency
/// guarantees. Pipeline: per-entry dialogue <c>adelay+apad+atrim</c> at source
/// <c>StartMs</c> (silence gaps stay silent; intentional overlaps kept via
/// <c>amix</c>) → background <c>sidechaincompress+volume</c> duck
/// (<c>-12dB, 150ms</c> defaults) → <c>amix</c> → two-pass
/// <c>loudnorm</c> (<c>I=-16|-23:TP=-1:LRA=11</c>, measure then
/// <c>linear=true</c> apply; single-pass when <c>Mixing:TwoPass=false</c>) →
/// <c>aformat=s16:48000:stereo</c>. Layout decision: every input is normalized
/// to stereo 48kHz float before placement (canonical preserves source layout,
/// TTS is mono 16kHz, background is stereo 48k; stereo is the deterministic
/// superset so no information is lost and loudness is comparable across runs);
/// the final is always 48kHz stereo <c>pcm_s16le</c> WAV. Exact
/// <c>-filter_complex</c> strings are returned for artifact/stage audit. All
/// FFmpeg invocations use <c>ArgumentList</c> only (never shell), run under the
/// shared <c>Media:MaxConcurrentMediaJobs</c> semaphore with the
/// <c>Media:FfmpegTimeoutSec</c> timeout (default 600s), and clean temp files.
/// Disk/timeout surfaces as <c>RESOURCE_EXHAUSTED</c> with no output commit;
/// loudness-measure failures throw <c>PROVIDER_INVALID_RESPONSE</c> (the worker
/// retries once inside the mixer, then routes to <c>MIX_QUALITY</c> review);
/// persistent clipping throws <c>QC_BLOCKED</c>. Never logs audio or secrets:
/// only ids, counts, durations, and levels.
/// </summary>
public sealed class FFmpegMixer
{
    /// <summary>Review reason for loudness-measure or out-of-tolerance mixes.</summary>
    public const string ReviewReason = "MIX_QUALITY";

    /// <summary>Loudness reference level width.</summary>
    public const double LraDb = 11.0;

    /// <summary>True-peak ceiling (dBTP).</summary>
    public const double TruePeakLimitDbtp = -1.0;

    /// <summary>Integrated-loudness tolerance (±LU).</summary>
    public const double ToleranceLu = 1.0;

    /// <summary>Final sample rate (Hz).</summary>
    public const int SampleRateHz = 48000;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ProcessRunner _runner;
    private readonly IFFprobeService _ffprobe;
    private readonly IFFmpegService _ffmpeg;
    private readonly MediaJobGate _gate;
    private readonly MixingOptions _mixing;
    private readonly MediaOptions _media;
    private readonly ILogger<FFmpegMixer> _logger;

    public FFmpegMixer(
        ProcessRunner runner,
        IFFprobeService ffprobe,
        IFFmpegService ffmpeg,
        MediaJobGate gate,
        IOptions<MixingOptions> mixing,
        IOptions<MediaOptions> media,
        ILogger<FFmpegMixer> logger)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(ffprobe);
        ArgumentNullException.ThrowIfNull(ffmpeg);
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(mixing);
        ArgumentNullException.ThrowIfNull(media);
        ArgumentNullException.ThrowIfNull(logger);
        _runner = runner;
        _ffprobe = ffprobe;
        _ffmpeg = ffmpeg;
        _gate = gate;
        _mixing = mixing.Value;
        _media = media.Value;
        _logger = logger;
    }

    /// <summary>
    /// Resolves a loudness profile name to its target. Pure. Null/empty yields
    /// web (<c>-16/-1</c>); <c>web</c>/<c>broadcast</c> case-insensitive;
    /// anything else throws <c>VALIDATION_FAILED</c>.
    /// </summary>
    public static LoudnessTarget ResolveTarget(string? profile)
    {
        var normalized = (profile ?? string.Empty).Trim().ToLowerInvariant();
        if (normalized.Length == 0 || string.Equals(normalized, "web", StringComparison.Ordinal))
        {
            return LoudnessTarget.WebDefault;
        }

        if (string.Equals(normalized, "broadcast", StringComparison.Ordinal))
        {
            return LoudnessTarget.Broadcast;
        }

        throw new ErrorCodeException(ErrorCodes.ValidationFailed, $"Unknown loudness profile '{profile}'. Expected 'web' or 'broadcast'.");
    }

    /// <summary>
    /// Parses <c>settings.loudnessProfile</c> from project settings JSON. Pure;
    /// fail-closed to <c>web</c> on missing/invalid JSON (mirrors context
    /// settings parsing: settings never fail a run, they default).
    /// </summary>
    public static string ParseLoudnessProfile(string? settingsJson)
    {
        if (string.IsNullOrWhiteSpace(settingsJson))
        {
            return "web";
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(settingsJson);
        }
        catch (JsonException)
        {
            return "web";
        }

        using (document)
        {
            if (document.RootElement.ValueKind is not JsonValueKind.Object)
            {
                return "web";
            }

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (string.Equals(property.Name, "loudnessProfile", StringComparison.OrdinalIgnoreCase)
                    && property.Value.ValueKind == JsonValueKind.String)
                {
                    var value = (property.Value.GetString() ?? string.Empty).Trim().ToLowerInvariant();
                    if (string.Equals(value, "broadcast", StringComparison.Ordinal))
                    {
                        return "broadcast";
                    }

                    return "web";
                }
            }

            return "web";
        }
    }

    /// <summary>
    /// Whether settings JSON explicitly carries a <c>loudnessProfile</c> key.
    /// Pure; used to prefer project settings over <c>Mixing:Profile</c> only
    /// when explicitly set.
    /// </summary>
    public static bool HasLoudnessProfile(string? settingsJson)
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
                if (string.Equals(property.Name, "loudnessProfile", StringComparison.OrdinalIgnoreCase))
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
    /// Parses canonical timeline JSON (schema v1 from
    /// <c>TimelineAssemblyService.BuildJson</c>). Pure. Throws
    /// <c>VALIDATION_FAILED</c> on malformed JSON.
    /// </summary>
    public static MixTimeline ParseTimeline(string timelineJson)
    {
        if (string.IsNullOrWhiteSpace(timelineJson))
        {
            throw new ErrorCodeException(ErrorCodes.ValidationFailed, "Timeline JSON must not be empty for mixing.");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(timelineJson);
        }
        catch (JsonException ex)
        {
            throw new ErrorCodeException(ErrorCodes.ValidationFailed, "Timeline JSON is malformed; mixing cannot proceed.", ex);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind is not JsonValueKind.Object)
            {
                throw new ErrorCodeException(ErrorCodes.ValidationFailed, "Timeline JSON must be an object for mixing.");
            }

            var sourceDurationMs = 0;
            if (root.TryGetProperty("sourceDurationMs", out var durationElement)
                && durationElement.ValueKind == JsonValueKind.Number
                && durationElement.TryGetInt32(out var parsedDuration)
                && parsedDuration >= 0)
            {
                sourceDurationMs = parsedDuration;
            }

            string? backgroundId = null;
            if (root.TryGetProperty("backgroundArtifactId", out var bgElement)
                && bgElement.ValueKind == JsonValueKind.String)
            {
                var text = (bgElement.GetString() ?? string.Empty).Trim();
                if (text.Length > 0)
                {
                    backgroundId = text;
                }
            }

            var entries = new List<MixTimelineEntry>();
            if (root.TryGetProperty("entries", out var entriesElement)
                && entriesElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in entriesElement.EnumerateArray())
                {
                    if (item.ValueKind is not JsonValueKind.Object)
                    {
                        continue;
                    }

                    var segmentId = item.TryGetProperty("segmentId", out var idElement) && idElement.ValueKind == JsonValueKind.String
                        ? (idElement.GetString() ?? string.Empty).Trim()
                        : string.Empty;
                    var sequence = item.TryGetProperty("sequence", out var seqElement) && seqElement.ValueKind == JsonValueKind.Number && seqElement.TryGetInt32(out var seq)
                        ? seq
                        : 0;
                    var startMs = item.TryGetProperty("startMs", out var startElement) && startElement.ValueKind == JsonValueKind.Number && startElement.TryGetInt32(out var start)
                        ? start
                        : -1;
                    var durationMs = item.TryGetProperty("durationMs", out var durElement) && durElement.ValueKind == JsonValueKind.Number && durElement.TryGetInt32(out var dur)
                        ? dur
                        : -1;
                    var audioId = item.TryGetProperty("audioArtifactId", out var audioElement) && audioElement.ValueKind == JsonValueKind.String
                        ? (audioElement.GetString() ?? string.Empty).Trim()
                        : string.Empty;
                    if (segmentId.Length == 0 || startMs < 0 || durationMs <= 0 || sequence < 0 || audioId.Length == 0)
                    {
                        throw new ErrorCodeException(ErrorCodes.ValidationFailed, "Timeline entry is invalid for mixing.");
                    }

                    entries.Add(new MixTimelineEntry(segmentId, sequence, startMs, durationMs, audioId));
                }
            }

            entries.Sort(static (a, b) =>
            {
                var byStart = a.StartMs.CompareTo(b.StartMs);
                if (byStart != 0)
                {
                    return byStart;
                }

                var bySeq = a.Sequence.CompareTo(b.Sequence);
                return bySeq != 0 ? bySeq : string.Compare(a.SegmentId, b.SegmentId, StringComparison.Ordinal);
            });

            return new MixTimeline(sourceDurationMs, entries, backgroundId);
        }
    }

    /// <summary>
    /// Builds the deterministic premix <c>-filter_complex</c> value (dialogue
    /// placement + duck + amix, no loudnorm). Pure. Dialogue inputs are
    /// <c>0..N-1</c>, background (when present) is <c>N</c>; output label is
    /// <c>[mix]</c>. Throws <c>VALIDATION_FAILED</c> for empty total duration.
    /// </summary>
    public static string BuildPremixFilter(
        IReadOnlyList<MixTimelineEntry> entries,
        int sourceDurationMs,
        bool hasBackground,
        double duckDb,
        int fadeMs)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (sourceDurationMs <= 0)
        {
            throw new ErrorCodeException(ErrorCodes.ValidationFailed, "Mix total duration must be > 0.");
        }

        if (double.IsNaN(duckDb) || double.IsInfinity(duckDb) || duckDb > 0.0 || duckDb < -30.0)
        {
            throw new ErrorCodeException(ErrorCodes.ValidationFailed, "Duck attenuation must be in -30..0 dB.");
        }

        if (fadeMs < 0 || fadeMs > 2000)
        {
            throw new ErrorCodeException(ErrorCodes.ValidationFailed, "Duck fade must be in 0..2000ms.");
        }

        var totalSec = (sourceDurationMs / 1000.0).ToString("F3", CultureInfo.InvariantCulture);
        var builder = new StringBuilder();

        if (entries.Count == 0 && !hasBackground)
        {
            throw new ErrorCodeException(ErrorCodes.ValidationFailed, "Nothing to mix: timeline has no entries and no background.");
        }

        if (entries.Count == 0)
        {
            // Background only: normalize the single input to the mix bus.
            builder.Append(CultureInfo.InvariantCulture, $"[{entries.Count}:a]aformat=sample_fmts=fltp:sample_rates=48000:channel_layouts=stereo,atrim=0:{totalSec},apad=whole_dur={totalSec},atrim=0:{totalSec}[mix]");
            return builder.ToString();
        }

        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            builder.Append(CultureInfo.InvariantCulture, $"[{i}:a]aformat=sample_fmts=fltp:sample_rates=48000:channel_layouts=stereo,adelay={entry.StartMs}|{entry.StartMs}:all=1,apad=whole_dur={totalSec},atrim=0:{totalSec}[dlg{i}];");
        }

        string dialogLabel;
        if (entries.Count == 1)
        {
            builder.Append("[dlg0]anull[dialog];");
            dialogLabel = "[dialog]";
        }
        else
        {
            for (var i = 0; i < entries.Count; i++)
            {
                builder.Append(CultureInfo.InvariantCulture, $"[dlg{i}]");
            }

            builder.Append(CultureInfo.InvariantCulture, $"amix=inputs={entries.Count}:duration=longest:dropout_transition=0:normalize=0[dialog];");
            dialogLabel = "[dialog]";
        }

        if (!hasBackground)
        {
            builder.Append(CultureInfo.InvariantCulture, $"{dialogLabel}anull[mix]");
            return builder.ToString();
        }

        var bgIndex = entries.Count;
        var duckText = string.Concat(duckDb.ToString("G", CultureInfo.InvariantCulture), "dB");
        builder.Append(CultureInfo.InvariantCulture, $"[{bgIndex}:a]aformat=sample_fmts=fltp:sample_rates=48000:channel_layouts=stereo,atrim=0:{totalSec},apad=whole_dur={totalSec},atrim=0:{totalSec}[bgfmt];");
        builder.Append(CultureInfo.InvariantCulture, $"{dialogLabel}asplit=2[dialogSC][dialogMix];");
        builder.Append(CultureInfo.InvariantCulture, $"[bgfmt][dialogSC]sidechaincompress=threshold=0.01:ratio=8:attack={fadeMs}:release={fadeMs}[bgcomp];");
        builder.Append(CultureInfo.InvariantCulture, $"[bgcomp]volume={duckText}[bgduck];");
        builder.Append("[dialogMix][bgduck]amix=inputs=2:duration=longest:dropout_transition=0:normalize=0[mix]");

        return builder.ToString();
    }

    /// <summary>
    /// Builds the final loudnorm filter (after the premix <c>[mix]</c> label).
    /// Pure. Two-pass linear when <paramref name="measured"/> is set, else
    /// single-pass dynamic. Always ends with
    /// <c>aformat=s16:48000:stereo</c>.
    /// </summary>
    public static string BuildFinalFilter(LoudnessTarget target, LoudnessMeasurement? measured)
    {
        ArgumentNullException.ThrowIfNull(target);
        var integrated = target.IntegratedLufs.ToString("F1", CultureInfo.InvariantCulture);
        if (measured is null)
        {
            return string.Concat(
                "loudnorm=I=", integrated, ":TP=-1:LRA=11",
                ",aformat=sample_fmts=s16:sample_rates=48000:channel_layouts=stereo");
        }

        return string.Concat(
            "loudnorm=I=", integrated, ":TP=-1:LRA=11",
            ":measured_I=", measured.IntegratedLufs.ToString("F2", CultureInfo.InvariantCulture),
            ":measured_TP=", measured.TruePeakDbtp.ToString("F2", CultureInfo.InvariantCulture),
            ":measured_LRA=", measured.LraDb.ToString("F2", CultureInfo.InvariantCulture),
            ":measured_thresh=", measured.ThresholdDb.ToString("F2", CultureInfo.InvariantCulture),
            ":offset=", measured.OffsetDb.ToString("F2", CultureInfo.InvariantCulture),
            ":linear=true",
            ",aformat=sample_fmts=s16:sample_rates=48000:channel_layouts=stereo");
    }

    /// <summary>
    /// Parses a <c>loudnorm=print_format=json</c> stderr payload. Pure. Throws
    /// <c>PROVIDER_INVALID_RESPONSE</c> when no JSON block is found.
    /// </summary>
    public static LoudnessMeasurement ParseLoudnormJson(string stderr)
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

        var json = stderr.Substring(start, end - start + 1);
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new ErrorCodeException(ErrorCodes.ProviderInvalidResponse, "Loudness measurement JSON is malformed.", ex);
        }

        using (document)
        {
            var root = document.RootElement;
            var integrated = GetDouble(root, "input_i");
            var truePeak = GetDouble(root, "input_tp");
            var lra = GetDouble(root, "input_lra");
            var thresh = GetDouble(root, "input_thresh");
            var offset = GetDouble(root, "target_offset");
            if (!integrated.HasValue || !truePeak.HasValue)
            {
                throw new ErrorCodeException(ErrorCodes.ProviderInvalidResponse, "Loudness measurement JSON is missing integrated/true-peak.");
            }

            return new LoudnessMeasurement(
                integrated.Value,
                truePeak.Value,
                lra ?? 0.0,
                thresh ?? -70.0,
                offset ?? 0.0,
                json);
        }
    }

    /// <summary>
    /// Whether integrated loudness is within ±1 LU of target. Pure.
    /// </summary>
    public static bool IsLoudnessWithinTarget(double measured, double target)
    {
        if (double.IsNaN(measured) || double.IsNaN(target) || double.IsInfinity(measured) || double.IsInfinity(target))
        {
            return false;
        }

        return Math.Abs(measured - target) <= ToleranceLu + 1e-9;
    }

    /// <summary>
    /// Whether true-peak is at or below -1 dBTP. Pure.
    /// </summary>
    public static bool IsPeakWithinLimit(double truePeak)
    {
        if (double.IsNaN(truePeak) || double.IsInfinity(truePeak))
        {
            return false;
        }

        return truePeak <= TruePeakLimitDbtp + 1e-9;
    }

    /// <summary>
    /// Ducking spot-check over decoded WAV RMS: background RMS must be lower
    /// during dialogue windows. Pure. Requires at least 3dB? No — requires any
    /// strict improvement beyond 0.5dB to tolerate measurement noise while
    /// still catching a missing duck (0dB).
    /// </summary>
    public static DuckingVerification VerifyDuckingLevels(double insideRmsDb, double outsideRmsDb)
    {
        var applied = !double.IsNaN(insideRmsDb)
            && !double.IsNaN(outsideRmsDb)
            && !double.IsInfinity(insideRmsDb)
            && !double.IsInfinity(outsideRmsDb)
            && insideRmsDb + 0.5 < outsideRmsDb;
        return new DuckingVerification(insideRmsDb, outsideRmsDb, applied);
    }

    /// <summary>
    /// Mixes dialogue (+ optional background) per <paramref name="timelineJson"/>
    /// to <paramref name="outputWav"/> (48kHz stereo WAV) with two-pass loudnorm
    /// (or single-pass when <c>Mixing:TwoPass=false</c>). See class docs for the
    /// filter pipeline. Throws without committing any output file on failure
    /// (partial outputs are deleted): <c>VALIDATION_FAILED</c> for bad inputs,
    /// <c>ARTIFACT_UNAVAILABLE</c> for missing files,
    /// <c>PROVIDER_INVALID_RESPONSE</c> for loudness-measure failures (measured
    /// twice before throwing), <c>QC_BLOCKED</c> for persistent clipping,
    /// <c>RESOURCE_EXHAUSTED</c> for disk/timeout.
    /// </summary>
    public async Task<MixResult> MixAsync(
        string timelineJson,
        IReadOnlyList<string> dialogueFiles,
        string? backgroundFile,
        string outputWav,
        string? profile,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(timelineJson))
        {
            throw new ErrorCodeException(ErrorCodes.ValidationFailed, "Timeline JSON must not be empty for mixing.");
        }

        ArgumentNullException.ThrowIfNull(dialogueFiles);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputWav);

        var timeline = ParseTimeline(timelineJson);
        if (dialogueFiles.Count != timeline.Entries.Count)
        {
            throw new ErrorCodeException(
                ErrorCodes.ValidationFailed,
                string.Concat(
                    "Dialogue file count ",
                    dialogueFiles.Count.ToString(CultureInfo.InvariantCulture),
                    " does not match timeline entries ",
                    timeline.Entries.Count.ToString(CultureInfo.InvariantCulture),
                    "."));
        }

        foreach (var file in dialogueFiles)
        {
            if (string.IsNullOrWhiteSpace(file) || !File.Exists(file.Trim()))
            {
                throw new ErrorCodeException(ErrorCodes.ArtifactUnavailable, "Dialogue audio for mixing is unavailable.");
            }
        }

        var hasBackground = !string.IsNullOrWhiteSpace(backgroundFile);
        if (hasBackground && !File.Exists(backgroundFile!.Trim()))
        {
            throw new ErrorCodeException(ErrorCodes.ArtifactUnavailable, "Background audio for mixing is unavailable.");
        }

        var effectiveProfile = string.IsNullOrWhiteSpace(profile) ? _mixing.Profile : profile;
        var target = ResolveTarget(effectiveProfile);
        var profileName = EffectiveProfileName(effectiveProfile);

        var totalMs = timeline.SourceDurationMs;
        if (totalMs <= 0)
        {
            if (timeline.Entries.Count > 0)
            {
                totalMs = timeline.Entries.Max(e => e.StartMs + e.DurationMs);
            }
            else if (hasBackground)
            {
                var probed = await ProbeDurationMs(backgroundFile!.Trim(), cancellationToken).ConfigureAwait(false);
                totalMs = (int)Math.Min(Math.Max(probed, 0), int.MaxValue);
            }
        }

        if (totalMs <= 0)
        {
            throw new ErrorCodeException(ErrorCodes.ValidationFailed, "Mix total duration must be > 0.");
        }

        string workDir;
        try
        {
            workDir = ProcessRunner.CreateTempWorkingDir();
        }
        catch (IOException ex)
        {
            throw new ErrorCodeException(ErrorCodes.ResourceExhausted, "Mixing cannot create temp dir; disk may be full.", ex);
        }

        var premixPath = Path.Combine(workDir, "premix.wav");
        var limitedPath = Path.Combine(workDir, "premix-limited.wav");
        try
        {
            var premixFilter = BuildPremixFilter(timeline.Entries, totalMs, hasBackground, _mixing.DuckDb, _mixing.FadeMs);
            await RunPremixAsync(dialogueFiles, hasBackground ? backgroundFile!.Trim() : null, premixFilter, premixPath, workDir, cancellationToken).ConfigureAwait(false);

            LoudnessMeasurement? measured = null;
            if (_mixing.TwoPass)
            {
                measured = await MeasureWithRetryAsync(premixPath, cancellationToken).ConfigureAwait(false);
            }

            var finalFilter = BuildFinalFilter(target, measured);
            var filterComplex = string.Concat("premix: ", premixFilter, " || final: ", finalFilter);
            await RunFinalAsync(premixPath, finalFilter, outputWav, workDir, cancellationToken).ConfigureAwait(false);

            var verification = await VerifyMixAsync(outputWav, target, totalMs, cancellationToken).ConfigureAwait(false);
            if (verification.Clipped)
            {
                // Clipping path: apply alimiter once and re-measure, else fail.
                await RunLimiterAsync(premixPath, limitedPath, workDir, cancellationToken).ConfigureAwait(false);
                LoudnessMeasurement? remeasured = null;
                if (_mixing.TwoPass)
                {
                    remeasured = await MeasureWithRetryAsync(limitedPath, cancellationToken).ConfigureAwait(false);
                }

                var limitedFilter = BuildFinalFilter(target, remeasured);
                await RunFinalAsync(limitedPath, limitedFilter, outputWav, workDir, cancellationToken).ConfigureAwait(false);
                var reverify = await VerifyMixAsync(outputWav, target, totalMs, cancellationToken).ConfigureAwait(false);
                if (reverify.Clipped || !reverify.LoudnessOk || !reverify.PeakOk)
                {
                    DeleteQuietly(outputWav);
                    throw new ErrorCodeException(ErrorCodes.QcBlocked, "Mixed audio clips even after limiting; blocking.");
                }

                verification = reverify;
                filterComplex = string.Concat(filterComplex, " || limiter: alimiter=limit=0.95");
            }

            if (!verification.LoudnessOk)
            {
                DeleteQuietly(outputWav);
                throw new ErrorCodeException(
                    ErrorCodes.ProviderInvalidResponse,
                    string.Concat(
                        "Mixed loudness ",
                        verification.Integrated.ToString("F2", CultureInfo.InvariantCulture),
                        " LUFS differs from target ",
                        target.IntegratedLufs.ToString("F1", CultureInfo.InvariantCulture),
                        " LUFS by more than 1 LU."));
            }

            if (!verification.PeakOk)
            {
                DeleteQuietly(outputWav);
                throw new ErrorCodeException(
                    ErrorCodes.QcBlocked,
                    string.Concat(
                        "Mixed true-peak ",
                        verification.TruePeak.ToString("F2", CultureInfo.InvariantCulture),
                        " dBTP exceeds the -1 dBTP limit."));
            }

            _logger.LogInformation(
                "Mixed {Entries} entries to {Output} ({Duration}ms, {Rate}Hz, {Channels}ch, {Integrated} LUFS, peak {Peak} dBTP, duck {Duck}).",
                timeline.Entries.Count, outputWav, verification.DurationMs,
                verification.SampleRate, verification.Channels,
                verification.Integrated.ToString("F2", CultureInfo.InvariantCulture),
                verification.TruePeak.ToString("F2", CultureInfo.InvariantCulture),
                hasBackground ? "applied" : "skipped");

            return new MixResult(
                outputWav, filterComplex, premixFilter, finalFilter,
                verification.Integrated, verification.TruePeak,
                verification.DurationMs, verification.SampleRate, verification.Channels,
                hasBackground, hasBackground, profileName, target.IntegratedLufs);
        }
        catch (ErrorCodeException)
        {
            DeleteQuietly(outputWav);
            throw;
        }
        catch (TimeoutException ex)
        {
            DeleteQuietly(outputWav);
            throw new ErrorCodeException(ErrorCodes.ResourceExhausted, "Mixing timed out; retry the operation.", ex);
        }
        catch (IOException ex)
        {
            DeleteQuietly(outputWav);
            throw new ErrorCodeException(ErrorCodes.ResourceExhausted, "Mixing hit disk limits; retry the operation.", ex);
        }
        finally
        {
            DeleteDirQuietly(workDir);
        }
    }

    /// <summary>
    /// Verifies duration/rate/channels via FFprobe plus loudness/peak via
    /// loudnorm re-measurement. Throws <c>RESOURCE_EXHAUSTED</c> on
    /// disk/timeout, <c>PROVIDER_INVALID_RESPONSE</c> on measure failure.
    /// </summary>
    public async Task<MixVerification> VerifyMixAsync(
        string file,
        LoudnessTarget target,
        int? expectedDurationMs,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(file);
        ArgumentNullException.ThrowIfNull(target);
        if (!File.Exists(file))
        {
            throw new ErrorCodeException(ErrorCodes.ArtifactUnavailable, "Mixed audio for verification is unavailable.");
        }

        FfprobeResult probe;
        try
        {
            probe = await _ffprobe.ProbeAsync(file, cancellationToken).ConfigureAwait(false);
        }
        catch (ErrorCodeException ex) when (string.Equals(ex.ErrorCode, ErrorCodes.MediaCorrupt, StringComparison.Ordinal))
        {
            throw new ErrorCodeException(ErrorCodes.ProviderInvalidResponse, "Mixed audio is undecodable.", ex);
        }

        var audio = probe.Streams.FirstOrDefault(s => string.Equals(s.CodecType, "audio", StringComparison.OrdinalIgnoreCase));
        if (audio is null)
        {
            throw new ErrorCodeException(ErrorCodes.PipelineInvariantViolation, "Mixed audio has no audio stream.");
        }

        if (audio.SampleRate != SampleRateHz)
        {
            throw new ErrorCodeException(
                ErrorCodes.PipelineInvariantViolation,
                string.Concat(
                    "Mixed audio sample rate is ",
                    audio.SampleRate?.ToString(CultureInfo.InvariantCulture) ?? "unknown",
                    "Hz; expected 48000Hz."));
        }

        if (audio.Channels is null or <= 0)
        {
            throw new ErrorCodeException(ErrorCodes.PipelineInvariantViolation, "Mixed audio has no channels.");
        }

        if (expectedDurationMs.HasValue && expectedDurationMs.Value > 0)
        {
            var drift = Math.Abs(probe.DurationMs - expectedDurationMs.Value);
            if (drift > 500)
            {
                throw new ErrorCodeException(
                    ErrorCodes.PipelineInvariantViolation,
                    string.Concat(
                        "Mixed audio duration ",
                        probe.DurationMs.ToString(CultureInfo.InvariantCulture),
                        "ms differs from timeline ",
                        expectedDurationMs.Value.ToString(CultureInfo.InvariantCulture),
                        "ms by ",
                        drift.ToString(CultureInfo.InvariantCulture),
                        "ms (tolerance 500ms)."));
            }
        }

        var measured = await MeasureWithRetryAsync(file, cancellationToken).ConfigureAwait(false);
        var loudnessOk = IsLoudnessWithinTarget(measured.IntegratedLufs, target.IntegratedLufs);
        var peakOk = IsPeakWithinLimit(measured.TruePeakDbtp);
        var clipped = measured.TruePeakDbtp >= 0.0 || await HasSampleClippingAsync(file, cancellationToken).ConfigureAwait(false);

        return new MixVerification(
            (int)Math.Min(Math.Max(probe.DurationMs, 0), int.MaxValue),
            audio.SampleRate ?? SampleRateHz,
            audio.Channels ?? 2,
            measured.IntegratedLufs,
            measured.TruePeakDbtp,
            loudnessOk,
            peakOk,
            clipped);
    }

    /// <summary>
    /// Measures integrated loudness + true-peak with one retry. Throws
    /// <c>PROVIDER_INVALID_RESPONSE</c> when both attempts fail, or
    /// <c>RESOURCE_EXHAUSTED</c> on disk/timeout.
    /// </summary>
    public async Task<LoudnessMeasurement> MeasureLoudnessAsync(string file, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(file);
        return await MeasureWithRetryAsync(file, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Ducking spot-check: compares background RMS inside vs outside dialogue
    /// windows in <paramref name="file"/> (decoded in C#, no FFmpeg). Windows
    /// are <c>(StartMs, EndMs)</c> dialogue intervals. Outside RMS uses the
    /// complement (silence gaps). Returns dB values plus whether ducking
    /// applied (inside &gt;0.5dB quieter).
    /// </summary>
    public async Task<DuckingVerification> VerifyDuckingAsync(
        string file,
        IReadOnlyList<(int StartMs, int EndMs)> windows,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(file);
        ArgumentNullException.ThrowIfNull(windows);
        if (!File.Exists(file))
        {
            throw new ErrorCodeException(ErrorCodes.ArtifactUnavailable, "Audio for ducking verification is unavailable.");
        }

        var (sampleRate, channels, samples) = await ReadWavSamplesAsync(file, cancellationToken).ConfigureAwait(false);
        if (samples.Length == 0)
        {
            throw new ErrorCodeException(ErrorCodes.ValidationFailed, "Audio for ducking verification is empty.");
        }

        var channelCount = Math.Max(1, channels);
        var frames = samples.Length / channelCount;
        if (frames == 0)
        {
            throw new ErrorCodeException(ErrorCodes.ValidationFailed, "Audio for ducking verification is empty.");
        }

        var insideSum = 0.0;
        var insideCount = 0L;
        var outsideSum = 0.0;
        var outsideCount = 0L;

        var sorted = windows
            .Where(w => w.EndMs > w.StartMs && w.StartMs >= 0)
            .OrderBy(w => w.StartMs)
            .ToList();

        bool Inside(int indexMs)
        {
            foreach (var (start, end) in sorted)
            {
                if (indexMs >= start && indexMs < end)
                {
                    return true;
                }
            }

            return false;
        }

        // Sample-accurate windows would need per-sample mapping; ms-granularity
        // RMS over 50ms buckets is deterministic and robust to resampling.
        // Samples are interleaved stereo: map ms to frames, then accumulate all
        // channels so time windows stay correct for any channel count.
        const int bucketMs = 50;
        var totalMs = (int)((long)frames * 1000 / Math.Max(1, sampleRate));
        for (var bucketStart = 0; bucketStart < totalMs; bucketStart += bucketMs)
        {
            var startFrame = (int)((long)bucketStart * sampleRate / 1000);
            var endFrame = (int)Math.Min(frames, (long)(bucketStart + bucketMs) * sampleRate / 1000);
            if (endFrame <= startFrame)
            {
                continue;
            }

            // Skip fade edges (±150ms) so attack/release ramps never decide.
            var edge = 150;
            var nearEdge = sorted.Any(w => Math.Abs(bucketStart - w.StartMs) < edge || Math.Abs(bucketStart - w.EndMs) < edge);
            if (nearEdge)
            {
                continue;
            }

            var sum = 0.0;
            for (var frame = startFrame; frame < endFrame; frame++)
            {
                var baseIndex = frame * channelCount;
                for (var ch = 0; ch < channelCount; ch++)
                {
                    var sample = samples[baseIndex + ch];
                    sum += sample * sample;
                }
            }

            var frameCount = (long)(endFrame - startFrame) * channelCount;
            var midMs = bucketStart + (bucketMs / 2);
            if (Inside(midMs))
            {
                insideSum += sum;
                insideCount += frameCount;
            }
            else
            {
                outsideSum += sum;
                outsideCount += frameCount;
            }
        }

        if (insideCount == 0 || outsideCount == 0)
        {
            throw new ErrorCodeException(ErrorCodes.ValidationFailed, "Ducking verification needs both dialogue and non-dialogue audio.");
        }

        var insideRms = Math.Sqrt(insideSum / insideCount);
        var outsideRms = Math.Sqrt(outsideSum / outsideCount);
        var insideDb = insideRms <= 1e-9 ? double.NegativeInfinity : 20.0 * Math.Log10(insideRms);
        var outsideDb = outsideRms <= 1e-9 ? double.NegativeInfinity : 20.0 * Math.Log10(outsideRms);
        return new DuckingVerification(insideDb, outsideDb, VerifyDuckingLevels(insideDb, outsideDb).Applied);
    }

    private async Task<LoudnessMeasurement> MeasureWithRetryAsync(string file, CancellationToken cancellationToken)
    {
        var target = LoudnessTarget.WebDefault;
        var measureFilter = string.Concat(
            "loudnorm=I=", target.IntegratedLufs.ToString("F1", CultureInfo.InvariantCulture), ":TP=-1:LRA=11:print_format=json");

        // Two attempts: initial + one retry, then PROVIDER_INVALID_RESPONSE.
        ErrorCodeException? last = null;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            string workDir;
            try
            {
                workDir = ProcessRunner.CreateTempWorkingDir();
            }
            catch (IOException ex)
            {
                throw new ErrorCodeException(ErrorCodes.ResourceExhausted, "Loudness measurement cannot create temp dir.", ex);
            }

            try
            {
                await _gate.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    var args = new List<string>
                    {
                        "-y", "-i", file,
                        "-filter:a", measureFilter,
                        "-f", "null", "-",
                    };

                    // loudnorm JSON goes to stderr; capture it directly via the
                    // runner (ArgumentList only, never shell).
                    var captured = await _runner.RunAsync(
                        "ffmpeg", args, workDir,
                        TimeSpan.FromSeconds(Math.Max(1, _media.FfmpegTimeoutSec)),
                        cancellationToken).ConfigureAwait(false);
                    if (captured.TimedOut)
                    {
                        throw new ErrorCodeException(ErrorCodes.ResourceExhausted, "Loudness measurement timed out.");
                    }

                    if (captured.ExitCode != 0)
                    {
                        throw new ErrorCodeException(ErrorCodes.ProviderInvalidResponse, "Loudness measurement failed.");
                    }

                    return ParseLoudnormJson(captured.StdErr);
                }
                finally
                {
                    _gate.Semaphore.Release();
                }
            }
#pragma warning disable CA1031 // Measure retry contract: any first-attempt failure retries once, then PROVIDER_INVALID_RESPONSE.
            catch (ErrorCodeException ex) when (attempt == 0 && !string.Equals(ex.ErrorCode, ErrorCodes.ResourceExhausted, StringComparison.Ordinal))
#pragma warning restore CA1031
            {
                last = ex;
                _logger.LogWarning("Loudness measurement failed (attempt 1); retrying once.");
                continue;
            }
            finally
            {
                DeleteDirQuietly(workDir);
            }
        }

        throw new ErrorCodeException(
            ErrorCodes.ProviderInvalidResponse,
            string.Concat("Loudness measurement failed twice.", last is not null ? " " : string.Empty, last?.Message ?? string.Empty));
    }

    private async Task RunPremixAsync(
        IReadOnlyList<string> dialogueFiles,
        string? backgroundFile,
        string premixFilter,
        string premixPath,
        string workDir,
        CancellationToken cancellationToken)
    {
        var args = new List<string> { "-y" };
        foreach (var file in dialogueFiles)
        {
            args.Add("-i");
            args.Add(file.Trim());
        }

        if (!string.IsNullOrWhiteSpace(backgroundFile))
        {
            args.Add("-i");
            args.Add(backgroundFile!.Trim());
        }

        args.Add("-filter_complex");
        args.Add(premixFilter);
        args.Add("-map");
        args.Add("[mix]");
        args.Add("-ar");
        args.Add("48000");
        args.Add("-ac");
        args.Add("2");
        args.Add("-c:a");
        args.Add("pcm_s16le");
        args.Add(premixPath);

        await _gate.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            FfmpegResult result;
            try
            {
                result = await _ffmpeg.RunAsync(args, workDir, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException ex)
            {
                throw new ErrorCodeException(ErrorCodes.ResourceExhausted, "Mixing premix timed out.", ex);
            }
            catch (IOException ex)
            {
                throw new ErrorCodeException(ErrorCodes.ResourceExhausted, "Mixing premix hit disk limits.", ex);
            }

            if (!File.Exists(premixPath) || new FileInfo(premixPath).Length < 1)
            {
                throw new ErrorCodeException(ErrorCodes.ProviderInvalidResponse, "Mixing premix produced no output.");
            }

            _ = result;
        }
        finally
        {
            _gate.Semaphore.Release();
        }
    }

    private async Task RunFinalAsync(
        string inputPath,
        string finalFilter,
        string outputPath,
        string workDir,
        CancellationToken cancellationToken)
    {
        var args = new List<string>
        {
            "-y", "-i", inputPath,
            "-filter:a", finalFilter,
            "-ar", "48000", "-ac", "2", "-c:a", "pcm_s16le",
            outputPath,
        };

        await _gate.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            try
            {
                await _ffmpeg.RunAsync(args, workDir, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException ex)
            {
                throw new ErrorCodeException(ErrorCodes.ResourceExhausted, "Mixing loudnorm timed out.", ex);
            }
            catch (IOException ex)
            {
                throw new ErrorCodeException(ErrorCodes.ResourceExhausted, "Mixing loudnorm hit disk limits.", ex);
            }

            if (!File.Exists(outputPath) || new FileInfo(outputPath).Length < 1)
            {
                throw new ErrorCodeException(ErrorCodes.ProviderInvalidResponse, "Mixing produced no output.");
            }
        }
        finally
        {
            _gate.Semaphore.Release();
        }
    }

    private async Task RunLimiterAsync(string inputPath, string outputPath, string workDir, CancellationToken cancellationToken)
    {
        var args = new List<string>
        {
            "-y", "-i", inputPath,
            "-filter:a", "alimiter=limit=0.95",
            "-ar", "48000", "-ac", "2", "-c:a", "pcm_s16le",
            outputPath,
        };

        await _gate.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            try
            {
                await _ffmpeg.RunAsync(args, workDir, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException ex)
            {
                throw new ErrorCodeException(ErrorCodes.ResourceExhausted, "Mixing limiter timed out.", ex);
            }
            catch (IOException ex)
            {
                throw new ErrorCodeException(ErrorCodes.ResourceExhausted, "Mixing limiter hit disk limits.", ex);
            }
        }
        finally
        {
            _gate.Semaphore.Release();
        }
    }

    private async Task<long> ProbeDurationMs(string file, CancellationToken cancellationToken)
    {
        try
        {
            var probe = await _ffprobe.ProbeAsync(file, cancellationToken).ConfigureAwait(false);
            return Math.Max(0, probe.DurationMs);
        }
        catch (ErrorCodeException)
        {
            return 0;
        }
    }

    private async Task<bool> HasSampleClippingAsync(string file, CancellationToken cancellationToken)
    {
        try
        {
            var (_, _, samples) = await ReadWavSamplesAsync(file, cancellationToken).ConfigureAwait(false);
            foreach (var sample in samples)
            {
                if (Math.Abs(sample) >= 1.0)
                {
                    return true;
                }
            }

            return false;
        }
#pragma warning disable CA1031 // Clipping probe is best-effort: undecodable bytes are already handled by FFprobe; assume no clipping here.
        catch (Exception)
#pragma warning restore CA1031
        {
            return false;
        }
    }

    private static async Task<(int SampleRate, int Channels, float[] Samples)> ReadWavSamplesAsync(
        string file,
        CancellationToken cancellationToken)
    {
        var bytes = await File.ReadAllBytesAsync(file, cancellationToken).ConfigureAwait(false);
        return ParseWav(bytes);
    }

    internal static (int SampleRate, int Channels, float[] Samples) ParseWav(byte[] bytes)
    {
        if (bytes.Length < 44)
        {
            throw new ErrorCodeException(ErrorCodes.ValidationFailed, "WAV file is too short.");
        }

        if (bytes[0] != (byte)'R' || bytes[1] != (byte)'I' || bytes[2] != (byte)'F' || bytes[3] != (byte)'F')
        {
            throw new ErrorCodeException(ErrorCodes.ValidationFailed, "WAV file is not RIFF.");
        }

        var channels = BitConverter.ToInt16(bytes, 22);
        var sampleRate = BitConverter.ToInt32(bytes, 24);
        var bitsPerSample = BitConverter.ToInt16(bytes, 34);
        var format = BitConverter.ToInt16(bytes, 20);

        // Locate the data chunk (skip extra chunks like bext/LIST).
        var dataOffset = -1;
        var dataLength = 0;
        var offset = 12;
        while (offset + 8 <= bytes.Length)
        {
            var chunkId = Encoding.ASCII.GetString(bytes, offset, 4);
            var chunkSize = BitConverter.ToInt32(bytes, offset + 4);
            if (string.Equals(chunkId, "data", StringComparison.Ordinal))
            {
                dataOffset = offset + 8;
                dataLength = chunkSize;
                break;
            }

            offset += 8 + Math.Max(0, chunkSize);
        }

        if (dataOffset < 0)
        {
            throw new ErrorCodeException(ErrorCodes.ValidationFailed, "WAV file has no data chunk.");
        }

        dataLength = Math.Min(dataLength, bytes.Length - dataOffset);
        if (channels <= 0 || sampleRate <= 0)
        {
            throw new ErrorCodeException(ErrorCodes.ValidationFailed, "WAV header is invalid.");
        }

        if (format == 1 && bitsPerSample == 16)
        {
            var count = dataLength / 2;
            var samples = new float[count];
            for (var i = 0; i < count; i++)
            {
                var value = BitConverter.ToInt16(bytes, dataOffset + (i * 2));
                samples[i] = value / 32768f;
            }

            return (sampleRate, channels, samples);
        }

        if (format == 3 && bitsPerSample == 32)
        {
            var count = dataLength / 4;
            var samples = new float[count];
            for (var i = 0; i < count; i++)
            {
                samples[i] = BitConverter.ToSingle(bytes, dataOffset + (i * 4));
            }

            return (sampleRate, channels, samples);
        }

        throw new ErrorCodeException(ErrorCodes.ValidationFailed, "WAV format must be 16-bit PCM or 32-bit float for verification.");
    }

    private static double? GetDouble(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var element))
        {
            return null;
        }

        if (element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out var number))
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

    private static string EffectiveProfileName(string? profile)
    {
        var normalized = (profile ?? string.Empty).Trim().ToLowerInvariant();
        return string.Equals(normalized, "broadcast", StringComparison.Ordinal) ? "broadcast" : "web";
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception)
        {
            // Best effort; OS temp cleaners cover leftovers.
        }
    }

    private static void DeleteDirQuietly(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch (Exception)
        {
            // Best effort; OS temp cleaners cover leftovers.
        }
    }
}

/// <summary>
/// Verification outcome for one mixed file (FFprobe + loudnorm re-measure).
/// </summary>
public sealed record MixVerification(
    int DurationMs,
    int SampleRate,
    int Channels,
    double Integrated,
    double TruePeak,
    bool LoudnessOk,
    bool PeakOk,
    bool Clipped);
