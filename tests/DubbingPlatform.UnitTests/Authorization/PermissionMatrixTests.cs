// Task 039C: authorization unit gap closure.
using System.Security.Claims;
using DubbingPlatform.Application.Authorization;
using DubbingPlatform.Application.Errors;
using DubbingPlatform.Application.Exceptions;
using DubbingPlatform.Application.MultiTenancy;
using DubbingPlatform.Application.Services;
using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Domain.Enums;
using DubbingPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using PlatformClaimTypes = DubbingPlatform.Application.Authorization.ClaimTypes;
using SystemClaimTypes = System.Security.Claims.ClaimTypes;

namespace DubbingPlatform.UnitTests.Authorization;

/// <summary>
/// Authorization unit coverage: the <see cref="PermissionResolver"/> pure
/// resolution matrix, the per-instance resolve cache and tenant scoping (via the
/// in-process EF InMemory provider already used by this suite - no database, no
/// container, no network), plus the <see cref="RoleMatrix"/>,
/// <see cref="Roles"/>, <see cref="Permissions"/>, <see cref="AuthPolicies"/>
/// contracts and the <see cref="ClaimsPrincipalExtensions"/> tenant/user/role
/// extraction helpers.
/// </summary>
public sealed class PermissionMatrixTests
{
    private static readonly string[] NoRoles = [];

    private static readonly ProjectRole[] NoMemberships = [];

    [Fact]
    public void Disabled_User_Resolves_To_No_Permissions_Even_With_Membership_Or_Jwt_Roles()
    {
        var allMemberships = new[]
        {
            ProjectRole.ProjectOwner, ProjectRole.ProjectEditor,
            ProjectRole.Reviewer, ProjectRole.ProjectViewer,
        };

        Assert.Empty(PermissionResolver.Resolve(TenantUserStatus.Disabled, allMemberships));
        Assert.Empty(PermissionResolver.Resolve(TenantUserStatus.Disabled, allMemberships, [Roles.TenantAdmin]));
        Assert.Empty(PermissionResolver.Resolve(TenantUserStatus.Disabled, allMemberships, [Roles.Service]));
        Assert.Empty(PermissionResolver.Resolve(TenantUserStatus.Disabled, allMemberships, ["Operator"]));
        Assert.Empty(PermissionResolver.Resolve(TenantUserStatus.Disabled, NoMemberships));
    }

    [Theory]
    [InlineData(Roles.TenantAdmin)]
    [InlineData(Roles.Service)]
    public void Tenant_Admin_And_Service_Jwt_Roles_Resolve_All_Permissions(string jwtRole)
    {
        var resolved = PermissionResolver.Resolve(TenantUserStatus.Active, NoMemberships, [jwtRole]);

        AssertPermissionsEqual(Permissions.All, resolved);
    }

    [Fact]
    public void Tenant_Admin_Elevation_Ignores_Membership_And_Wins_Over_Operator()
    {
        var resolved = PermissionResolver.Resolve(
            TenantUserStatus.Active,
            [ProjectRole.ProjectViewer],
            ["Operator", Roles.TenantAdmin]);

        AssertPermissionsEqual(Permissions.All, resolved);
    }

    [Fact]
    public void Project_Owner_Membership_Resolves_To_The_Exact_Elevated_Set()
    {
        var resolved = PermissionResolver.Resolve(TenantUserStatus.Active, [ProjectRole.ProjectOwner]);

        string[] expected =
        [
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
        ];

        AssertPermissionsEqual(expected, resolved);

        // Owner is never a tenant admin: the admin-only permission stays absent.
        Assert.DoesNotContain(Permissions.AdminManage, resolved);
        Assert.Equal(Permissions.All.Length - 1, resolved.Count);
    }

