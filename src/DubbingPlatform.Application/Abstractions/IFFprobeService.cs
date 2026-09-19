namespace DubbingPlatform.Application.Abstractions;

/// <summary>
/// A single ffprobe stream.
/// </summary>
public sealed record FfprobeStream(
    string CodecType,
    string Codec,
    int? Width,
    int? Height,
    double? Fps,
    int? SampleRate,
    int? Channels,
    string? ChannelLayout);

/// <summary>
/// Parsed ffprobe output. <c>Container</c> is the normalized container name
/// (lowercase, e.g. <c>mp4</c>/<c>mov</c>/<c>mkv</c>/<c>wav</c>/<c>mp3</c>/<c>flac</c>);
/// <c>DurationMs</c> is rounded milliseconds from <c>format.duration</c>.
/// </summary>
public sealed record FfprobeResult(
    string Container,
    long DurationMs,
    IReadOnlyList<FfprobeStream> Streams);

/// <summary>
/// FFprobe media inspection. Implementations run <c>ffprobe</c> via
/// <c>ProcessRunner</c> (ArgumentList only, no shell), download S3 keys to
/// temp files when needed, and always delete temp files. Failures throw
/// <c>ErrorCodeException</c> with <c>MEDIA_CORRUPT</c> (exit≠0, timeout,
/// unparseable); validation of size/duration/container/codecs is the
/// caller's job (<c>MEDIA_UNSUPPORTED</c>).
/// </summary>
public interface IFFprobeService
{
    Task<FfprobeResult> ProbeAsync(string storageKeyOrLocalPath, CancellationToken cancellationToken);
}
