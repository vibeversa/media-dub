namespace DubbingPlatform.Contracts.Messages;

/// <summary>
/// Requests cancellation of a running processing run.
/// </summary>
public sealed record RunCancelledRequested(
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
    string Reason)
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
