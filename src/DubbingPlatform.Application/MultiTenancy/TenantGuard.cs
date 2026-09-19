using DubbingPlatform.Application.Exceptions;
using DubbingPlatform.Application.Security;
using DubbingPlatform.Domain.Exceptions;

namespace DubbingPlatform.Application.MultiTenancy;

/// <summary>
/// Defense-in-depth tenant ownership guard. Every service method that loads a
/// tenant-scoped row must call <see cref="AssertMatch"/> before acting on it:
/// the application query filters plus PostgreSQL RLS remain the primary
/// enforcement, and this guard turns a missed filter into a deterministic 403
/// instead of a cross-tenant side effect. Rejections increment
/// <c>security.cross_tenant_rejected</c> (see <see cref="SecurityMeters"/>).
/// Message consumers validate the same way (see
/// <c>MessageDisposition.Decide</c>); <c>BaseConsumer</c> records both the
/// messaging and the security counters on reject. Never logs resource ids
/// beyond the fact of rejection: only the mismatch itself.
/// </summary>
public static class TenantGuard
{
    /// <summary>
    /// Whether the caller tenant owns the resource tenant. Pure.
    /// Empty ids never match (fail closed).
    /// </summary>
    public static bool IsMatch(Guid callerTenantId, Guid resourceTenantId)
    {
        if (callerTenantId == Guid.Empty || resourceTenantId == Guid.Empty)
        {
            return false;
        }

        return callerTenantId == resourceTenantId;
    }

    /// <summary>
    /// Throws <see cref="ForbiddenException"/> (403 FORBIDDEN) when
    /// <paramref name="callerTenantId"/> does not own
    /// <paramref name="resourceTenantId"/>, recording the security rejection
    /// metric. Empty ids fail closed (deny).
    /// </summary>
    public static void AssertMatch(Guid callerTenantId, Guid resourceTenantId)
    {
        if (IsMatch(callerTenantId, resourceTenantId))
        {
            return;
        }

        SecurityMeters.CrossTenantRejects.Add(1);
        throw new ForbiddenException("The requested resource does not belong to the current tenant.");
    }

    /// <summary>
    /// Throws <see cref="DomainException"/> when <paramref name="tenantId"/>
    /// is empty (fail closed for callers that have no tenant at all, e.g. RLS
    /// without a tenant denies). Pure validation, no metric.
    /// </summary>
    public static void RequireTenant(Guid tenantId)
    {
        if (tenantId == Guid.Empty)
        {
            throw new DomainException("TenantId must not be empty.");
        }
    }
}
