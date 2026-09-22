using System.Text.Json;
using System.Text.RegularExpressions;
using DubbingPlatform.Application.MultiTenancy;
using DubbingPlatform.Application.Security;
using DubbingPlatform.Application.Services;
using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Domain.Enums;
using DubbingPlatform.Domain.Exceptions;
using Microsoft.EntityFrameworkCore;

namespace DubbingPlatform.Application.Activity;

/// <summary>
/// Append-only activity input. Summaries are short human-readable strings with
/// ids and counts only; metadata carries ids, counts, and hashes, never media,
/// text, URLs, or secrets (redacted and validated before storage).
/// </summary>
public sealed record ActivityInput(
    Guid TenantId,
    Guid? ProjectId,
    Guid? ProcessingRunId,
    ActivityType Type,
    ActivityActorType ActorType,
    Guid? ActorUserId,
    string Summary,
    ActivitySeverity Severity,
    string CorrelationId,
    DateTimeOffset OccurredAt,
    string? MetadataJson);

/// <summary>
/// Append-only activity projection with paginated reads by
/// <c>(ProjectId, OccurredAt)</c>. No update or delete paths exist; retention
/// deletion lives with the retention job. Redelivered source events deduplicate
/// on <c>(TenantId, CorrelationId, Type, ProjectId, ProcessingRunId,
/// OccurredAt)</c> so at-least-once delivery never double-appends.
/// Security-relevant actions (review requested/resolved, edits, exports) also
/// append an <see cref="AuditEvent"/> in the same scope.
/// </summary>
public sealed class ActivityProjector
{
    private static readonly Regex UrlRegex = new(
        "https?://\\S+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    private static readonly Regex BearerRegex = new(
        "bearer\\s+[^\\s,;\\]]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IStageExecutionContextFactory _contextFactory;

    public ActivityProjector(IStageExecutionContextFactory contextFactory)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        _contextFactory = contextFactory;
    }

    /// <summary>
    /// Appends one activity event. Idempotent for redeliveries: an existing row
    /// with the same tenant, correlation, type, project, run, and timestamp is
    /// returned instead of appended.
    /// </summary>
    public async Task<ActivityEvent> AppendAsync(
        ActivityInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        RequireTenant(input.TenantId);

        var summary = Sanitize(input.Summary, ActivityEvent.MaxSummaryLength);
        var correlationId = Sanitize(input.CorrelationId, ActivityEvent.MaxCorrelationIdLength);
        var metadata = SanitizeMetadata(input.MetadataJson);

        using (TenantContext.BeginScope(input.TenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            var existing = await db.Set<ActivityEvent>()
                .FirstOrDefaultAsync(
                    e => e.CorrelationId == correlationId
                        && e.Type == input.Type
                        && e.ProjectId == input.ProjectId
                        && e.ProcessingRunId == input.ProcessingRunId
                        && e.OccurredAt == input.OccurredAt,
                    cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                return existing;
            }

            var @event = new ActivityEvent(
                Guid.NewGuid(), input.TenantId, input.ProjectId, input.ProcessingRunId,
                input.Type, input.ActorType, input.ActorUserId, summary,
                input.Severity, correlationId, input.OccurredAt,
                ActivityEvent.SupportedSchemaVersion, metadata);
            db.Set<ActivityEvent>().Add(@event);

            if (IsSecurityRelevant(input.Type))
            {
                db.Set<AuditEvent>().Add(new AuditEvent(
                    Guid.NewGuid(), input.TenantId, input.ProjectId,
                    AuditActorFor(input), AuditActionFor(input.Type),
                    input.ProjectId.HasValue ? "DubbingProject" : "Tenant",
                    (input.ProjectId ?? input.TenantId).ToString("D"),
                    SecretRedactor.Redact(metadata),
                    DateTimeOffset.UtcNow));
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return @event;
        }
    }

    /// <summary>
    /// Paginated activity for one project, oldest first.
    /// </summary>
    public async Task<(IReadOnlyList<ActivityEvent> Items, long Total)> ListAsync(
        Guid tenantId,
        Guid projectId,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        RequireTenant(tenantId);
        if (projectId == Guid.Empty)
        {
            throw new DomainException("ProjectId must not be empty.");
        }

        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            var scoped = db.Set<ActivityEvent>().AsNoTracking()
                .Where(e => e.ProjectId == projectId);
            var total = await scoped.LongCountAsync(cancellationToken).ConfigureAwait(false);
            var items = await scoped
                .OrderBy(e => e.OccurredAt)
                .ThenBy(e => e.Id)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            return (items, total);
        }
    }

    /// <summary>
    /// Whether the action is security-relevant and must also append an audit
    /// event. Pure.
    /// </summary>
    public static bool IsSecurityRelevant(ActivityType type)
    {
        return type is ActivityType.ReviewRequested
            or ActivityType.ReviewResolved
            or ActivityType.EditApplied
            or ActivityType.ExportCompleted
            or ActivityType.ExportFailed;
    }

    /// <summary>
    /// Audit action for a security-relevant activity type. Pure.
    /// </summary>
    public static string AuditActionFor(ActivityType type)
    {
        return type switch
        {
            ActivityType.ReviewRequested => "review.requested",
            ActivityType.ReviewResolved => "review.resolved",
            ActivityType.EditApplied => "review.edit_applied",
            ActivityType.ExportCompleted => "export.completed",
            ActivityType.ExportFailed => "export.failed",
            _ => string.Concat("activity.", type.ToString().ToLowerInvariant()),
        };
    }

    /// <summary>
    /// Builds sanitized metadata JSON from id/count/hash entries. Sensitive keys
    /// are redacted; URLs and bearer tokens are stripped. Pure.
    /// </summary>
    public static string? BuildMetadata(IEnumerable<KeyValuePair<string, object?>>? entries)
    {
        if (entries is null)
        {
            return null;
        }

        var redacted = SecretRedactor.RedactDetails(entries);
        var cleaned = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var pair in redacted)
        {
            cleaned[pair.Key] = pair.Value is string text
                ? UrlRegex.Replace(BearerRegex.Replace(text, "[redacted-token]"), "[redacted-url]")
                : pair.Value;
        }

        return JsonSerializer.Serialize(cleaned, JsonOptions);
    }

