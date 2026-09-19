namespace DubbingPlatform.Application.Abstractions.Providers.Dtos;

/// <summary>
/// Local-inference request.
/// </summary>
public sealed record LocalInferenceRequest(
    Guid TenantId,
    Guid ProjectId,
    Guid RunId,
    string ModelId,
    string PayloadJson,
    string Language,
    long InputBytes,
    int DurationMs);

/// <summary>
/// Local-inference response.
/// </summary>
public sealed record LocalInferenceResponse(
    string ModelId,
    string OutputJson,
    double Confidence,
    string Model,
    string? ModelVersion,
    string? Deployment,
    ProviderUsage? Usage,
    Dictionary<string, string>? RawMetadata);
