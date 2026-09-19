namespace DubbingPlatform.Application.Authorization;

/// <summary>
/// Platform role names. Claims carry roles in <c>tid</c> (tenant), <c>sub</c> (user),
/// and <c>roles[]</c> (one claim per role; <c>role</c> and <c>ClaimTypes.Role</c> also accepted).
/// </summary>
public static class Roles
{
    public const string TenantAdmin = "TenantAdmin";

    public const string ProjectOwner = "ProjectOwner";

    public const string ProjectEditor = "ProjectEditor";

    public const string Reviewer = "Reviewer";

    public const string ProjectViewer = "ProjectViewer";

    public const string Service = "Service";

    public static readonly string[] All =
    [
        TenantAdmin,
        ProjectOwner,
        ProjectEditor,
        Reviewer,
        ProjectViewer,
        Service,
    ];

    public static bool IsKnown(string? role)
    {
        return !string.IsNullOrWhiteSpace(role) && All.Contains(role, StringComparer.Ordinal);
    }
}