    /// <summary>
    /// Sanitizes free text: redacts secrets, strips URLs, trims, truncates. Pure.
    /// </summary>
    public static string Sanitize(string value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new DomainException("Activity text must not be empty.");
        }

        var withoutTokens = BearerRegex.Replace(value, "[redacted-token]");
        var redacted = SecretRedactor.Redact(withoutTokens) ?? withoutTokens;
        redacted = UrlRegex.Replace(redacted, "[redacted-url]");
        redacted = redacted.Trim();
        if (string.IsNullOrWhiteSpace(redacted))
        {
            throw new DomainException("Activity text must not be empty.");
        }

        return redacted.Length > maxLength ? redacted.Substring(0, maxLength) : redacted;
    }

    internal static string? SanitizeMetadata(string? metadataJson)
    {
        if (metadataJson is null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(metadataJson))
        {
            throw new DomainException("ActivityEvent MetadataJson must not be empty when set.");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(metadataJson);
        }
        catch (JsonException ex)
        {
            throw new DomainException("ActivityEvent MetadataJson must be valid JSON.", ex);
        }

        using (document)
        {
            var withoutTokens = BearerRegex.Replace(metadataJson, "[redacted-token]");
            var redacted = SecretRedactor.Redact(withoutTokens) ?? withoutTokens;
            redacted = UrlRegex.Replace(redacted, "[redacted-url]");
            try
            {
                using var _ = JsonDocument.Parse(redacted);
            }
            catch (JsonException ex)
            {
                throw new DomainException("ActivityEvent MetadataJson must be valid JSON after sanitization.", ex);
            }

            return redacted;
        }
    }

    private static string AuditActorFor(ActivityInput input)
    {
        return input.ActorUserId.HasValue && input.ActorUserId.Value != Guid.Empty
            ? input.ActorUserId.Value.ToString("D")
            : input.ActorType.ToString().ToLowerInvariant();
    }

    private static void RequireTenant(Guid tenantId)
    {
        if (tenantId == Guid.Empty)
        {
            throw new DomainException("TenantId must not be empty.");
        }
    }
}
