namespace DubbingPlatform.Application.Abstractions.Providers.Dtos;

/// <summary>
/// Source-separation request.
/// </summary>
public sealed record SeparationRequest(
    Guid TenantId,
    Guid ProjectId,
    Guid RunId,
    string ArtifactId,
    string Language,
    long InputBytes,
    int DurationMs,
    string? MediaFormat);

/// <summary>
/// Source-separation response.
/// </summary>
public sealed record SeparationResponse(
    string DialogueArtifactId,
    string? BackgroundArtifactId,
    double Confidence,
    string Model,
    string? ModelVersion,
    string? Deployment,
    ProviderUsage? Usage,
    Dictionary<string, string>? RawMetadata);
