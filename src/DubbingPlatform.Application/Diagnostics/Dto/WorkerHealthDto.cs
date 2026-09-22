namespace DubbingPlatform.Application.Diagnostics.Dto;

/// <summary>
/// Per-worker health derived from runtime lease state. Workers with no
/// runtime state read as <c>Unknown</c> with a null heartbeat (never 404).
/// Counts and timestamps only — never lease tokens.
/// </summary>
public sealed record WorkerHealthDto(
    string CorrelationId,
    string Worker,
    string Status,
    DateTimeOffset? LastHeartbeatAt,
    long ActiveJobs,
    string? Version);
