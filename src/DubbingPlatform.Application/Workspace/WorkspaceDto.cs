namespace DubbingPlatform.Application.Workspace;

/// <summary>
/// Workspace project slice.
/// </summary>
public sealed record WorkspaceProjectDto(
    string Id,
    string Name,
    string Status,
    string SourceLanguage,
    string TargetLanguage,
    bool IsArchived,
    string ConfigurationHash,
    int SettingsVersion);

/// <summary>
/// Workspace media slice (latest validated asset, null when none).
/// </summary>
public sealed record WorkspaceMediaDto(
    string? Id,
    string Status,
    string? Container,
    long SizeBytes,
    int DurationMs);

/// <summary>
/// Workspace run slice (latest run, null when never started).
/// </summary>
public sealed record WorkspaceRunDto(
    string? Id,
    string? Status,
    string? ConfigHash,
    int Attempt);

/// <summary>
/// Workspace progress slice. <c>PercentApproximate</c> is display-only and
/// never drives billing (billing source of truth stays in the Plan A ledger).
/// </summary>
public sealed record WorkspaceProgressDto(
    int PercentApproximate,
    string? CurrentStage,
    DateTimeOffset UpdatedAt);

/// <summary>
/// Workspace review slice.
/// </summary>
public sealed record WorkspaceReviewDto(
    int PendingCount,
    DateTimeOffset? OldestWaitingAt);

/// <summary>
/// Workspace output slice. <c>State</c> is <c>ready|pending</c>;
/// <c>Completeness</c> is 0..100 display-only.
/// </summary>
public sealed record WorkspaceOutputDto(
    string State,
    int Completeness);

/// <summary>
/// Workspace cost slice. Display approximations only.
/// </summary>
public sealed record WorkspaceCostDto(
    double RunCost,
    double MonthToDate);

/// <summary>
/// Workspace activity row (max 10, newest first; ids and summaries only).
/// </summary>
public sealed record WorkspaceActivityRowDto(
    string Id,
    string Summary,
    DateTimeOffset OccurredAt);

/// <summary>
/// Workspace activity slice.
/// </summary>
public sealed record WorkspaceActivityDto(
    IReadOnlyList<WorkspaceActivityRowDto> Recent);

/// <summary>
/// Workspace permissions slice (UX hints only, never a security boundary).
/// </summary>
public sealed record WorkspacePermissionsDto(
    IReadOnlyList<string> AllowedActions);

/// <summary>
/// Workspace aggregate DTO. One handler call returns all sections in a single
/// batched read (no N+1): <c>project, media, run, phase, stage, progress,
/// review, warnings, output, cost, activity, permissions</c>.
/// </summary>
public sealed record WorkspaceDto(
    WorkspaceProjectDto Project,
    WorkspaceMediaDto Media,
    WorkspaceRunDto Run,
    string Phase,
    string? Stage,
    WorkspaceProgressDto Progress,
    WorkspaceReviewDto Review,
    IReadOnlyList<string> Warnings,
    WorkspaceOutputDto Output,
    WorkspaceCostDto Cost,
    WorkspaceActivityDto Activity,
    WorkspacePermissionsDto Permissions);
