namespace DubbingPlatform.Application.Diagnostics.Dto;

/// <summary>
/// One configured provider route: capability key to provider name with its
/// priority index and enablement flag. Carries route names only — never keys,
/// endpoints with credentials, or raw payloads.
/// </summary>
public sealed record ProviderRouteDto(
    string CorrelationId,
    string Capability,
    string Provider,
    int Priority,
    bool Enabled);
