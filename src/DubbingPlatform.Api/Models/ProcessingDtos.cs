using DubbingPlatform.Application.Common;

namespace DubbingPlatform.Api.Models;

/// <summary>
/// Start-processing request body (all fields optional for 018; media-ready
/// is validated server-side, else 409 CONFLICT). <c>Force</c> bypasses the
/// active-run 409 only when the caller holds <c>processing.retry</c>
/// (else 409 <c>RUN_ALREADY_ACTIVE</c> persists).
/// </summary>
public sealed class StartProcessingRequest
{
    public string? PipelineVersion { get; set; }

    public bool Force { get; set; }
}

/// <summary>
/// Processing-run response body. <c>RetryOfRunId</c> is set only for run-level
/// retries (new run linked to the failed run); <c>ConfigHash</c> echoes the
/// run configuration hash for transparency.
/// </summary>
public sealed record ProcessingRunResponse(
    string RunId,
    string ProjectId,
    string Status,
    int Attempt,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    string? RetryOfRunId = null,
    string? ConfigHash = null);

/// <summary>
/// Durable progress response body. <c>PercentageIndicator</c> is
/// <c>completed/expected*100</c> rounded (indicator only);
/// <c>PercentApproximate</c> is the Task 008 alias for the same display-only
/// value (never drives billing); <c>NotEta</c> is always true — the platform
/// never promises an ETA. <c>Warnings</c> carries review reasons and
/// advisories, never payload text. A run with no stages yet returns
/// <c>{ percentApproximate: 0, currentStage: null }</c> (plus the remaining
/// zeroed fields), not 404.
/// </summary>
public sealed record ProgressResponse(
    string ProjectId,
    string? RunId,
    string Status,
    string Phase,
    string? CurrentStage,
    int CompletedUnits,
    int FailedUnits,
    int RetryingUnits,
    int ReviewUnits,
    int SkippedUnits,
    int ExpectedUnits,
    int EstimatedRemaining,
    int PercentageIndicator,
    bool NotEta,
    IReadOnlyList<string> Warnings,
    DateTimeOffset GeneratedAt,
    int PercentApproximate);

/// <summary>
/// Paginated run-list envelope for <c>GET .../processing</c>.
/// </summary>
public sealed record ProcessingRunListResponse(
    IReadOnlyList<ProcessingRunResponse> Items,
    int Page,
    int PageSize,
    long Total,
    bool HasMore)
{
    public static ProcessingRunListResponse Create(
        IReadOnlyList<ProcessingRunResponse> items,
        int page,
        int pageSize,
        long total)
    {
        ArgumentNullException.ThrowIfNull(items);
        return new ProcessingRunListResponse(
            items, page, pageSize, total, (long)page * pageSize < total);
    }
}

/// <summary>
/// Cancel request body. <c>Reason</c> is optional (defaults to
/// <c>operator-requested</c>); it is audited, never used for control flow.
/// </summary>
public sealed class CancelRequest
{
    public string? Reason { get; set; }
}

/// <summary>
/// Selective-retry request body: <c>scope</c> is <c>stage|segment</c>,
/// <c>stageType</c> names the DAG stage, <c>segmentId</c> is required only for
/// segment scope (raw GUID or <c>seg_</c>).
/// </summary>
public sealed class RetryRequest
{
    public string Scope { get; set; } = string.Empty;

    public string? StageType { get; set; }

    public string? SegmentId { get; set; }
}

/// <summary>
/// Workspace activity projection row (thin, paginated where lists).
/// </summary>
public sealed record WorkspaceActivityResponse(
    string Id,
    string Summary,
    DateTimeOffset OccurredAt);

/// <summary>
/// Workspace output projection (thin; full bodies stay in Tasks 009–012).
/// </summary>
public sealed record WorkspaceOutputResponse(
    string State,
    int Completeness,
    string? OutputId);

/// <summary>
/// Workspace quality projection (thin; counts only, never payload text).
/// </summary>
public sealed record WorkspaceQualityResponse(
    int FailedCount,
    int BlockedCount,
    IReadOnlyList<string> Codes);
