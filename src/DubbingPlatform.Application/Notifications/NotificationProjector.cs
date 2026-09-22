using System.Diagnostics.Metrics;
using System.Text.RegularExpressions;
using DubbingPlatform.Application.MultiTenancy;
using DubbingPlatform.Application.Security;
using DubbingPlatform.Application.Services;
using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Domain.Enums;
using DubbingPlatform.Domain.Exceptions;
using Microsoft.EntityFrameworkCore;

namespace DubbingPlatform.Application.Notifications;

/// <summary>
/// Notification meters. Counter names are frozen: renaming breaks dashboards.
/// </summary>
public static class NotificationMeters
{
    public const string MeterName = "DubbingPlatform.Notifications";

    public const string SkippedMetricName = "notifications.skipped_total";

    private static readonly Meter Meter = new(MeterName);

    public static readonly Counter<long> Skipped =
        Meter.CreateCounter<long>(SkippedMetricName);
}

/// <summary>
/// Durable notification input. Summaries only: callers pass short human-readable
/// text with ids and counts; storage keys, transcript bodies, signed URLs, and
/// secrets are stripped by the projector and rejected by the entity.
/// </summary>
public sealed record NotificationInput(
    Guid TenantId,
    Guid? ProjectId,
    NotificationType Type,
    NotificationSeverity Severity,
    string Title,
    string Body,
    string ResourceType,
    string ResourceId,
    Guid? SourceEventId,
    DateTimeOffset? ExpiresAt);

/// <summary>
/// Durable notification projection. Notifications survive restarts (persisted rows,
/// never in-memory); redelivered source events deduplicate on
/// <c>(TenantId, RecipientUserId, SourceEventId)</c>, including the concurrent
/// race via the filtered unique index (CONFLICT → re-read). Recipients resolve
/// from <c>ProjectMembership</c> plus the project owner; with no membership the
/// owner is the fallback, and with neither the event is skipped with
/// <c>notifications.skipped_total</c>. Expired rows are excluded from listings
/// but retained for the retention job. Only ids and short summaries are stored.
/// </summary>
public sealed class NotificationProjector
{
    private readonly IStageExecutionContextFactory _contextFactory;

