namespace DubbingPlatform.Application.Abstractions.Providers.Dtos;

/// <summary>
/// VAD request. ArtifactId references the audio content to scan.
/// </summary>
public sealed record VadRequest(
    Guid TenantId,
    Guid ProjectId,
    Guid RunId,
    string ArtifactId,
    string Language,
    long InputBytes,
    int DurationMs,
    string? MediaFormat);

/// <summary>
/// One voiced region.
/// </summary>
public sealed record VadRegion(
    int StartMs,
    int EndMs,
    double Confidence);

/// <summary>
/// VAD response with regions plus model telemetry.
/// </summary>
public sealed record VadResponse(
    List<VadRegion> Regions,
    double Confidence,
    string Model,
    string? ModelVersion,
    string? Deployment,
    ProviderUsage? Usage,
    Dictionary<string, string>? RawMetadata);
