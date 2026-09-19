namespace DubbingPlatform.Application.Abstractions.Providers.Dtos;

/// <summary>
/// Lip-sync analysis request (audio/video run context).
/// </summary>
public sealed record LipSyncRequest(
    Guid TenantId,
    Guid ProjectId,
    Guid RunId,
    string ArtifactId,
    string Language,
    long InputBytes,
    int DurationMs,
    string? MediaFormat);

/// <summary>
/// Lip-sync analysis response. <c>Score</c> is 0..1 (mock 0.85 on success,
/// 0.35 on low-confidence). Model metadata mirrors other providers.
/// </summary>
public sealed record LipSyncResponse(
    double Score,
    int DurationMs,
    string Model,
    string? ModelVersion,
    string? Deployment,
    ProviderUsage? Usage,
    Dictionary<string, string>? RawMetadata);
