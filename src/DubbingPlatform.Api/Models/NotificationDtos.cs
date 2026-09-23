namespace DubbingPlatform.Api.Models;

/// <summary>
/// Notification inbox row with deep-link fields for Task 034 routing.
/// Summaries only — never transcript bodies, signed URLs, or secrets.
/// </summary>
public sealed record NotificationResponse(
    string Id,
    string Type,
    string Severity,
    string Title,
    string Body,
    string ResourceType,
    string ResourceId,
    string? ProjectId,
    DateTimeOffset? ReadAt,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? ExpiresAt);

/// <summary>
/// Unread-count payload over the same scope as the list endpoint.
/// </summary>
public sealed record UnreadCountResponse(long UnreadCount);

/// <summary>
/// Read-all payload. Both names are returned for Task 012 (<c>markedCount</c>)
/// and Task 012B (<c>marked</c>) compatibility; they always carry the same value.
/// </summary>
public sealed record MarkAllReadResponse(
    int MarkedCount,
    int Marked);
