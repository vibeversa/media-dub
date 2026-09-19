namespace DubbingPlatform.Application.Authorization;

/// <summary>
/// Authorization policy names. Each policy allows a hierarchical role set
/// (higher privilege implies lower): TenantAdmin &gt; ProjectOwner &gt;
/// ProjectEditor &gt; Reviewer &gt; ProjectViewer. Service is a machine role:
/// it satisfies every policy except the strict <see cref="RequireService"/>
/// (Service-only) gate; <see cref="RequireTenantAdmin"/> covers
/// Service+TenantAdmin admin endpoints.
/// </summary>
public static class AuthPolicies
{
    public const string RequireTenantAdmin = "RequireTenantAdmin";

    public const string RequireProjectOwner = "RequireProjectOwner";

    public const string RequireProjectEditor = "RequireProjectEditor";

    public const string RequireReviewer = "RequireReviewer";

    public const string RequireProjectViewer = "RequireProjectViewer";

    public const string RequireService = "RequireService";

    public static readonly string[] All =
    [
        RequireTenantAdmin,
        RequireProjectOwner,
        RequireProjectEditor,
        RequireReviewer,
        RequireProjectViewer,
        RequireService,
    ];

    /// <summary>
    /// Gets the allowed roles for a policy.
    /// </summary>
    public static IReadOnlyList<string> AllowedRoles(string policy)
    {
        return policy switch
        {
            RequireTenantAdmin => [Roles.TenantAdmin, Roles.Service],
            RequireProjectOwner => [Roles.TenantAdmin, Roles.ProjectOwner, Roles.Service],
            RequireProjectEditor => [Roles.TenantAdmin, Roles.ProjectOwner, Roles.ProjectEditor, Roles.Service],
            RequireReviewer => [Roles.TenantAdmin, Roles.ProjectOwner, Roles.ProjectEditor, Roles.Reviewer, Roles.Service],
            RequireProjectViewer => [Roles.TenantAdmin, Roles.ProjectOwner, Roles.ProjectEditor, Roles.Reviewer, Roles.ProjectViewer, Roles.Service],
            RequireService => [Roles.Service],
            _ => throw new ArgumentException($"Unknown policy '{policy}'.", nameof(policy)),
        };
    }
}
