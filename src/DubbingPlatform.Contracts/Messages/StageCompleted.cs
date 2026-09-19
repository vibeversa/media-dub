namespace DubbingPlatform.Contracts.Messages;

/// <summary>
/// Published when a stage unit completes, listing the output artifact ids.
/// </summary>
public sealed record StageCompleted(
    Guid MessageId,
    string CorrelationId,
    Guid TenantId,
    Guid ProjectId,
    Guid ProcessingRunId,
    Guid? StageExecutionId,
    string? StageType,
    string? ScopeType,
    string? ScopeId,
    Guid? SegmentId,
    int SchemaVersion,
    DateTimeOffset CreatedAt,
    int Attempt,
    string? InputHash,
    string? ConfigurationHash,
    string? ExecutionSnapshotHash,
    string CompletedStageType,
    string[] OutputArtifactIds)
    : IntegrationMessage(
        MessageId,
        CorrelationId,
        TenantId,
        ProjectId,
        ProcessingRunId,
        StageExecutionId,
        StageType,
        ScopeType,
        ScopeId,
        SegmentId,
        SchemaVersion,
        CreatedAt,
        Attempt,
        InputHash,
        ConfigurationHash,
        ExecutionSnapshotHash);
