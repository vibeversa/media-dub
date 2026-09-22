using DubbingPlatform.Application.Authorization;
using DubbingPlatform.Application.Exceptions;
using DubbingPlatform.Application.MultiTenancy;
using DubbingPlatform.Application.Services;
using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DubbingPlatform.Application.Diagnostics;

/// <summary>
/// Service-layer elevated-authz policy for the diagnostics read layer.
/// A diagnostics viewer holds the <c>TenantAdmin</c> or <c>Operator</c> role
/// or the <c>diagnostics.view</c> permission. Defense in depth: endpoints
/// re-check JWT roles/policies in Task 013; this guard is the service-layer
/// backstop so direct service use never leaks operator views.
/// </summary>
public static class DiagnosticsAccessPolicy
{
    /// <summary>Elevated tenant administrator role.</summary>
    public const string TenantAdminRole = Roles.TenantAdmin;

    /// <summary>Elevated operator role (JWT role claim; no project-role counterpart).</summary>
    public const string OperatorRole = "Operator";

    /// <summary>Machine service role, always a viewer.</summary>
    public const string ServiceRole = Roles.Service;

    /// <summary>Fine-grained diagnostics permission.</summary>
    public const string ViewPermission = "diagnostics.view";

    /// <summary>
    /// Sub-code marker carried in the denial message. The public error code
    /// stays <c>FORBIDDEN</c> (403): the catalog is frozen until Task 013.
    /// </summary>
    public const string ForbiddenMarker = "DIAGNOSTICS_FORBIDDEN";

    /// <summary>
    /// Determines whether the given roles/permissions satisfy elevated
    /// diagnostics viewership. Pure.
    /// </summary>
    public static bool IsElevatedViewer(IEnumerable<string> roles, IEnumerable<string> permissions)
    {
        ArgumentNullException.ThrowIfNull(roles);
        ArgumentNullException.ThrowIfNull(permissions);

        var roleSet = new HashSet<string>(roles.Where(r => !string.IsNullOrWhiteSpace(r)), StringComparer.Ordinal);
        if (roleSet.Contains(TenantAdminRole) || roleSet.Contains(OperatorRole) || roleSet.Contains(ServiceRole))
        {
            return true;
        }

        return permissions.Any(p => string.Equals(p?.Trim(), ViewPermission, StringComparison.Ordinal));
    }
}

/// <summary>
/// Service-layer guard for diagnostics queries. Implementations throw
/// <see cref="ForbiddenException"/> (403 FORBIDDEN, carrying the
/// <c>DIAGNOSTICS_FORBIDDEN</c> marker) when the caller is not a diagnostics
/// viewer. Tests substitute a fake; production resolves from membership state.
/// </summary>
public interface IDiagnosticsAccessChecker
{
    /// <summary>
    /// Requires diagnostics viewership for the tenant/user pair.
    /// </summary>
    Task RequireDiagnosticsViewerAsync(Guid tenantId, Guid userId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Default <see cref="IDiagnosticsAccessChecker"/> over membership state.
/// Grants viewership when the caller is an active tenant user holding a
/// <c>ProjectOwner</c> membership in the tenant: tenant-wide administration
/// has no dedicated membership table, and project ownership is the strongest
/// tenant-scoped elevation persisted in the database. JWT roles
/// (<c>TenantAdmin</c>/<c>Operator</c>) and the <c>diagnostics.view</c>
/// permission are re-checked at the endpoint layer (Task 013); callers that
/// resolve roles/permissions from JWT claims should evaluate
/// <see cref="DiagnosticsAccessPolicy.IsElevatedViewer"/> directly.
/// </summary>
public sealed class DiagnosticsAccessChecker : IDiagnosticsAccessChecker
{
    private readonly IStageExecutionContextFactory _contextFactory;
    private readonly ILogger<DiagnosticsAccessChecker> _logger;

    public DiagnosticsAccessChecker(
        IStageExecutionContextFactory contextFactory,
        ILogger<DiagnosticsAccessChecker> logger)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        ArgumentNullException.ThrowIfNull(logger);
        _contextFactory = contextFactory;
        _logger = logger;
    }

    public async Task RequireDiagnosticsViewerAsync(Guid tenantId, Guid userId, CancellationToken cancellationToken = default)
    {
        if (tenantId == Guid.Empty)
        {
            throw new Domain.Exceptions.DomainException("TenantId must not be empty.");
        }

        if (userId == Guid.Empty)
        {
            throw new Domain.Exceptions.DomainException("UserId must not be empty.");
        }

        bool granted;
        using (TenantContext.BeginMaintenanceScope())
        {
            using var db = _contextFactory.CreateDbContext();
            var user = await db.Set<TenantUser>()
                .AsNoTracking()
                .FirstOrDefaultAsync(u => u.Id == userId && u.TenantId == tenantId, cancellationToken)
                .ConfigureAwait(false);
            if (user is null || user.Status != TenantUserStatus.Active)
            {
                granted = false;
            }
            else
            {
                granted = await db.Set<ProjectMembership>()
                    .AsNoTracking()
                    .AnyAsync(m => m.TenantId == tenantId && m.UserId == userId && m.Role == ProjectRole.ProjectOwner, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        if (!granted)
        {
            _logger.LogWarning(
                "Diagnostics access denied. {TenantId} {UserId}",
                tenantId,
                userId);
            throw new ForbiddenException($"{DiagnosticsAccessPolicy.ForbiddenMarker}: user '{userId:D}' is not authorized for diagnostics in tenant '{tenantId:D}'.");
        }
    }
}
