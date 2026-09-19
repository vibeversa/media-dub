namespace DubbingPlatform.Application.Abstractions.Providers.Dtos;

/// <summary>
/// Translation request.
/// </summary>
public sealed record TranslationRequest(
    Guid TenantId,
    Guid ProjectId,
    Guid RunId,
    string ArtifactId,
    string SourceLanguage,
    string TargetLanguage,
    long InputBytes,
    int DurationMs);

/// <summary>
/// Translation response with quality sub-scores.
/// </summary>
public sealed record TranslationResponse(
    string PrimaryText,
    List<string> Alternatives,
    double SemanticScore,
    double NaturalnessScore,
    double TimingScore,
    double Confidence,
    string Model,
    string? ModelVersion,
    string? Deployment,
    ProviderUsage? Usage,
    Dictionary<string, string>? RawMetadata);
