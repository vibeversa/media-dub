namespace DubbingPlatform.Contracts.Messages;

/// <summary>
/// Published whenever a segment selection changes (explicit version select or
/// manual edit creating a new version). Downstream voice/timing work treats
/// this as invalidation: artifacts rendered from older selections are stale.
/// Uses the frozen <see cref="IntegrationMessage"/> envelope with
/// <c>SchemaVersion = 1</c>; <c>ProcessingRunId</c> is the segment's run,
/// <c>ScopeType = "Segment"</c>, <c>ScopeId</c>/<c>SegmentId</c> identify the
/// segment. No media, text, or secrets are carried — ids, versions, and flags
/// only.
/// </summary>
public sealed record SegmentSelectionChanged(
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
    int SelectionVersion,
    Guid? SelectedTranscriptVersionId,
    Guid? SelectedTranslationVersionId,
    string? Reason,
    Guid UpdatedByUserId,
    bool OutputStale)
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