    [Fact]
    public void Project_Editor_Membership_Resolves_To_The_Exact_Set()
    {
        var resolved = PermissionResolver.Resolve(TenantUserStatus.Active, [ProjectRole.ProjectEditor]);

        string[] expected =
        [
            Permissions.ProjectView,
            Permissions.ProjectEdit,
            Permissions.ProcessingStart,
            Permissions.ProcessingRetry,
            Permissions.ReviewView,
            Permissions.ReviewResolve,
            Permissions.ExportCreate,
            Permissions.ExportDownload,
        ];

        AssertPermissionsEqual(expected, resolved);
        Assert.DoesNotContain(Permissions.ProjectDelete, resolved);
        Assert.DoesNotContain(Permissions.ProcessingCancel, resolved);
        Assert.DoesNotContain(Permissions.DiagnosticsView, resolved);
    }

    [Fact]
    public void Reviewer_Membership_Resolves_To_View_And_Resolve_Only()
    {
        var resolved = PermissionResolver.Resolve(TenantUserStatus.Active, [ProjectRole.Reviewer]);

        string[] expected =
        [
            Permissions.ProjectView,
            Permissions.ReviewView,
            Permissions.ReviewResolve,
            Permissions.ExportDownload,
        ];

        AssertPermissionsEqual(expected, resolved);
        Assert.DoesNotContain(Permissions.ProjectEdit, resolved);
        Assert.DoesNotContain(Permissions.ExportCreate, resolved);
    }

    [Fact]
    public void Project_Viewer_Membership_Resolves_To_Read_Only()
    {
        var resolved = PermissionResolver.Resolve(TenantUserStatus.Active, [ProjectRole.ProjectViewer]);

        string[] expected =
        [
            Permissions.ProjectView,
            Permissions.ReviewView,
            Permissions.ExportDownload,
        ];

        AssertPermissionsEqual(expected, resolved);
        Assert.DoesNotContain(Permissions.ProjectEdit, resolved);
        Assert.DoesNotContain(Permissions.ReviewResolve, resolved);
        Assert.DoesNotContain(Permissions.ExportCreate, resolved);
    }

    [Fact]
    public void No_Membership_Resolves_To_No_Permissions()
    {
        Assert.Empty(PermissionResolver.Resolve(TenantUserStatus.Active, NoMemberships));
        Assert.Empty(PermissionResolver.Resolve(TenantUserStatus.Active, NoMemberships, null));
        Assert.Empty(PermissionResolver.Resolve(TenantUserStatus.Active, NoMemberships, NoRoles));
    }

    [Theory]
    [InlineData(ProjectRole.ProjectOwner, ProjectRole.ProjectViewer)]
    [InlineData(ProjectRole.ProjectEditor, ProjectRole.Reviewer)]
    [InlineData(ProjectRole.Reviewer, ProjectRole.ProjectViewer)]
    [InlineData(ProjectRole.ProjectOwner, ProjectRole.Reviewer)]
    [InlineData(ProjectRole.ProjectEditor, ProjectRole.ProjectViewer)]
    public void Multiple_Memberships_Resolve_To_The_Union(ProjectRole first, ProjectRole second)
    {
        var forward = PermissionResolver.Resolve(TenantUserStatus.Active, [first, second]);
        var reverse = PermissionResolver.Resolve(TenantUserStatus.Active, [second, first]);

        AssertPermissionsEqual(forward, reverse);

        // The union is exactly the set union of the two single-role resolutions.
        var firstAlone = PermissionResolver.Resolve(TenantUserStatus.Active, [first]);
        var secondAlone = PermissionResolver.Resolve(TenantUserStatus.Active, [second]);
        var union = new HashSet<string>(firstAlone, StringComparer.Ordinal);
        union.UnionWith(secondAlone);

        AssertPermissionsEqual(union, forward);
    }

    [Fact]
    public void Duplicate_Memberships_Deduplicate()
    {
        var once = PermissionResolver.Resolve(TenantUserStatus.Active, [ProjectRole.Reviewer]);
        var thrice = PermissionResolver.Resolve(
            TenantUserStatus.Active,
            [ProjectRole.Reviewer, ProjectRole.Reviewer, ProjectRole.Reviewer]);

        AssertPermissionsEqual(once, thrice);
    }

