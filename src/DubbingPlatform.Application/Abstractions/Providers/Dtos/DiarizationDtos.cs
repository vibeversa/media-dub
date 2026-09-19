namespace DubbingPlatform.Application.Abstractions.Providers.Dtos;

/// <summary>
/// Diarization request.
/// </summary>
public sealed record DiarizationRequest(
    Guid TenantId,
    Guid ProjectId,
    Guid RunId,
    string ArtifactId,
    string Language,
    long InputBytes,
    int DurationMs,
    string? MediaFormat);

/// <summary>
/// One diarized segment.
/// </summary>
public sealed record DiarizationSegment(
    string SpeakerLabel,
    int StartMs,
    int EndMs,
    double Confidence);

/// <summary>
/// Diarization response.
/// </summary>
public sealed record DiarizationResponse(
    List<DiarizationSegment> Segments,
    List<string> SpeakerLabels,
    double Confidence,
    string Model,
    string? ModelVersion,
    string? Deployment,
    ProviderUsage? Usage,
    Dictionary<string, string>? RawMetadata);
