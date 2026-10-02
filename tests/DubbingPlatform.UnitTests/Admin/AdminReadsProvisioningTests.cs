// GAP-024: the admin panels (tenants, users/roles, retention, audit, feature
// flags) had no backend read, so every panel rendered "not provisioned on this
// backend yet". Plan B 12.19 lists those areas, so the reads are provisioned
// here; deployment-scoped surfaces (local-GPU device health, enrichment runtime)
// stay explicitly out of scope and that decision is pinned, not implicit.
using System.Reflection;
using DubbingPlatform.Api.Controllers;
using DubbingPlatform.Application.Authorization;

namespace DubbingPlatform.UnitTests.Admin;

public sealed class AdminReadsProvisioningTests
{
    private const string CanonicalReadRoutes =
        "GET /api/v1/admin/tenants|GET /api/v1/admin/users|GET /api/v1/admin/retention"
        + "|GET /api/v1/admin/feature-flags|GET /api/v1/admin/audit-events";

    [Theory]
    [InlineData("GetTenants", "tenants")]
    [InlineData("GetUsers", "users")]
    [InlineData("GetRetentionPolicies", "retention")]
    [InlineData("GetFeatureFlags", "feature-flags")]
    [InlineData("GetAuditEvents", "audit-events")]
    public void Admin_Read_Endpoints_Exist_And_Are_Elevated(string method, string routeFragment)
    {
        var endpoint = typeof(AdminController).GetMethod(method)
            ?? throw new InvalidOperationException($"AdminController.{method} is missing.");

        var route = endpoint.GetCustomAttribute<Microsoft.AspNetCore.Mvc.HttpGetAttribute>();
        Assert.NotNull(route);
        Assert.Equal(routeFragment, route!.Template);

        // Elevated-only, like every other admin read: no viewer access.
        Assert.Contains(
            CanonicalReadRoutes.Split('|'),
            r => r.EndsWith("/" + routeFragment, StringComparison.Ordinal) && RoleMatrix.IsAllowed(r, [Roles.Service]));
        Assert.Contains(
            CanonicalReadRoutes.Split('|'),
            r => r.EndsWith("/" + routeFragment, StringComparison.Ordinal) && !RoleMatrix.IsAllowed(r, [Roles.ProjectViewer]));
    }

    [Fact]
    public void Role_Matrix_Covers_Every_Provisioned_Admin_Read()
    {
        foreach (var route in CanonicalReadRoutes.Split('|'))
        {
            Assert.True(RoleMatrix.IsAllowed(route, [Roles.TenantAdmin]), $"{route} must allow TenantAdmin.");
            Assert.True(RoleMatrix.IsAllowed(route, [Roles.Service]), $"{route} must allow Service.");
            Assert.False(RoleMatrix.IsAllowed(route, [Roles.ProjectViewer]), $"{route} must deny ProjectViewer.");
        }
    }

    [Fact]
    public void OpenAPI_Lists_Every_Provisioned_Admin_Read()
    {
        var bundle = File.ReadAllText(Path.Combine(RepoRoot(), "src", "DubbingPlatform.Api", "OpenApi", "openapi.v1.json"));
        foreach (var route in CanonicalReadRoutes.Split('|'))
        {
            // Bundle paths are relative to the /api/v1 server root.
            var path = route.Replace("GET ", "", StringComparison.Ordinal).Replace("/api/v1", "", StringComparison.Ordinal);
            Assert.Contains($"\"{path}\"", bundle, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Scope_Decision_Is_Recorded_For_Deployment_Only_Surfaces()
    {
        // Plan B 12.19 lists the admin areas above. Local-GPU device health and
        // the enrichment runtime reads are deployment-scoped: the records must
        // say so explicitly instead of leaving a placeholder panel.
        var doc = File.ReadAllText(Path.Combine(RepoRoot(), "docs", "api-contract.md"));

        Assert.Contains("local-gpu", doc, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("out of scope", doc, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("enrichment", doc, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Panels_Degrade_On_404_But_No_Longer_Claim_Unprovisioned_For_Provisioned_Reads()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "frontend", "src", "features", "admin", "useAdminQueries.ts"));

        foreach (var route in new[] { "/admin/tenants", "/admin/users", "/admin/retention", "/admin/feature-flags" })
        {
            Assert.Contains($"'{route}'", source, StringComparison.Ordinal);
        }

        // The 404-tolerant optional-read path stays: it is what keeps a
        // not-yet-provisioned deployment on EmptyState instead of a red box.
        Assert.Contains("useOptionalAdminList", source, StringComparison.Ordinal);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DubbingPlatform.sln")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return dir!.FullName;
    }
}