    [Fact]
    public void Operator_Jwt_Role_Adds_Diagnostics_View_On_Top_Of_Membership()
    {
        string[] operatorOnly = [Permissions.DiagnosticsView];
        AssertPermissionsEqual(
            operatorOnly,
            PermissionResolver.Resolve(TenantUserStatus.Active, NoMemberships, ["Operator"]));

        string[] reviewerPlusOperator =
        [
            Permissions.ProjectView,
            Permissions.ReviewView,
            Permissions.ReviewResolve,
            Permissions.ExportDownload,
            Permissions.DiagnosticsView,
        ];
        AssertPermissionsEqual(
            reviewerPlusOperator,
            PermissionResolver.Resolve(TenantUserStatus.Active, [ProjectRole.Reviewer], ["Operator"]));

        // Owner already holds diagnostics.view: the elevation is idempotent.
        var ownerPlusOperator = PermissionResolver.Resolve(
            TenantUserStatus.Active,
            [ProjectRole.ProjectOwner],
            ["Operator"]);

        AssertPermissionsEqual(
            PermissionResolver.Resolve(TenantUserStatus.Active, [ProjectRole.ProjectOwner]),
            ownerPlusOperator);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("operator")]
    [InlineData("OPERATOR")]
    [InlineData("tenantadmin")]
    [InlineData("Unknown")]
    public void Blank_Or_Unknown_Jwt_Roles_Never_Elevate(string? jwtRole)
    {
        var resolved = PermissionResolver.Resolve(TenantUserStatus.Active, NoMemberships, [jwtRole!]);

        Assert.Empty(resolved);
    }

    [Fact]
    public void Null_Membership_Roles_Is_Rejected()
    {
        Assert.Throws<ArgumentNullException>(
            () => PermissionResolver.Resolve(TenantUserStatus.Active, null!));
    }

    [Fact]
    public async Task Resolve_Async_Unknown_User_Resolves_Empty_And_Caches()
    {
        using var factory = CreateFactory();
        var resolver = CreateResolver(factory);
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        var first = await resolver.ResolveAsync(tenantId, userId);
        Assert.Empty(first);

        // The per-instance cache returns the very same instance on the second read.
        var second = await resolver.ResolveAsync(tenantId, userId);
        Assert.Same(first, second);
    }

    [Fact]
    public async Task Resolve_Async_Cache_Is_Keyed_By_Tenant_And_User_And_Is_Request_Scoped()
    {
        using var factory = CreateFactory();
        var tenantId = Guid.NewGuid();
        var userId = SeedUser(factory, tenantId, "sub-cache", TenantUserStatus.Active);
        SeedMembership(factory, tenantId, userId, ProjectRole.ProjectViewer);

        string[] viewerOnly =
        [
            Permissions.ProjectView,
            Permissions.ReviewView,
            Permissions.ExportDownload,
        ];

        var resolver = CreateResolver(factory);
        var first = await resolver.ResolveAsync(tenantId, userId);
        AssertPermissionsEqual(viewerOnly, first);

        // A distinct (tenant, user) pair is a distinct cache entry: no user in
        // the other tenant means no permissions, not the cached viewer set.
        Assert.Empty(await resolver.ResolveAsync(Guid.NewGuid(), userId));

        // Membership changes apply on the next request, i.e. not while cached.
        SeedMembership(factory, tenantId, userId, ProjectRole.ProjectOwner);
        Assert.Same(first, await resolver.ResolveAsync(tenantId, userId));

        // A fresh resolver (next request) observes the new membership.
        var nextRequest = CreateResolver(factory);
        AssertPermissionsEqual(
            PermissionResolver.Resolve(TenantUserStatus.Active, [ProjectRole.ProjectOwner]),
            await nextRequest.ResolveAsync(tenantId, userId));
    }

    [Fact]
    public async Task Resolve_Async_Is_Tenant_Scoped()
    {
        using var factory = CreateFactory();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var userInA = SeedUser(factory, tenantA, "sub-a", TenantUserStatus.Active);
        var userInB = SeedUser(factory, tenantB, "sub-b", TenantUserStatus.Active);
        SeedMembership(factory, tenantA, userInA, ProjectRole.ProjectOwner);
        SeedMembership(factory, tenantB, userInB, ProjectRole.ProjectViewer);

        var resolver = CreateResolver(factory);

        // The same user id resolved under the other tenant is unknown, and each
        // user's memberships never leak across the tenant boundary.
        AssertPermissionsEqual(
            PermissionResolver.Resolve(TenantUserStatus.Active, [ProjectRole.ProjectOwner]),
            await resolver.ResolveAsync(tenantA, userInA));

        AssertPermissionsEqual(
            PermissionResolver.Resolve(TenantUserStatus.Active, [ProjectRole.ProjectViewer]),
            await resolver.ResolveAsync(tenantB, userInB));

        Assert.Empty(await resolver.ResolveAsync(tenantB, userInA));
        Assert.Empty(await resolver.ResolveAsync(tenantA, userInB));
    }

