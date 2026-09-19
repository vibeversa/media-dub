namespace DubbingPlatform.Application.Providers;

/// <summary>
/// Routing input for compatibility checks. Carries only non-secret sizing and
/// feature flags; never credentials or payload bytes.
/// </summary>
public sealed record ProviderRoutingRequest(
    string Language,
    long InputBytes,
    int DurationMs,
    string? MediaFormat,
    bool RequiresWordTimestamps,
    bool RequiresDiarization,
    bool RequiresVoiceCloning);
