using Microsoft.EntityFrameworkCore;

namespace DubbingPlatform.Application.Common;

/// <summary>
/// Marker for tenant-scoped entities carrying a <c>TenantId</c>.
/// </summary>
public interface ITenantScoped
{
    Guid TenantId { get; }
}

/// <summary>
/// Tenant-scoping helper for repositories and controllers. Primary enforcement
/// remains PostgreSQL RLS plus <c>AppDbContext</c> query filters; this helper is
/// defense in depth for explicit queries. Uses <c>EF.Property&lt;Guid&gt;</c> so it
/// works for every entity with a <c>TenantId</c> without requiring
/// <see cref="ITenantScoped"/>.
/// </summary>
public static class TenantQueryFilter
{
    /// <summary>
    /// Applies <c>e.TenantId == tenantId</c> to any queryable with a TenantId shadow/CLR property.
    /// </summary>
    public static IQueryable<T> ApplyTenant<T>(IQueryable<T> query, Guid tenantId)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(query);
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("TenantId must not be empty.", nameof(tenantId));
        }

        return query.Where(e => EF.Property<Guid>(e, "TenantId") == tenantId);
    }

    /// <summary>
    /// Applies tenant scoping for <see cref="ITenantScoped"/> entities.
    /// </summary>
    public static IQueryable<T> ApplyTenantScoped<T>(IQueryable<T> query, Guid tenantId)
        where T : class, ITenantScoped
    {
        ArgumentNullException.ThrowIfNull(query);
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("TenantId must not be empty.", nameof(tenantId));
        }

        return query.Where(e => e.TenantId == tenantId);
    }
}
