namespace DubbingPlatform.Application.Abstractions.Providers.Dtos;

/// <summary>
/// TTS synthesis request.
/// </summary>
public sealed record TtsRequest(
    Guid TenantId,
    Guid ProjectId,
    Guid RunId,
    string Text,
    string Language,
    string VoiceId,
    int DurationMs,
    bool RequiresVoiceCloning);

/// <summary>
/// TTS response referencing synthesized audio content.
/// </summary>
public sealed record TtsResponse(
    string ContentObjectId,
    int DurationMs,
    string VoiceId,
    double Confidence,
    string Model,
    string? ModelVersion,
    string? Deployment,
    ProviderUsage? Usage,
    Dictionary<string, string>? RawMetadata);
