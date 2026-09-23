namespace DubbingPlatform.Application.Output;

/// <summary>
/// Task 012A output aggregate wire model. Top-level <c>state</c> is one of
/// <c>Ready|Generating|Failed|Partial|Unavailable</c>; <c>generationState</c>
/// is the explicit alias of <c>state</c> required by Task 012A R1 (per-asset
/// and top-level readiness are never implied); <c>completeness</c> is
/// segment readiness (e.g. 96/100); <c>items</c> carries per-asset readiness
/// with signed-URL-only delivery (never storage keys or bucket paths);
/// <c>warnings</c> are display strings; <c>updatedAt</c> is the latest
/// data timestamp. <c>progressApproximate</c> is set only for
/// <c>Generating</c>; <c>errorCode</c> only for <c>Failed</c>;
/// <c>reason</c> only for <c>Unavailable</c> (<c>NO_RUNS_YET</c>).
/// </summary>
public sealed record OutputResponse(
    string State,
    string GenerationState,
    string? Reason,
    OutputCompletenessDto Completeness,
    double? ProgressApproximate,
    string? ErrorCode,
    OutputItemsDto Items,
    IReadOnlyList<string> Warnings,
    DateTimeOffset UpdatedAt);

/// <summary>
/// Segment readiness (ready/total).
/// </summary>
public sealed record OutputCompletenessDto(
    int Ready,
    int Total);

/// <summary>
/// Per-asset output items. Scalar items are single entries; subtitles is a
/// list (srt/webvtt); QC carries a summary plus an optional issues URL
/// (signed URL when a QC report artifact exists).
/// </summary>
public sealed record OutputItemsDto(
    OutputAssetEntryDto? Video,
    OutputAssetEntryDto? Audio,
    IReadOnlyList<OutputAssetEntryDto> Subtitles,
    OutputAssetEntryDto? Transcript,
    OutputAssetEntryDto? Translation,
    OutputAssetEntryDto? Timeline,
    OutputAssetEntryDto? Speakers,
    OutputQcDto Qc);

/// <summary>
/// One servable asset entry. <c>state</c> is one of
/// <c>ready|generating|failed|partial|unavailable</c>; <c>generationState</c>
/// is the explicit alias of <c>state</c> required by Task 012A R1 (no asset is
/// implied ready); <c>downloadUrl</c> is a
/// short-lived signed URL (≤15 min) present only when ready; <c>missing</c>
/// carries machine reasons (<c>SEGMENT_PENDING</c>, <c>QC_BLOCKED</c>,
/// <c>NO_RUNS_YET</c>, <c>ARTIFACT_MISSING</c>); <c>completeness</c> mirrors
/// the top-level pair for partial items.
/// </summary>
public sealed record OutputAssetEntryDto(
    string State,
    string GenerationState,
    string? DownloadUrl,
    IReadOnlyList<string> Missing,
    OutputCompletenessDto? Completeness);

/// <summary>
/// QC summary entry. <c>generationState</c> aliases <c>state</c> per Task 012A R1.
/// </summary>
public sealed record OutputQcDto(
    string State,
    string GenerationState,
    string Summary,
    string? IssuesUrl,
    IReadOnlyList<string> Missing);