    [Fact]
    public async Task Resolve_Async_Disabled_User_Resolves_Empty()
    {
        using var factory = CreateFactory();
        var tenantId = Guid.NewGuid();
        var userId = SeedUser(factory, tenantId, "sub-disabled", TenantUserStatus.Disabled);
        SeedMembership(factory, tenantId, userId, ProjectRole.ProjectOwner);

        var resolver = CreateResolver(factory);

        Assert.Empty(await resolver.ResolveAsync(tenantId, userId));
    }

    [Fact]
    public void Resolve_Async_Rejects_Null_Dependencies()
    {
        using var factory = CreateFactory();
        Assert.Throws<ArgumentNullException>(
            () => new PermissionResolver(null!, NullLogger<PermissionResolver>.Instance));
        Assert.Throws<ArgumentNullException>(() => new PermissionResolver(factory, null!));
    }

    [Theory]
    [InlineData(AuthPolicies.RequireService)]
    [InlineData(AuthPolicies.RequireTenantAdmin)]
    [InlineData(AuthPolicies.RequireProjectOwner)]
    [InlineData(AuthPolicies.RequireProjectEditor)]
    [InlineData(AuthPolicies.RequireReviewer)]
    [InlineData(AuthPolicies.RequireProjectViewer)]
    public void Auth_Policies_AllowedRoles_Is_The_Frozen_Hierarchy(string policy)
    {
        var allowed = AuthPolicies.AllowedRoles(policy);

        Assert.Contains(Roles.Service, allowed);
        Assert.Equal(allowed.Count, allowed.Distinct(StringComparer.Ordinal).Count());

        string[] expected = policy switch
        {
            AuthPolicies.RequireService => [Roles.Service],
            AuthPolicies.RequireTenantAdmin => [Roles.TenantAdmin, Roles.Service],
            AuthPolicies.RequireProjectOwner => [Roles.TenantAdmin, Roles.ProjectOwner, Roles.Service],
            AuthPolicies.RequireProjectEditor =>
                [Roles.TenantAdmin, Roles.ProjectOwner, Roles.ProjectEditor, Roles.Service],
            AuthPolicies.RequireReviewer =>
                [Roles.TenantAdmin, Roles.ProjectOwner, Roles.ProjectEditor, Roles.Reviewer, Roles.Service],
            _ =>
            [
                Roles.TenantAdmin, Roles.ProjectOwner, Roles.ProjectEditor,
                Roles.Reviewer, Roles.ProjectViewer, Roles.Service,
            ],
        };

        Assert.Equal(expected, allowed.ToArray());
    }