    public NotificationProjector(IStageExecutionContextFactory contextFactory)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        _contextFactory = contextFactory;
    }

    /// <summary>
    /// Projects one source event to all recipients. Idempotent per
    /// <c>SourceEventId</c>; returns the notifications created or already present.
    /// </summary>
    public async Task<IReadOnlyList<Notification>> ProjectAsync(
        NotificationInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        RequireTenant(input.TenantId);

        var title = Sanitize(input.Title, Notification.MaxTitleLength);
        var body = Sanitize(input.Body, Notification.MaxBodyLength);
        var resourceType = input.ResourceType.Trim();
        var resourceId = SanitizeResourceId(input.ResourceId);
        if (string.IsNullOrWhiteSpace(resourceType))
        {
            throw new DomainException("Notification ResourceType must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(resourceId))
        {
            throw new DomainException("Notification ResourceId must not be empty.");
        }

        using (TenantContext.BeginScope(input.TenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            var recipients = await ResolveRecipientsAsync(db, input, cancellationToken).ConfigureAwait(false);
            if (recipients.Count == 0)
            {
                NotificationMeters.Skipped.Add(1);
                return [];
            }

            if (input.SourceEventId.HasValue)
            {
                var existingRecipients = await db.Set<Notification>()
                    .Where(n => n.TenantId == input.TenantId && n.SourceEventId == input.SourceEventId)
                    .Where(n => recipients.Contains(n.RecipientUserId))
                    .Select(n => n.RecipientUserId)
                    .ToListAsync(cancellationToken).ConfigureAwait(false);
                recipients = recipients.Where(r => !existingRecipients.Contains(r)).ToList();
                if (recipients.Count == 0)
                {
                    return await db.Set<Notification>()
                        .Where(n => n.TenantId == input.TenantId && n.SourceEventId == input.SourceEventId)
                        .ToListAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            var now = DateTimeOffset.UtcNow;
            var created = new List<Notification>(recipients.Count);
            foreach (var recipient in recipients)
            {
                created.Add(new Notification(
                    Guid.NewGuid(), input.TenantId, recipient, input.ProjectId,
                    input.Type, input.Severity, title, body,
                    resourceType, resourceId, input.SourceEventId,
                    null, now, input.ExpiresAt));
            }

            foreach (var notification in created)
            {
                db.Set<Notification>().Add(notification);
            }

            try
            {
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (DomainException ex) when (input.SourceEventId.HasValue && ex.Message.Contains("CONFLICT", StringComparison.Ordinal))
            {
                db.ChangeTracker.Clear();
                return await db.Set<Notification>()
                    .Where(n => n.TenantId == input.TenantId && n.SourceEventId == input.SourceEventId)
                    .Where(n => recipients.Contains(n.RecipientUserId))
                    .ToListAsync(cancellationToken).ConfigureAwait(false);
            }

            return created;
        }
    }

    /// <summary>
    /// Lists unexpired notifications for one recipient, newest first.
    /// </summary>
    public async Task<(IReadOnlyList<Notification> Items, long Total)> ListActiveAsync(
        Guid tenantId,
        Guid recipientUserId,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        RequireTenant(tenantId);
        if (recipientUserId == Guid.Empty)
        {
            throw new DomainException("RecipientUserId must not be empty.");
        }

        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var now = DateTimeOffset.UtcNow;
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            var scoped = db.Set<Notification>().AsNoTracking()
                .Where(n => n.RecipientUserId == recipientUserId)
                .Where(n => n.ExpiresAt == null || n.ExpiresAt > now);
            var total = await scoped.LongCountAsync(cancellationToken).ConfigureAwait(false);
            var items = await scoped
                .OrderByDescending(n => n.CreatedAt)
                .ThenBy(n => n.Id)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            return (items, total);
        }
    }

    /// <summary>
    /// Marks one owned notification read. Throws <see cref="Exceptions.NotFoundException"/>
    /// when the row is missing or belongs to another recipient.
    /// </summary>
    public async Task MarkAsReadAsync(
        Guid tenantId,
        Guid recipientUserId,
        Guid notificationId,
        CancellationToken cancellationToken = default)
    {
        RequireTenant(tenantId);
        if (recipientUserId == Guid.Empty)
        {
            throw new DomainException("RecipientUserId must not be empty.");
        }

        if (notificationId == Guid.Empty)
        {
            throw new DomainException("NotificationId must not be empty.");
        }

        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            var notification = await db.Set<Notification>()
                .FirstOrDefaultAsync(
                    n => n.Id == notificationId && n.RecipientUserId == recipientUserId,
                    cancellationToken).ConfigureAwait(false);
            if (notification is null)
            {
                throw new Exceptions.NotFoundException($"Notification '{notificationId}' was not found.");
            }

            if (notification.ReadAt is null)
            {
                notification.MarkAsRead(DateTimeOffset.UtcNow);
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Sanitizes free text for storage: redacts secrets, strips URLs and bearer
    /// tokens, trims, and truncates to <paramref name="maxLength"/>. Pure.
    /// </summary>
    public static string Sanitize(string value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new DomainException("Notification text must not be empty.");
        }

        var withoutTokens = BearerRegex.Replace(value, "[redacted-token]");
        var redacted = SecretRedactor.Redact(withoutTokens) ?? withoutTokens;
        redacted = UrlRegex.Replace(redacted, "[redacted-url]");
        redacted = redacted.Trim();
        if (string.IsNullOrWhiteSpace(redacted))
        {
            throw new DomainException("Notification text must not be empty.");
        }

        return redacted.Length > maxLength ? redacted.Substring(0, maxLength) : redacted;
    }

    internal static string SanitizeResourceId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new DomainException("Notification ResourceId must not be empty.");
        }

        var trimmed = value.Trim();
        if (trimmed.Contains("http://", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("https://", StringComparison.OrdinalIgnoreCase))
        {
            throw new DomainException("Notification ResourceId must not contain URLs.");
        }

        if (trimmed.Contains("bearer ", StringComparison.OrdinalIgnoreCase))
        {
            throw new DomainException("Notification ResourceId must not contain tokens.");
        }

        return trimmed;
    }

    internal static async Task<List<Guid>> ResolveRecipientsAsync(
        DbContext db,
        NotificationInput input,
        CancellationToken cancellationToken)
    {
        if (input.ProjectId.HasValue)
        {
            var memberIds = await db.Set<ProjectMembership>()
                .AsNoTracking()
                .Where(m => m.ProjectId == input.ProjectId.Value)
                .Select(m => m.UserId)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            var ownerId = await db.Set<DubbingProject>()
                .AsNoTracking()
                .Where(p => p.Id == input.ProjectId.Value)
                .Select(p => p.OwnerUserId)
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            var recipients = new HashSet<Guid>(memberIds.Where(id => id != Guid.Empty));
            if (ownerId.HasValue && ownerId.Value != Guid.Empty)
            {
                recipients.Add(ownerId.Value);
            }

            return recipients.OrderBy(id => id).ToList();
        }

        return await db.Set<TenantUser>()
            .AsNoTracking()
            .Where(u => u.Status == TenantUserStatus.Active)
            .Select(u => u.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void RequireTenant(Guid tenantId)
    {
        if (tenantId == Guid.Empty)
        {
            throw new DomainException("TenantId must not be empty.");
        }
    }

    private static readonly Regex UrlRegex = new(
        "https?://\\S+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    private static readonly Regex BearerRegex = new(
        "bearer\\s+[^\\s,;\\]]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));
}
