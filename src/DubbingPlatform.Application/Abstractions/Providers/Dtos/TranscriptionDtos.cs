namespace DubbingPlatform.Application.Abstractions.Providers.Dtos;

/// <summary>
/// Transcription request.
/// </summary>
public sealed record TranscriptionRequest(
    Guid TenantId,
    Guid ProjectId,
    Guid RunId,
    string ArtifactId,
    string Language,
    long InputBytes,
    int DurationMs,
    string? MediaFormat,
    bool RequiresWordTimestamps,
    bool RequiresDiarization);

/// <summary>
/// One word-level timestamp.
/// </summary>
public sealed record WordTimestamp(
    string Word,
    int StartMs,
    int EndMs,
    double Confidence);

/// <summary>
/// Transcription response.
/// </summary>
public sealed record TranscriptionResponse(
    string Text,
    double Confidence,
    List<WordTimestamp> Words,
    string Model,
    string? ModelVersion,
    string? Deployment,
    ProviderUsage? Usage,
    Dictionary<string, string>? RawMetadata);
