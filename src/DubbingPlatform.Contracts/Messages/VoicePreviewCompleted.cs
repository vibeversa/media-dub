namespace DubbingPlatform.Contracts.Messages;

/// <summary>
/// Published whenever a voice preview job reaches a terminal state
/// (<c>Completed</c>, <c>Failed</c>, or <c>Cancelled</c>). Consumed by the
/// Task 010 endpoint layer (UI polling/push); no worker acts on it yet.
/// Uses the frozen <see cref="IntegrationMessage"/> envelope with
/// <c>SchemaVersion = 1</c>; <c>ProcessingRunId</c> carries the synthesis
/// scope (the originating run when the preview was requested in-pipeline,
/// otherwise the job id as an ad-hoc scope). <c>ScopeType = "VoicePreview"</c>
/// and <c>ScopeId</c>/<c>SegmentId</c> are unset (job-scoped, not
/// segment-scoped). Carries ids, status, and error codes only — never preview
/// text, audio bytes, or secrets.
/// </summary>
public sealed record VoicePreviewCompleted(
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
    Guid VoicePreviewJobId,
    string Status,
    Guid? ArtifactId,
    Guid? ProviderExecutionId,
    string? ErrorCode)
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
