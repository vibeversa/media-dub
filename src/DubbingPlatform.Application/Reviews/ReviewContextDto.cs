namespace DubbingPlatform.Application.Reviews;

/// <summary>
/// Task 011 single-screen review read model. Eleven logical sections in one
/// 200: item, project, run, segment, versions, voice, audio, sync, QC,
/// actions/permissions, history. Audio carries IDs only here — signed URLs are
/// minted by the Task 012 output path at serve time, never in this aggregate.
/// Texts stay in the versions section (reviewer screen needs them); logs and
/// audit entries carry IDs only.
/// </summary>
public sealed record ReviewContextResponse(
    ReviewContextItemDto Item,
    ReviewContextProjectDto Project,
    ReviewContextRunDto Run,
    ReviewContextSegmentDto? Segment,
    ReviewContextVersionsDto Versions,
    ReviewContextVoiceDto Voice,
    ReviewContextAudioDto Audio,
    ReviewContextSyncDto Sync,
    ReviewContextQcDto Qc,
    ReviewContextActionsDto Actions,
    ReviewContextPermissionsDto Permissions,
    IReadOnlyList<ReviewContextHistoryDto> History,
    bool Truncated);

/// <summary>
/// Review identity: scope type, worst QC severity (<c>high|medium|low</c>,
/// <c>medium</c> when no QC rows), status, and the optimistic-concurrency
/// version (count of decision rows, 0 when untouched).
/// </summary>
public sealed record ReviewContextItemDto(
    string Id,
    string Type,
    string Severity,
    string Status,
    int Version);

/// <summary>
/// Owning project.
/// </summary>
public sealed record ReviewContextProjectDto(
    string Id,
    string? Name);

/// <summary>
/// Owning processing run.
/// </summary>
public sealed record ReviewContextRunDto(
    string Id,
    string Status,
    string ConfigHash);

/// <summary>
/// Scoped segment (null for project/run-level reviews).
/// </summary>
public sealed record ReviewContextSegmentDto(
    string Id,
    int StartMs,
    int EndMs,
    string? SpeakerId);

/// <summary>
/// Immutable content versions for the segment plus the selection pointer.
/// Lists are capped at <see cref="ReviewContextService.MaxVersionsPerList"/>
/// rows each (oldest first); <c>Truncated</c> flags capping. Segmentless
/// reviews carry empty lists with <c>SelectionVersion</c> 0.
/// </summary>
public sealed record ReviewContextVersionsDto(
    IReadOnlyList<ReviewContextTranscriptRowDto> Transcript,
    IReadOnlyList<ReviewContextTranslationRowDto> Translation,
    string? SelectedTranscriptVersionId,
    string? SelectedTranslationVersionId,
    int SelectionVersion,
    bool Truncated);

/// <summary>
/// Transcript version row.
/// </summary>
public sealed record ReviewContextTranscriptRowDto(
    string Id,
    string Provider,
    string Model,
    string Text,
    bool IsSelected,
    DateTimeOffset CreatedAt);

/// <summary>
/// Translation version row.
/// </summary>
public sealed record ReviewContextTranslationRowDto(
    string Id,
    string PrimaryText,
    string Provider,
    string Model,
    bool IsSelected,
    DateTimeOffset CreatedAt);

/// <summary>
/// Stable voice pointer for the segment speaker. <c>ConsentState</c> is one of
/// <c>not_applicable</c> (no speaker or no assignment yet),
/// <c>not_required</c> (stock voice), <c>granted</c> (cloning voice with a
/// covering granted consent), or <c>required</c> (cloning voice without one).
/// </summary>
public sealed record ReviewContextVoiceDto(
    string? SpeakerId,
    string? VoiceProfileId,
    string? VoiceId,
    string ConsentState);

/// <summary>
/// Latest voice-preview artifact reference for the speaker (IDs only;
/// <c>SignedUrl</c> is always null here and resolved by Task 012).
/// </summary>
public sealed record ReviewContextAudioDto(
    string? PreviewArtifactId,
    string? SignedUrl);

/// <summary>
/// Timing sync for the segment. Segmentless reviews carry null offset with
/// <c>DriftFlag</c> false.
/// </summary>
public sealed record ReviewContextSyncDto(
    int? OffsetMs,
    bool DriftFlag);

/// <summary>
/// QC evidence for the segment (or run scope when segmentless).
/// </summary>
public sealed record ReviewContextQcDto(
    IReadOnlyList<ReviewContextQcIssueDto> Issues,
    IReadOnlyList<string> EvidenceArtifactIds);

/// <summary>
/// One QC issue (IDs and codes only in logs).
/// </summary>
public sealed record ReviewContextQcIssueDto(
    string Id,
    string Code,
    string Severity,
    string Message,
    string? ArtifactId);

/// <summary>
/// Mutations the caller may attempt right now (state + server policy).
/// Open + <c>review.resolve</c> yields resolve/dismiss/resolve-with-edit;
/// terminal + <c>review.resolve</c> yields reopen; otherwise empty.
/// </summary>
public sealed record ReviewContextActionsDto(
    IReadOnlyList<string> Allowed);

/// <summary>
/// Caller capabilities (UX hints; every mutation re-authorizes server-side —
/// a forged action without the permission returns 403).
/// </summary>
public sealed record ReviewContextPermissionsDto(
    bool CanResolve,
    bool CanEdit);

/// <summary>
/// One decision row, oldest first, capped at
/// <see cref="ReviewContextService.MaxHistoryEntries"/> with the envelope
/// <c>Truncated</c> flag.
/// </summary>
public sealed record ReviewContextHistoryDto(
    string Id,
    string Type,
    string Reviewer,
    string Reason,
    DateTimeOffset CreatedAt);
