using System.Security.Claims;

namespace DubbingPlatform.Application.Authorization;

/// <summary>
/// Endpoint-to-role matrix. Keys are <c>METHOD path-prefix</c> entries used for
/// documentation and OpenAPI hints; runtime enforcement is via
/// <c>[Authorize(Policy=...)]</c> plus the project-ownership resource check.
/// Admin/diagnostics entries list the JWT role gate; <c>Operator</c> JWT roles
/// and <c>diagnostics.view</c>/<c>admin.manage</c> permission holders
/// (ProjectOwner membership) additionally pass the Task 013 endpoint check plus
/// the Task 005 service guard (defense in depth).
/// Matrix (hierarchical, Service satisfies all except strict Service-only):
/// POST projects (create): TenantAdmin/ProjectOwner/ProjectEditor;
/// GET projects (read/list): +Reviewer/Viewer; DELETE projects: TenantAdmin/Owner;
/// reviews approve: Reviewer and above; admin: Service/TenantAdmin.
/// </summary>
public static class RoleMatrix
{
    public static readonly IReadOnlyDictionary<string, string[]> Endpoints =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["POST /api/v1/projects"] = [Roles.TenantAdmin, Roles.ProjectOwner, Roles.ProjectEditor],
            ["GET /api/v1/projects"] = [Roles.TenantAdmin, Roles.ProjectOwner, Roles.ProjectEditor, Roles.Reviewer, Roles.ProjectViewer],
            ["DELETE /api/v1/projects"] = [Roles.TenantAdmin, Roles.ProjectOwner],
            ["POST /api/v1/uploads"] = [Roles.TenantAdmin, Roles.ProjectOwner, Roles.ProjectEditor],
            ["GET /api/v1/uploads"] = [Roles.TenantAdmin, Roles.ProjectOwner, Roles.ProjectEditor, Roles.Reviewer, Roles.ProjectViewer],
            ["POST /api/v1/processing"] = [Roles.TenantAdmin, Roles.ProjectOwner, Roles.ProjectEditor],
            ["POST /api/v1/processing/cancel"] = [Roles.TenantAdmin, Roles.ProjectOwner],
            ["POST /api/v1/processing/retry"] = [Roles.TenantAdmin, Roles.ProjectOwner, Roles.ProjectEditor],
            ["GET /api/v1/segments"] = [Roles.TenantAdmin, Roles.ProjectOwner, Roles.ProjectEditor, Roles.Reviewer, Roles.ProjectViewer],
            ["GET /api/v1/speakers"] = [Roles.TenantAdmin, Roles.ProjectOwner, Roles.ProjectEditor, Roles.Reviewer, Roles.ProjectViewer],
            ["PUT /api/v1/speakers/voice-assignment"] = [Roles.TenantAdmin, Roles.ProjectOwner, Roles.ProjectEditor],
            ["POST /api/v1/voice-previews"] = [Roles.TenantAdmin, Roles.ProjectOwner, Roles.ProjectEditor],
            ["GET /api/v1/voice-previews"] = [Roles.TenantAdmin, Roles.ProjectOwner, Roles.ProjectEditor, Roles.Reviewer, Roles.ProjectViewer],
            ["GET /api/v1/reviews"] = [Roles.TenantAdmin, Roles.ProjectOwner, Roles.ProjectEditor, Roles.Reviewer, Roles.ProjectViewer],
            ["POST /api/v1/reviews/approve"] = [Roles.TenantAdmin, Roles.ProjectOwner, Roles.ProjectEditor, Roles.Reviewer],
            ["POST /api/v1/reviews/resolve"] = [Roles.TenantAdmin, Roles.ProjectOwner, Roles.ProjectEditor, Roles.Reviewer],
            ["POST /api/v1/reviews/dismiss"] = [Roles.TenantAdmin, Roles.ProjectOwner, Roles.ProjectEditor, Roles.Reviewer],
            ["POST /api/v1/reviews/reopen"] = [Roles.TenantAdmin, Roles.ProjectOwner, Roles.ProjectEditor, Roles.Reviewer],
            ["POST /api/v1/reviews/resolve-with-edit"] = [Roles.TenantAdmin, Roles.ProjectOwner, Roles.ProjectEditor, Roles.Reviewer],
            ["POST /api/v1/exports"] = [Roles.TenantAdmin, Roles.ProjectOwner, Roles.ProjectEditor],
            ["GET /api/v1/output"] = [Roles.TenantAdmin, Roles.ProjectOwner, Roles.ProjectEditor, Roles.Reviewer, Roles.ProjectViewer],
            ["GET /api/v1/exports"] = [Roles.TenantAdmin, Roles.ProjectOwner, Roles.ProjectEditor, Roles.Reviewer, Roles.ProjectViewer],
            ["GET /api/v1/notifications"] = [Roles.TenantAdmin, Roles.ProjectOwner, Roles.ProjectEditor, Roles.Reviewer, Roles.ProjectViewer],
            ["POST /api/v1/notifications"] = [Roles.TenantAdmin, Roles.ProjectOwner, Roles.ProjectEditor, Roles.Reviewer, Roles.ProjectViewer],
            ["GET /api/v1/admin"] = [Roles.Service, Roles.TenantAdmin],
            ["POST /api/v1/admin"] = [Roles.Service, Roles.TenantAdmin],
            ["GET /api/v1/admin/usage"] = [Roles.Service, Roles.TenantAdmin],
            ["GET /api/v1/admin/quotas"] = [Roles.Service, Roles.TenantAdmin],
            ["GET /api/v1/admin/provider-health"] = [Roles.Service, Roles.TenantAdmin],
            ["GET /api/v1/admin/provider-routes"] = [Roles.Service, Roles.TenantAdmin],
            ["GET /api/v1/admin/diagnostics/queues"] = [Roles.Service, Roles.TenantAdmin],
            ["GET /api/v1/admin/diagnostics/dlq"] = [Roles.Service, Roles.TenantAdmin],
            ["GET /api/v1/admin/diagnostics/leases"] = [Roles.Service, Roles.TenantAdmin],
            ["GET /api/v1/admin/diagnostics/orphans"] = [Roles.Service, Roles.TenantAdmin],
            ["GET /api/v1/admin/diagnostics/review-backlog"] = [Roles.Service, Roles.TenantAdmin],