    [Fact]
    public void Auth_Policies_Unknown_Policy_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(() => AuthPolicies.AllowedRoles("RequireWizard"));
        Assert.Equal("policy", ex.ParamName);
        Assert.Throws<ArgumentException>(() => AuthPolicies.AllowedRoles(string.Empty));
    }

    [Fact]
    public void Auth_Policies_All_Matches_The_Declared_Policy_Names()
    {
        string[] expected =
        [
            "RequireProjectEditor",
            "RequireProjectOwner",
            "RequireProjectViewer",
            "RequireReviewer",
            "RequireService",
            "RequireTenantAdmin",
        ];

        Assert.Equal(expected, AuthPolicies.All.OrderBy(p => p, StringComparer.Ordinal).ToArray());

        foreach (var policy in AuthPolicies.All)
        {
            Assert.Contains(policy, AuthPolicies.All);
        }
    }

    [Fact]
    public void Roles_Contract_Is_Frozen_At_Six_Names()
    {
        string[] expected =
        [
            "ProjectEditor", "ProjectOwner", "ProjectViewer", "Reviewer", "Service", "TenantAdmin",
        ];

        Assert.Equal(expected, Roles.All.OrderBy(r => r, StringComparer.Ordinal).ToArray());
        Assert.Equal(6, Roles.All.Length);
        Assert.Equal(6, Roles.All.Distinct(StringComparer.Ordinal).Count());

        foreach (var role in Roles.All)
        {
            Assert.True(Roles.IsKnown(role));
        }
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("tenantadmin", false)]
    [InlineData("ProjectOwner ", false)]
    [InlineData("Wizard", false)]
    [InlineData("TenantAdmin", true)]
    [InlineData("ProjectOwner", true)]
    [InlineData("ProjectEditor", true)]
    [InlineData("Reviewer", true)]
    [InlineData("ProjectViewer", true)]
    [InlineData("Service", true)]
    public void Roles_IsKnown_Is_Ordinal_And_Whitespace_Sensitive(string? role, bool expected)
    {
        Assert.Equal(expected, Roles.IsKnown(role));
    }

    [Fact]
    public void Permissions_Contract_Is_Frozen_At_Twelve_Unique_Names()
    {
        string[] expected =
        [
            "admin.manage",
            "diagnostics.view",
            "export.create",
            "export.download",
            "processing.cancel",
            "processing.retry",
            "processing.start",
            "project.delete",
            "project.edit",
            "project.view",
            "review.resolve",
            "review.view",
        ];

        Assert.Equal(expected, Permissions.All.OrderBy(p => p, StringComparer.Ordinal).ToArray());
        Assert.Equal(12, Permissions.All.Length);
        Assert.Equal(12, Permissions.All.Distinct(StringComparer.Ordinal).Count());

        foreach (var permission in Permissions.All)
        {
            Assert.True(Permissions.IsKnown(permission));
        }
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData(" ", false)]
    [InlineData("Project.View", false)]
    [InlineData("project.view ", false)]
    [InlineData("project.unknown", false)]
    [InlineData("project.view", true)]
    [InlineData("admin.manage", true)]
    [InlineData("diagnostics.view", true)]
    public void Permissions_IsKnown_Is_Ordinal_And_Null_Safe(string? permission, bool expected)
    {
        Assert.Equal(expected, Permissions.IsKnown(permission));
    }

    [Fact]
    public void Role_Matrix_Unknown_Endpoint_Is_Denied_For_Everyone_Including_Service()
    {
        // The Service bypass only applies to *declared* entries: an undeclared
        // endpoint is unknown to the matrix, so nothing satisfies it.
        Assert.False(RoleMatrix.IsAllowed("GET /api/v1/unknown", [Roles.TenantAdmin]));
        Assert.False(RoleMatrix.IsAllowed("GET /api/v1/unknown", [Roles.ProjectOwner]));
        Assert.False(RoleMatrix.IsAllowed("GET /api/v1/unknown", NoRoles));
        Assert.False(RoleMatrix.IsAllowed("GET /api/v1/unknown", [Roles.Service]));
        Assert.Empty(RoleMatrix.AllowedFor("GET /api/v1/unknown"));
    }

    [Fact]
    public void Role_Matrix_Service_Satisfies_Every_Declared_Entry()
    {
        Assert.NotEmpty(RoleMatrix.Endpoints);

        foreach (var entry in RoleMatrix.Endpoints)
        {
            Assert.True(RoleMatrix.IsAllowed(entry.Key, [Roles.Service]));
            Assert.False(RoleMatrix.IsAllowed(entry.Key, ["Wizard"]));
            Assert.False(RoleMatrix.IsAllowed(entry.Key, NoRoles));
            Assert.Equal(entry.Value, RoleMatrix.AllowedFor(entry.Key).ToArray());
        }
    }

    [Fact]
    public void Role_Matrix_Hierarchical_Gates_For_Project_Endpoints()
    {
        // Create: Viewer and Reviewer are read-only.
        Assert.False(RoleMatrix.IsAllowed("POST /api/v1/projects", [Roles.ProjectViewer]));
        Assert.False(RoleMatrix.IsAllowed("POST /api/v1/projects", [Roles.Reviewer]));
        Assert.True(RoleMatrix.IsAllowed("POST /api/v1/projects", [Roles.ProjectEditor]));
        Assert.True(RoleMatrix.IsAllowed("POST /api/v1/projects", [Roles.ProjectOwner]));
        Assert.True(RoleMatrix.IsAllowed("POST /api/v1/projects", [Roles.TenantAdmin]));

        // Read: everyone below the owner.
        Assert.True(RoleMatrix.IsAllowed("GET /api/v1/projects", [Roles.ProjectViewer]));
        Assert.True(RoleMatrix.IsAllowed("GET /api/v1/projects", [Roles.Reviewer]));

        // Delete and cancel: owner and above only.
        Assert.True(RoleMatrix.IsAllowed("DELETE /api/v1/projects", [Roles.ProjectOwner]));
        Assert.False(RoleMatrix.IsAllowed("DELETE /api/v1/projects", [Roles.ProjectEditor]));
        Assert.True(RoleMatrix.IsAllowed("POST /api/v1/processing/cancel", [Roles.ProjectOwner]));
        Assert.False(RoleMatrix.IsAllowed("POST /api/v1/processing/cancel", [Roles.ProjectEditor]));

        // Retry: an editor may retry, a reviewer may not.
        Assert.True(RoleMatrix.IsAllowed("POST /api/v1/processing/retry", [Roles.ProjectEditor]));
        Assert.False(RoleMatrix.IsAllowed("POST /api/v1/processing/retry", [Roles.Reviewer]));

        // Admin diagnostics: Service or TenantAdmin only.
        Assert.True(RoleMatrix.IsAllowed("GET /api/v1/admin/diagnostics/leases", [Roles.Service]));
        Assert.True(RoleMatrix.IsAllowed("GET /api/v1/admin/diagnostics/leases", [Roles.TenantAdmin]));
        Assert.False(RoleMatrix.IsAllowed("GET /api/v1/admin/diagnostics/leases", [Roles.ProjectOwner]));
        Assert.Equal(
            [Roles.Service, Roles.TenantAdmin],
            RoleMatrix.AllowedFor("GET /api/v1/admin/diagnostics/leases").ToArray());
    }

    [Fact]
    public void Role_Matrix_Ignores_Blank_Role_Entries_And_Needs_Non_Blank_Inputs()
    {
        Assert.True(RoleMatrix.IsAllowed("POST /api/v1/projects", [string.Empty, "  ", Roles.ProjectEditor]));
        Assert.False(RoleMatrix.IsAllowed("POST /api/v1/projects", [string.Empty, "   "]));

        Assert.Throws<ArgumentException>(() => RoleMatrix.IsAllowed("   ", [Roles.TenantAdmin]));
        Assert.Throws<ArgumentException>(() => RoleMatrix.AllowedFor(string.Empty));
        Assert.Throws<ArgumentNullException>(() => RoleMatrix.IsAllowed("GET /api/v1/projects", null!));
    }

    [Fact]
    public void Claim_Types_Constants_Are_Stable()
    {
        Assert.Equal("tid", PlatformClaimTypes.TenantId);
        Assert.Equal("sub", PlatformClaimTypes.Subject);
        Assert.Equal("roles", PlatformClaimTypes.Roles);
        Assert.Equal("role", PlatformClaimTypes.Role);
    }

    [Fact]
    public void GetTenantId_Reads_The_Tid_Claim_And_Trims_It()
    {
        var tenantId = Guid.NewGuid();

        Assert.Equal(tenantId, Principal(tenantId.ToString("D")).GetTenantId());
        Assert.Equal(tenantId, Principal(tenantId.ToString("N")).GetTenantId());
        Assert.Equal(tenantId, Principal($"  {tenantId:D}  ").GetTenantId());
        Assert.Equal(tenantId, Principal($"{{{tenantId:D}}}").GetTenantId());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("tenant_id")]
    [InlineData("tid2")]
    [InlineData("Tid")]
    public void GetTenantId_Throws_Forbidden_For_Missing_Or_Invalid_Claims(string? raw)
    {
        var principal = raw is null ? EmptyPrincipal() : Principal(raw);

        var ex = Assert.Throws<ForbiddenException>(() => principal.GetTenantId());
        Assert.Equal(ErrorCodes.Forbidden, ex.ErrorCode);
        Assert.Equal(403, ex.StatusCode);
    }

    [Fact]
    public void GetTenantId_Rejects_Null_Principal()
    {
        Assert.Throws<ArgumentNullException>(() => ((ClaimsPrincipal)null!).GetTenantId());
    }

    [Fact]
    public void TryGetTenantId_Never_Throws_And_Signals_Through_The_Out_Parameter()
    {
        var tenantId = Guid.NewGuid();

        Assert.True(Principal(tenantId.ToString("D")).TryGetTenantId(out var parsed));
        Assert.Equal(tenantId, parsed);

        foreach (var raw in new[] { null, "", "   ", "bogus", Guid.Empty.ToString("D") })
        {
            var principal = raw is null ? EmptyPrincipal() : Principal(raw);
            Assert.False(principal.TryGetTenantId(out var failed));
            Assert.Equal(Guid.Empty, failed);
        }

        Assert.False(((ClaimsPrincipal)null!).TryGetTenantId(out var fromNull));
        Assert.Equal(Guid.Empty, fromNull);
    }

    [Fact]
    public void GetSubject_Returns_Trimmed_Sub_Or_Unknown()
    {
        Assert.Equal("user-42", Subject("user-42").GetSubject());
        Assert.Equal("user-42", Subject("  user-42  ").GetSubject());
        Assert.Equal("unknown", EmptyPrincipal().GetSubject());
        Assert.Equal("unknown", Subject("   ").GetSubject());

        // Wrong claim type: a 'name' claim is not a subject.
        var wrongType = new ClaimsPrincipal(new ClaimsIdentity([new Claim("name", "user-42")]));
        Assert.Equal("unknown", wrongType.GetSubject());

        // The first 'sub' claim wins when the type repeats.
        var repeated = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(PlatformClaimTypes.Subject, "first"),
            new Claim(PlatformClaimTypes.Subject, "second"),
        ]));
        Assert.Equal("first", repeated.GetSubject());

        Assert.Throws<ArgumentNullException>(() => ((ClaimsPrincipal)null!).GetSubject());
    }

    [Fact]
    public void GetRoles_Reads_Roles_Role_And_ClaimTypes_Role_And_Skips_Blanks()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(PlatformClaimTypes.Roles, "Reviewer"),
            new Claim(PlatformClaimTypes.Roles, "  ProjectViewer  "),
            new Claim(PlatformClaimTypes.Role, "ProjectEditor"),
            new Claim(SystemClaimTypes.Role, "Service"),
            new Claim(PlatformClaimTypes.Roles, "   "),
            new Claim("scope", "ignored"),
        ]));

        string[] expected = ["Reviewer", "ProjectViewer", "ProjectEditor", "Service"];
        Assert.Equal(expected, principal.GetRoles().ToArray());
        Assert.Empty(EmptyPrincipal().GetRoles());
        Assert.Throws<ArgumentNullException>(() => ((ClaimsPrincipal)null!).GetRoles());
    }

    [Fact]
    public void GetRoles_Treats_The_App_Role_Type_And_The_System_Role_Type_As_Distinct()
    {
        var appRoleOnly = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(PlatformClaimTypes.Role, "Reviewer")]));
        Assert.Equal(["Reviewer"], appRoleOnly.GetRoles().ToArray());

        var systemRoleOnly = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(SystemClaimTypes.Role, "http://schemas.example/role")]));
        Assert.Equal(["http://schemas.example/role"], systemRoleOnly.GetRoles().ToArray());

        // System ClaimTypes.Role (a URI) is not the app-level 'role' string.
        Assert.NotEqual(SystemClaimTypes.Role, PlatformClaimTypes.Role);
    }

    [Fact]
    public void HasAnyRole_Matches_Any_Allowed_Role_Case_Sensitively()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(PlatformClaimTypes.Roles, "Reviewer"),
            new Claim(PlatformClaimTypes.Roles, "ProjectViewer"),
        ]));

        Assert.True(principal.HasAnyRole([Roles.Reviewer]));
        Assert.True(principal.HasAnyRole([Roles.ProjectOwner, Roles.ProjectViewer]));
        Assert.False(principal.HasAnyRole([Roles.ProjectOwner]));
        Assert.False(principal.HasAnyRole(NoRoles));

        Assert.Throws<ArgumentNullException>(() => ((ClaimsPrincipal)null!).HasAnyRole([Roles.Reviewer]));
        Assert.Throws<ArgumentNullException>(() => principal.HasAnyRole(null!));
    }

    [Fact]
    public void Policy_Role_Lists_And_Claim_Role_Values_Use_The_Same_Role_Names()
    {
        foreach (var role in Roles.All)
        {
            var principal = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(PlatformClaimTypes.Roles, role)]));

            foreach (var policy in AuthPolicies.All)
            {
                var expected = AuthPolicies.AllowedRoles(policy).Contains(role, StringComparer.Ordinal);
                Assert.Equal(expected, principal.HasAnyRole(AuthPolicies.AllowedRoles(policy)));
            }

            // The viewer policy is the widest human gate; every declared role,
            // Service included, satisfies it.
            Assert.True(principal.HasAnyRole(AuthPolicies.AllowedRoles(AuthPolicies.RequireProjectViewer)));
        }
    }

    private static void AssertPermissionsEqual(IEnumerable<string> expected, IEnumerable<string> actual)
    {
        Assert.Equal(
            expected.OrderBy(p => p, StringComparer.Ordinal).ToArray(),
            actual.OrderBy(p => p, StringComparer.Ordinal).ToArray());
    }

    private static ClaimsPrincipal EmptyPrincipal() => new(new ClaimsIdentity());

    private static ClaimsPrincipal Principal(string tenantClaimValue)
    {
        return new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(PlatformClaimTypes.TenantId, tenantClaimValue)],
            "test-auth"));
    }

    private static ClaimsPrincipal Subject(string subject)
    {
        return new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(PlatformClaimTypes.Subject, subject)],
            "test-auth"));
    }

    private static PermissionResolver CreateResolver(InMemoryContextFactory factory)
    {
        return new PermissionResolver(factory, NullLogger<PermissionResolver>.Instance);
    }

    private static InMemoryContextFactory CreateFactory()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase("PermissionMatrixTests-" + Guid.NewGuid().ToString("N"))
            .ReplaceService<IModelCacheKeyFactory, TenantModelCacheKeyFactory>()
            .Options;
        return new InMemoryContextFactory(options);
    }

    private static Guid SeedUser(
        InMemoryContextFactory factory,
        Guid tenantId,
        Guid userId,
        string subject,
        TenantUserStatus status)
    {
        var now = DateTimeOffset.UtcNow;
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = new AppDbContext(factory.Options);
            db.Set<TenantUser>().Add(new TenantUser(
                userId, tenantId, subject, $"{subject}@example.test", subject, status, now, now));
            db.SaveChanges();
        }

        return userId;
    }

    private static Guid SeedUser(
        InMemoryContextFactory factory,
        Guid tenantId,
        string subject,
        TenantUserStatus status)
    {
        return SeedUser(factory, tenantId, Guid.NewGuid(), subject, status);
    }

    private static void SeedMembership(InMemoryContextFactory factory, Guid tenantId, Guid userId, ProjectRole role)
    {
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = new AppDbContext(factory.Options);
            db.Set<ProjectMembership>().Add(new ProjectMembership(
                Guid.NewGuid(), tenantId, Guid.NewGuid(), userId, role, null, DateTimeOffset.UtcNow));
            db.SaveChanges();
        }
    }

    private sealed class InMemoryContextFactory : IStageExecutionContextFactory, IDisposable
    {
        public InMemoryContextFactory(DbContextOptions<AppDbContext> options)
        {
            Options = options;
        }

        public DbContextOptions<AppDbContext> Options { get; }

        public DbContext CreateDbContext() => new AppDbContext(Options);

        public void Dispose()
        {
        }
    }
}
