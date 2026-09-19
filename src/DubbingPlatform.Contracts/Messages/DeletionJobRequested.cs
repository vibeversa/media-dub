namespace DubbingPlatform.Contracts.Messages;

/// <summary>
/// Requests durable execution of one logical project deletion.
/// Added by Task 37 (security/privacy/retention/deletion): deletion jobs run
/// on the <c>maintenance</c> queue, independent of the core pipeline DAG.
/// Unlike stage messages this envelope is project-scoped (no processing run):
/// <c>ProcessingRunId</c> carries <see cref="Guid.Empty"/> and consumers must
/// not route it through run-scoped gates. Handled eagerly by
/// <c>DeletionJobWorker</c> with the <c>RetentionSweeper</c> as the daily
/// backstop; both are idempotent so duplicate deliveries are safe.
/// </summary>
public sealed record DeletionJobRequested(
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
    Guid DeletionJobId)
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
