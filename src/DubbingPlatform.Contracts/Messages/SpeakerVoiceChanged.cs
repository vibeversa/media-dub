namespace DubbingPlatform.Contracts.Messages;

/// <summary>
/// Published whenever a speaker's stable voice assignment changes (explicit
/// PUT selecting a different voice). Downstream voice/timing work treats this
/// as invalidation: artifacts rendered from the old voice are stale.
/// Uses the frozen <see cref="IntegrationMessage"/> envelope with
/// <c>SchemaVersion = 1</c>; <c>ProcessingRunId</c> carries the assignment
/// run scope (latest run, or the project id as a pseudo-run when no runs
/// exist), <c>ScopeType = "Speaker"</c>, <c>ScopeId</c> is the speaker id in
/// <c>N</c> form, <c>SegmentId</c> is unset (speaker-scoped, not
/// segment-scoped). Carries ids and flags only — never audio, keys, or
/// subject identity.
/// </summary>
public sealed record SpeakerVoiceChanged(
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
    Guid SpeakerId,
    Guid OldVoiceProfileId,
    Guid NewVoiceProfileId,
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
