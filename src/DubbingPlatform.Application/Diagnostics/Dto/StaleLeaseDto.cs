namespace DubbingPlatform.Application.Diagnostics.Dto;

/// <summary>
/// One stale stage-execution lease. Carries ids, timestamps, and a run/project
/// owner hint only — never lease tokens or payload bodies.
/// </summary>
public sealed record StaleLeaseDto(
    string CorrelationId,
    Guid StageExecutionId,
    string StageType,
    string Status,
    Guid ProjectId,
    Guid ProcessingRunId,
    string OwnerHint,
    DateTimeOffset StartedAt,
    DateTimeOffset LeaseExpiresAt,
    DateTimeOffset? LastHeartbeatAt);
