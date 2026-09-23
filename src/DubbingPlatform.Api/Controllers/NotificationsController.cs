using DubbingPlatform.Application.Common;
using DubbingPlatform.Application.MultiTenancy;
using DubbingPlatform.Application.Notifications;
using DubbingPlatform.Api.Middleware;
using DubbingPlatform.Api.Models;
using DubbingPlatform.Domain.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DubbingPlatform.Api.Controllers;

/// <summary>
/// Durable notification inbox (Task 012 / 012B) over the Task 002 projection:
/// <c>GET /api/v1/notifications</c> (paginated, <c>?unreadOnly=true</c>,
/// newest-first, expired excluded),
/// <c>GET /api/v1/notifications/unread-count</c> (same scope, lightweight),
/// <c>POST /api/v1/notifications/{id}/read</c> (idempotent),
/// <c>POST /api/v1/notifications/read-all</c> (idempotent, returns both
/// <c>markedCount</c> and <c>marked</c> for 012/012B compat).
/// Strict recipient scoping (<c>TenantId + RecipientUserId == caller</c>);
/// cross-user/cross-tenant ids return 404 without leak. Null-<c>ProjectId</c>
/// quota/policy rows are visible at tenant level (no project filter).
/// Payloads carry short summaries only — never transcript bodies, signed
/// URLs, secrets, or raw provider data. SSE (Task 013) is an invalidation
/// hint only; this API is the source of truth.
/// </summary>
[ApiController]
[Route("api/v1/notifications")]
[Authorize]
[ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
public sealed class NotificationsController : ControllerBase
{
    private readonly NotificationProjector _notifications;

    public NotificationsController(NotificationProjector notifications)
    {
        ArgumentNullException.ThrowIfNull(notifications);
        _notifications = notifications;
    }

    [HttpGet]
    [ProducesResponseType(typeof(PaginatedResult<NotificationResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> List(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] bool unreadOnly = false,
        CancellationToken cancellationToken = default)
    {
        var tenantId = TenantForNotifications(User);
        var recipient = RecipientForNotifications(User);
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var (items, total) = await _notifications.ListActiveAsync(
            tenantId, recipient, unreadOnly, page, pageSize, cancellationToken).ConfigureAwait(false);
        var responses = items.Select(ToResponse).ToList();
        return Ok(PaginatedResult<NotificationResponse>.Create(responses, page, pageSize, total));
    }

    [HttpGet("unread-count")]
    [ProducesResponseType(typeof(UnreadCountResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> UnreadCount(CancellationToken cancellationToken)
    {
        var tenantId = TenantForNotifications(User);
        var recipient = RecipientForNotifications(User);
        var count = await _notifications.CountUnreadAsync(tenantId, recipient, cancellationToken).ConfigureAwait(false);
        return Ok(new UnreadCountResponse(count));
    }

    [HttpPost("{notificationId}/read")]
    [ProducesResponseType(typeof(NotificationResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> MarkRead(
        [FromRoute] string notificationId,
        CancellationToken cancellationToken)
    {
        var tenantId = TenantForNotifications(User);
        var recipient = RecipientForNotifications(User);
        var id = PublicIdParser.ParseNotificationId(notificationId);
        await _notifications.MarkAsReadAsync(tenantId, recipient, id, cancellationToken).ConfigureAwait(false);

        var (items, _) = await _notifications.ListActiveAsync(
            tenantId, recipient, false, 1, 100, cancellationToken).ConfigureAwait(false);
        var current = items.FirstOrDefault(n => n.Id == id);
        if (current is null)
        {
            // Expired between mark and re-read: the mark succeeded; report
            // the id as read without leaking storage detail.
            return Ok(new NotificationResponse(
                PublicIdParser.ToNotificationId(id), string.Empty, string.Empty,
                string.Empty, string.Empty, string.Empty, string.Empty, null, null, null, null));
        }

        return Ok(ToResponse(current));
    }

    [HttpPost("read-all")]
    [ProducesResponseType(typeof(MarkAllReadResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> MarkAllRead(CancellationToken cancellationToken)
    {
        var tenantId = TenantForNotifications(User);
        var recipient = RecipientForNotifications(User);
        var marked = await _notifications.MarkAllReadAsync(tenantId, recipient, cancellationToken).ConfigureAwait(false);
        return Ok(new MarkAllReadResponse(marked, marked));
    }

    private static Guid TenantForNotifications(System.Security.Claims.ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(user);
        var raw = user.FindFirst(DubbingPlatform.Application.Authorization.ClaimTypes.TenantId)?.Value;
        if (string.IsNullOrWhiteSpace(raw) || !Guid.TryParse(raw.Trim(), out var tenantId) || tenantId == Guid.Empty)
        {
            throw new UnauthorizedAccessException("The 'tid' tenant claim is missing or invalid.");
        }

        return tenantId;
    }

    private static Guid RecipientForNotifications(System.Security.Claims.ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(user);
        var raw = user.FindFirst(DubbingPlatform.Application.Authorization.ClaimTypes.Subject)?.Value;
        if (string.IsNullOrWhiteSpace(raw) || !Guid.TryParse(raw.Trim(), out var recipient) || recipient == Guid.Empty)
        {
            throw new UnauthorizedAccessException("The 'sub' claim must be a user id.");
        }

        return recipient;
    }

    private static NotificationResponse ToResponse(Domain.Entities.Notification notification)
    {
        return new NotificationResponse(
            PublicIdMapper.ToPublic(notification.Id, PublicIdMapper.NotificationPrefix),
            notification.Type.ToString(),
            notification.Severity.ToString(),
            notification.Title,
            notification.Body,
            notification.ResourceType,
            notification.ResourceId,
            notification.ProjectId.HasValue
                ? PublicIdMapper.ToPublic(notification.ProjectId.Value, PublicIdMapper.DubbingProjectPrefix)
                : null,
            notification.ReadAt,
            notification.CreatedAt,
            notification.ExpiresAt);
    }
}
