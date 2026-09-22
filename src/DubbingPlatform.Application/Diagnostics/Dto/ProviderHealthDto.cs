namespace DubbingPlatform.Application.Diagnostics.Dto;

/// <summary>
/// Per-provider health snapshot. Secret-free by construction: only the
/// provider name, aggregate status, latency/error aggregates, timestamps,
/// route names, and circuit state are carried — never keys, endpoints with
/// credentials, or raw payloads.
/// </summary>
public sealed record ProviderHealthDto(
    string CorrelationId,
    string Provider,
    string Status,
    double? LatencyMsP95,
    double ErrorRate,
    DateTimeOffset? LastSuccessAt,
    IReadOnlyList<string> ActiveRoutes,
    string CircuitBreakerState);
