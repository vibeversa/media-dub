namespace DubbingPlatform.Application.Diagnostics.Dto;

/// <summary>
/// Review backlog aggregation: status counts, QC severity counts, the oldest
/// waiting review, and a per-project open-review breakdown. Counts and ids
/// only — never review payload text.
/// </summary>
public sealed record ReviewBacklogDto(
    string CorrelationId,
    long TotalOpen,
    IReadOnlyDictionary<string, long> ByStatus,
    IReadOnlyDictionary<string, long> BySeverity,
    DateTimeOffset? OldestWaitingAt,
    Guid? OldestWaitingReviewId,
    IReadOnlyList<ReviewBacklogProjectEntry> PerProject);

/// <summary>
/// Open-review backlog for one project.
/// </summary>
public sealed record ReviewBacklogProjectEntry(
    Guid ProjectId,
    long OpenCount,
    DateTimeOffset? OldestWaitingAt);
