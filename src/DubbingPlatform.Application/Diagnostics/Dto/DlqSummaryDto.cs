namespace DubbingPlatform.Application.Diagnostics.Dto;

/// <summary>
/// Dead-letter queue summary: depth, oldest-entry age, and the top-10
/// dead-letter reason breakdown. An empty DLQ reads as zero depth with null
/// oldest age (never 404). Counts and codes only — never message bodies.
/// </summary>
public sealed record DlqSummaryDto(
    string CorrelationId,
    long Depth,
    DateTimeOffset? OldestEnqueuedAt,
    TimeSpan? OldestEntryAge,
    IReadOnlyList<DlqReasonCount> TopReasons);

/// <summary>
/// One dead-letter reason code with its entry count.
/// </summary>
public sealed record DlqReasonCount(
    string Code,
    long Count);
