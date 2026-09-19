namespace DubbingPlatform.Application.Abstractions;

/// <summary>
/// Result of an FFmpeg execution. <c>Args</c> is the exact argument list passed
/// via <c>ArgumentList</c> (no shell); workers persist it to artifact metadata
/// so canonical-audio provenance is auditable.
/// </summary>
public sealed record FfmpegResult(
    int ExitCode,
    IReadOnlyList<string> Args,
    TimeSpan Duration,
    bool TimedOut,
    string OutputPath);

/// <summary>
/// Hardened FFmpeg audio extraction. Implementations run <c>ffmpeg</c> via
/// <c>ProcessRunner</c> (<c>ArgumentList</c> only, never shell), append
/// <c>-threads {CpuThreads}</c> from <c>Media:CpuThreads</c>, retry once on
/// timeout (then throw <c>TimeoutException</c>, transient), throw
/// <c>ErrorCodeException</c> with <c>INTERNAL_ERROR</c> on non-zero exit, and
/// verify the output via <see cref="IFFprobeService"/> (decodable audio else
/// <c>MEDIA_CORRUPT</c>). Paths and flags contain no secrets and are safe to log.
/// </summary>
public interface IFFmpegService
{
    /// <summary>
    /// Runs <c>ffmpeg</c> with <paramref name="args"/> in
    /// <paramref name="workDir"/> with the <c>Media:FfmpegTimeoutSec</c> timeout.
    /// </summary>
    Task<FfmpegResult> RunAsync(IReadOnlyList<string> args, string workDir, CancellationToken cancellationToken);

    /// <summary>
    /// Builds the canonical-audio args: 48kHz, 24-bit (<c>s32→flac</c>),
    /// layout preserved (no <c>-ac</c>), <c>-vn</c> (audio only).
    /// </summary>
    IReadOnlyList<string> BuildCanonicalArgs(string sourcePath, string destPath, int cpuThreads);

    /// <summary>
    /// Builds the optional 32-bit float working-copy args
    /// (<c>flt→pcm_f32le</c>). Used only when <c>needsFloatWork</c> is true.
    /// </summary>
    IReadOnlyList<string> BuildFloatArgs(string sourcePath, string destPath, int cpuThreads);

    /// <summary>
    /// Extracts canonical 48kHz audio to <paramref name="destFlacPath"/>
    /// (24-bit FLAC via <c>s32</c>, or 32-bit float working copy when
    /// <paramref name="needsFloatWork"/> is true) and verifies the output.
    /// </summary>
    Task<FfmpegResult> ExtractCanonicalAudioAsync(
        string sourcePath,
        string destFlacPath,
        CancellationToken cancellationToken,
        bool needsFloatWork = false);

    /// <summary>
    /// Builds segment-slice args: seeks to <paramref name="startSec"/>, takes
    /// <paramref name="durationSec"/>, 16kHz mono 16-bit wav for providers.
    /// </summary>
    IReadOnlyList<string> BuildSliceArgs(string sourcePath, string destPath, double startSec, double durationSec, int cpuThreads);

    /// <summary>
    /// Extracts <c>[startMs, startMs+durationMs]</c> to a 16kHz mono 16-bit wav
    /// at <paramref name="destWavPath"/> and verifies the output exists.
    /// Empty slices throw <c>VALIDATION_FAILED</c> (fail fast, no retry).
    /// </summary>
    Task<FfmpegResult> ExtractSegmentSliceAsync(
        string sourcePath,
        string destWavPath,
        int startMs,
        int durationMs,
        CancellationToken cancellationToken);

    /// <summary>
    /// Measures EBU R128 integrated loudness + true peak via a
    /// <c>loudnorm=print_format=json</c> measure pass (no gain is applied).
    /// Throws <c>PROVIDER_INVALID_RESPONSE</c> when the measurement fails or
    /// yields no JSON, <c>RESOURCE_EXHAUSTED</c> on disk/timeout.
    /// </summary>
    Task<SignalLoudness> MeasureSignalLoudnessAsync(
        string file,
        string workDir,
        CancellationToken cancellationToken);

    /// <summary>
    /// Measures per-channel RMS levels via <c>astats</c>, optionally windowed
    /// with input seeking (<paramref name="startMs"/>/
    /// <paramref name="durationMs"/>). Returns one dB value per channel
    /// (<c>double.NegativeInfinity</c> for digital silence). Throws
    /// <c>PROVIDER_INVALID_RESPONSE</c> when no levels parse,
    /// <c>RESOURCE_EXHAUSTED</c> on disk/timeout.
    /// </summary>
    Task<IReadOnlyList<double>> MeasureChannelRmsDbAsync(
        string file,
        string workDir,
        int? startMs,
        int? durationMs,
        CancellationToken cancellationToken);

    /// <summary>
    /// Measures the longest near-silence run via
    /// <c>silencedetect</c> at <paramref name="silenceThresholdDb"/> dB with
    /// minimum <paramref name="minSilenceSec"/> seconds. Returns 0 when no
    /// silence is found. Throws <c>PROVIDER_INVALID_RESPONSE</c> on FFmpeg
    /// failure, <c>RESOURCE_EXHAUSTED</c> on disk/timeout.
    /// </summary>
    Task<double> MeasureMaxSilenceSecAsync(
        string file,
        string workDir,
        double silenceThresholdDb,
        double minSilenceSec,
        CancellationToken cancellationToken);
}

/// <summary>
/// EBU R128 loudness measurement for one audio file.
/// </summary>
public sealed record SignalLoudness(
    double IntegratedLufs,
    double TruePeakDbtp);
