namespace DubbingPlatform.Contracts.Messages;

/// <summary>
/// Requests execution of one stage unit. The required stage, scope type, and
/// scope id are explicit non-null fields; a null value is a validation error,
/// never a silent default. Optional JSON input stays under 256KB by referencing
/// artifacts instead of inline bytes.
/// </summary>
public sealed record StageWorkRequested(
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
    string StageTypeRequired,
    string ScopeTypeRequired,
    string ScopeIdRequired,
    string? PayloadJson)
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
