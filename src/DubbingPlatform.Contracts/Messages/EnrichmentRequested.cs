namespace DubbingPlatform.Contracts.Messages;

/// <summary>
/// Optional post-MVP enrichment request emitted after core render completes.
/// Published to <see cref="QueueNames.AiGpu"/> (video workload queue) only when
/// the corresponding <c>Features</c> flag is true AND the project opted in via
/// <c>settings.enrichment</c> (dual gate; see
/// <c>DubbingPlatform.Application.Enrichment.EnrichmentGate</c>).
/// Core completion (<c>RunCompleted</c>) is independent: enrichment failures
/// never fail the run and never modify the core <c>OutputAsset</c>.
/// Flag toggles take effect only for new runs (a run reads flags+settings at
/// render-completion time; mid-run toggles do not retroactively enqueue).
/// </summary>
public sealed record EnrichmentRequested(
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
    string Kind)
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
