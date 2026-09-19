using DubbingPlatform.Application.MultiTenancy;
using DubbingPlatform.Application.Security;
using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Domain.Exceptions;
using Microsoft.EntityFrameworkCore;

namespace DubbingPlatform.Application.Services;

/// <summary>
/// Append-only audit log. Every privileged operation appends an
/// <see cref="AuditEvent"/>; events are never updated or deleted by this
/// service (retention-owned deletion lives in 037). Detail payloads pass through
/// <see cref="SecretRedactor"/> so keys, tokens, and credentials never persist.
/// </summary>
public sealed class AuditService
{
    private readonly IStageExecutionContextFactory _contextFactory;

    public AuditService(IStageExecutionContextFactory contextFactory)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        _contextFactory = contextFactory;
    }

    /// <summary>
    /// Appends an audit event.
    /// </summary>
    public async Task<Guid> LogAsync(
        Guid tenantId,
        Guid? projectId,
        string actor,
        string action,
        string resourceType,
        string resourceId,
        string? details,
        CancellationToken cancellationToken = default)
    {
        if (tenantId == Guid.Empty)
        {
            throw new DomainException("TenantId must not be empty.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceType);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceId);

        var redacted = SecretRedactor.Redact(details);
        if (redacted is not null && string.IsNullOrWhiteSpace(redacted))
        {
            redacted = null;
        }

        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var @event = new AuditEvent(id, tenantId, projectId, actor.Trim(), action.Trim(), resourceType.Trim(), resourceId.Trim(), redacted, now);

        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            db.Set<AuditEvent>().Add(@event);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        return id;
    }
}
