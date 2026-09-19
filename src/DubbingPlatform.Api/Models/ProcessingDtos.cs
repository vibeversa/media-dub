namespace DubbingPlatform.Api.Models;

/// <summary>
/// Start-processing request body (all fields optional for 018; media-ready
/// is validated server-side, else 409 CONFLICT).
/// </summary>
public sealed class StartProcessingRequest
{
    public string? PipelineVersion { get; set; }
}

/// <summary>
/// Processing-run response body.
/// </summary>
public sealed record ProcessingRunResponse(
    string RunId,
    string ProjectId,
    string Status,
    int Attempt,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt);

/// <summary>
/// Durable progress response body. <c>PercentageIndicator</c> is
/// <c>completed/expected*100</c> rounded (indicator only);
/// <c>NotEta</c> is always true — the platform never promises an ETA.
/// <c>Warnings</c> carries review reasons and advisories, never payload text.
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
    DateTimeOffset GeneratedAt);

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
