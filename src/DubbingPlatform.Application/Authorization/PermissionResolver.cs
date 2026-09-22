using DubbingPlatform.Application.MultiTenancy;
using DubbingPlatform.Application.Services;
using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DubbingPlatform.Application.Authorization;

/// <summary>
/// Resolves UX-hint permission strings from identity state. Registered scoped
/// so the per-instance cache is naturally per-request; no cross-request cache
/// exists (membership changes apply on the next request).
/// </summary>
public interface IPermissionResolver
{
    /// <summary>
    /// Resolves the permission set for a tenant/user pair.
    /// </summary>
    Task<IReadOnlySet<string>> ResolveAsync(Guid tenantId, Guid userId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Default <see cref="IPermissionResolver"/> over <c>TenantUser</c> status
/// plus <c>ProjectMembership</c> roles. A <c>Disabled</c> user resolves to
/// zero permissions. Role-to-permission mapping mirrors
/// <see cref="RoleMatrix"/>: Owner can delete/cancel/retry but never
/// <c>admin.manage</c> (TenantAdmin/Service only); Editor can edit/start/retry
/// but never delete/cancel; Reviewer can view and resolve reviews; Viewer can
/// only view and download. <c>diagnostics.view</c> follows the Task 005
/// elevation (ProjectOwner membership ≈ tenant-admin elevation) so operator
/// triage works without a dedicated membership table. JWT
/// <c>TenantAdmin</c>/<c>Service</c> roles grant the full set;
/// <c>Operator</c> grants <c>diagnostics.view</c> on top of the union.
/// </summary>
public sealed class PermissionResolver : IPermissionResolver
{
    private readonly IStageExecutionContextFactory _contextFactory;
    private readonly ILogger<PermissionResolver> _logger;
    private readonly Dictionary<(Guid TenantId, Guid UserId), IReadOnlySet<string>> _cache = new();

    public PermissionResolver(
        IStageExecutionContextFactory contextFactory,
        ILogger<PermissionResolver> logger)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        ArgumentNullException.ThrowIfNull(logger);
        _contextFactory = contextFactory;
        _logger = logger;
    }

    public async Task<IReadOnlySet<string>> ResolveAsync(Guid tenantId, Guid userId, CancellationToken cancellationToken = default)
    {
        if (_cache.TryGetValue((tenantId, userId), out var cached))
        {
            return cached;
        }

        TenantUserStatus status;
        List<ProjectRole> roles;
        List<string> jwtRoles = [];
        using (TenantContext.BeginMaintenanceScope())
        {
            using var db = _contextFactory.CreateDbContext();
            var user = await db.Set<TenantUser>()
                .AsNoTracking()
                .FirstOrDefaultAsync(u => u.Id == userId && u.TenantId == tenantId, cancellationToken)
                .ConfigureAwait(false);
            if (user is null)
            {
                var empty = new HashSet<string>(StringComparer.Ordinal);
                _cache[(tenantId, userId)] = empty;
                return empty;
            }

            status = user.Status;
            roles = await db.Set<ProjectMembership>()
                .AsNoTracking()
                .Where(m => m.TenantId == tenantId && m.UserId == userId)
                .Select(m => m.Role)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        var resolved = Resolve(status, roles, jwtRoles);
        _cache[(tenantId, userId)] = resolved;
        _logger.LogDebug(
            "Permissions resolved. {TenantId} {UserId} {Count}",
            tenantId,
            userId,
            resolved.Count);
        return resolved;
    }

    /// <summary>
    /// Pure permission resolution from status plus membership and JWT roles.
    /// Disabled status yields zero permissions.
    /// </summary>
    public static IReadOnlySet<string> Resolve(
        TenantUserStatus status,
        IEnumerable<ProjectRole> membershipRoles,
        IEnumerable<string>? jwtRoles = null)
    {
        ArgumentNullException.ThrowIfNull(membershipRoles);

        var result = new HashSet<string>(StringComparer.Ordinal);
        if (status == TenantUserStatus.Disabled)
        {
            return result;
        }

        var elevated = new HashSet<string>(
            (jwtRoles ?? []).Where(r => !string.IsNullOrWhiteSpace(r)),
            StringComparer.Ordinal);
        if (elevated.Contains(Roles.TenantAdmin) || elevated.Contains(Roles.Service))
        {
            return new HashSet<string>(Permissions.All, StringComparer.Ordinal);
        }

        var roles = new HashSet<ProjectRole>(membershipRoles);
        if (roles.Contains(ProjectRole.ProjectOwner))
        {
            result.UnionWith([
                Permissions.ProjectView,
                Permissions.ProjectEdit,
                Permissions.ProjectDelete,
                Permissions.ProcessingStart,
                Permissions.ProcessingCancel,
                Permissions.ProcessingRetry,
                Permissions.ReviewView,
                Permissions.ReviewResolve,
                Permissions.ExportCreate,
                Permissions.ExportDownload,
                Permissions.DiagnosticsView,
            ]);
        }

        if (roles.Contains(ProjectRole.ProjectEditor))
        {
            result.UnionWith([
                Permissions.ProjectView,
                Permissions.ProjectEdit,
                Permissions.ProcessingStart,
                Permissions.ProcessingRetry,
                Permissions.ReviewView,
                Permissions.ReviewResolve,
                Permissions.ExportCreate,
                Permissions.ExportDownload,
            ]);
        }

        if (roles.Contains(ProjectRole.Reviewer))
        {
            result.UnionWith([
                Permissions.ProjectView,
                Permissions.ReviewView,
                Permissions.ReviewResolve,
                Permissions.ExportDownload,
            ]);
        }

        if (roles.Contains(ProjectRole.ProjectViewer))
        {
            result.UnionWith([
                Permissions.ProjectView,
                Permissions.ReviewView,
                Permissions.ExportDownload,
            ]);
        }

        if (elevated.Contains("Operator"))
        {
            result.Add(Permissions.DiagnosticsView);
        }

        return result;
    }
}
