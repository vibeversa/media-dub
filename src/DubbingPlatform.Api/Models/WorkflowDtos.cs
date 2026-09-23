namespace DubbingPlatform.Api.Models;

/// <summary>
/// Segment list item (018 returns real rows when present, else empty pages;
/// single-segment reads return 404 until 022 populates segments).
/// </summary>
public sealed record SegmentResponse(
    string Id,
    string ProjectId,
    string Status,
    int Sequence,
    int StartMs,
    int EndMs);

/// <summary>
/// Review list item / single.
/// </summary>
public sealed record ReviewResponse(
    string Id,
    string ProjectId,
    string Status,
    string Reason,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ResolvedAt);

/// <summary>
/// Review decision request body. <c>Reason</c> is recorded on the decision;
/// <c>EditedText</c> is required only for resolve-with-edit (reviewer-authored
/// replacement transcript/translation, 1..5000 chars).
/// </summary>
public sealed class ReviewDecisionRequest
{
    public string? Reason { get; set; }

    public string? EditedText { get; set; }
}

/// <summary>
/// Export create request body. <c>Format</c> is kebab-case allowlisted
/// server-side; <c>Profile</c> is an optional kebab-case variant (path
/// traversal rejected with 400); <c>AllowPartial</c> must be true to create
/// an export over an incomplete run (otherwise 409 with a partial offer).
/// </summary>
public sealed class CreateExportRequest
{
    public string Format { get; set; } = string.Empty;

    public string? Profile { get; set; }

    public bool AllowPartial { get; set; }
}

/// <summary>
/// Export response body.
/// </summary>
public sealed record ExportResponse(
    string Id,
    string ProjectId,
    string Format,
    string Status,
    bool IsPartial,
    DateTimeOffset CreatedAt,
    string? CompletenessJson);

/// <summary>
/// Download-URL response body (15-minute presigned URLs).
/// </summary>
public sealed record DownloadUrlResponse(
    string Url,
    DateTimeOffset ExpiresAt);
