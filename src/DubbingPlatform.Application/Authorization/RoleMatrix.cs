using System.Security.Claims;

namespace DubbingPlatform.Application.Authorization;

/// <summary>
/// Endpoint-to-role matrix. Keys are <c>METHOD path-prefix</c> entries used for
/// documentation and OpenAPI hints; runtime enforcement is via
/// <c>[Authorize(Policy=...)]</c> plus the project-ownership resource check.
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
            ["GET /api/v1/reviews"] = [Roles.TenantAdmin, Roles.ProjectOwner, Roles.ProjectEditor, Roles.Reviewer, Roles.ProjectViewer],
            ["POST /api/v1/reviews/approve"] = [Roles.TenantAdmin, Roles.ProjectOwner, Roles.ProjectEditor, Roles.Reviewer],
            ["POST /api/v1/exports"] = [Roles.TenantAdmin, Roles.ProjectOwner, Roles.ProjectEditor],
            ["GET /api/v1/output"] = [Roles.TenantAdmin, Roles.ProjectOwner, Roles.ProjectEditor, Roles.Reviewer, Roles.ProjectViewer],
            ["GET /api/v1/admin"] = [Roles.Service, Roles.TenantAdmin],
            ["POST /api/v1/admin"] = [Roles.Service, Roles.TenantAdmin],
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