            // GAP-020: worker health (lease-derived roster) is operator
            // infrastructure state, same gate as the other diagnostics reads.
            ["GET /api/v1/admin/diagnostics/workers"] = [Roles.Service, Roles.TenantAdmin],
            // Task 044 (Plan B §19.3): operator-only local-GPU health. Device
            // inventory — accelerator model, model revision, device count,
            // per-device latency — is infrastructure detail. It is not secret,
            // and it is equally not appropriate for an ordinary project member,
            // so the entry is restricted to the same two roles every other
            // admin read uses and to nothing below. The frontend reads
            // `/admin/local-gpu` through `apiFetch` and degrades on the
            // controller's `ADMIN_ROUTE_UNKNOWN` 404 until the route is
            // provisioned; see `LocalGpuPanel`.
            ["GET /api/v1/admin/local-gpu"] = [Roles.Service, Roles.TenantAdmin],
        };

    /// <summary>
    /// Determines whether the given user roles satisfy the matrix entry for an endpoint.
    /// Service satisfies every entry (it is listed on admin entries and implied on the rest
    /// via the hierarchical policies).
    /// </summary>
    public static bool IsAllowed(string endpoint, IEnumerable<string> userRoles)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        ArgumentNullException.ThrowIfNull(userRoles);

        if (!Endpoints.TryGetValue(endpoint, out var allowed))
        {
            return false;
        }

        var roles = new HashSet<string>(userRoles.Where(r => !string.IsNullOrWhiteSpace(r)), StringComparer.Ordinal);
        if (roles.Contains(Roles.Service))
        {
            return true;
        }

        return allowed.Any(roles.Contains);
    }

    /// <summary>
    /// Gets the allowed roles for an endpoint, or an empty list when unknown.
    /// </summary>
    public static IReadOnlyList<string> AllowedFor(string endpoint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        return Endpoints.TryGetValue(endpoint, out var allowed) ? allowed : [];
    }
}
