namespace DubbingPlatform.Api.Models;

/// <summary>
/// Task 009 segment summary for list rows (no version texts; detail endpoint
/// carries texts). Includes selection pointer plus derived review/quality/sync
/// flags used by the filter matrix.
/// </summary>
public sealed record SegmentSummaryResponse(
    string Id,
    string ProjectId,
    string Status,
    int Sequence,
    int StartMs,
    int EndMs,
    string? SpeakerId,
    int SelectionVersion,
    string? ReviewStatus,
    IReadOnlyList<string> QualityCodes,
    string? SyncStatus);

/// <summary>
/// Transcript version row for segment detail (texts included; never logged).
/// </summary>
public sealed record SegmentTranscriptVersionResponse(
    string Id,
    string Provider,
    string Model,
    string Text,
    bool IsSelected,
    DateTimeOffset CreatedAt);

/// <summary>
/// Translation version row for segment detail.
/// </summary>
public sealed record SegmentTranslationVersionResponse(
    string Id,
    string PrimaryText,
    string Provider,
    string Model,
    bool IsSelected,
    DateTimeOffset CreatedAt);

/// <summary>
/// Segment detail with current versions + selection version.
/// Example: <c>{ id: "seg_...", selectionVersion: 2, transcriptVersions: [...],
/// translationVersions: [...] }</c>.
/// </summary>
public sealed record SegmentDetailResponse(
    string Id,
    string ProjectId,
    string Status,
    int Sequence,
    int StartMs,
    int EndMs,
    string? SpeakerId,
    int SelectionVersion,
    string? SelectedTranscriptVersionId,
    string? SelectedTranslationVersionId,
    IReadOnlyList<SegmentTranscriptVersionResponse> TranscriptVersions,
    IReadOnlyList<SegmentTranslationVersionResponse> TranslationVersions,
    string? ReviewStatus,
    IReadOnlyList<string> QualityCodes,
    string? SyncStatus,
    bool OutputStale);

/// <summary>
/// Selection/edit mutation response. <c>OutputStale</c> true plus
/// <c>WarningCode="OUTPUT_STALE"</c> when final output already exists for the
/// run (success with warning, not an error).
/// </summary>
public sealed record SegmentMutationResponse(
    string SegmentId,
    int SelectionVersion,
    string? SelectedTranscriptVersionId,
    string? SelectedTranslationVersionId,
    string? NewVersionId,
    bool OutputStale,
    string? WarningCode);

/// <summary>
/// Select-version request body: <c>{ versionId, expectedSelectionVersion,
/// reason? }</c>. <c>Reason</c> nullable, max 500, sanitized (HTML stripped).
/// Example: <c>{ versionId: "3fa...", expectedSelectionVersion: 1,
/// reason: "prefer v2" }</c>.
/// </summary>
public sealed class SelectVersionRequest
{
    public string? VersionId { get; set; }

    public int ExpectedSelectionVersion { get; set; }

    public string? Reason { get; set; }
}

/// <summary>
/// Manual-edit request body: <c>{ text, expectedSelectionVersion, reason? }</c>
/// (1..5000 chars plain text; empty → 400 <c>SEGMENT_TEXT_EMPTY</c>).
/// Example: <c>{ text: "corrected line", expectedSelectionVersion: 1 }</c>.
/// </summary>
public sealed class EditTextRequest
{
    public string? Text { get; set; }

    public int ExpectedSelectionVersion { get; set; }

    public string? Reason { get; set; }
}
