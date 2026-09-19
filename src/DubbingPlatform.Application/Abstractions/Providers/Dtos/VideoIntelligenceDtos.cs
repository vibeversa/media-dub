namespace DubbingPlatform.Application.Abstractions.Providers.Dtos;

/// <summary>
/// Video-intelligence request.
/// </summary>
public sealed record VideoIntelligenceRequest(
    Guid TenantId,
    Guid ProjectId,
    Guid RunId,
    string ArtifactId,
    string Language,
    long InputBytes,
    int DurationMs,
    string? MediaFormat);

/// <summary>
/// One face track.
/// </summary>
public sealed record FaceTrack(
    string TrackId,
    int StartMs,
    int EndMs,
    double Confidence);

/// <summary>
/// One active-speaker span.
/// </summary>
public sealed record ActiveSpeakerSegment(
    string SpeakerLabel,
    int StartMs,
    int EndMs,
    double Confidence);

/// <summary>
/// Video-intelligence response.
/// </summary>
public sealed record VideoIntelligenceResponse(
    List<FaceTrack> Faces,
    List<ActiveSpeakerSegment> ActiveSpeakers,
    double Confidence,
    string Model,
    string? ModelVersion,
    string? Deployment,
    ProviderUsage? Usage,
    Dictionary<string, string>? RawMetadata);
