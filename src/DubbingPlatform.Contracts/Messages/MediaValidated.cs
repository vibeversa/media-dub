namespace DubbingPlatform.Contracts.Messages;

/// <summary>
/// Published when uploaded media has been validated.
/// </summary>
public sealed record MediaValidated(
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
    string MediaAssetId,
    bool IsValid)
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
