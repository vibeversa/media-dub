using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using DubbingPlatform.Api.Errors;
using DubbingPlatform.Api.Middleware;
using DubbingPlatform.Application.Authorization;
using DubbingPlatform.Application.Errors;
using DubbingPlatform.Application.Exceptions;
using DubbingPlatform.Application.MultiTenancy;
using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Infrastructure.Persistence;
using DubbingPlatform.Infrastructure.Persistence.Interceptors;
using EFCore.NamingConventions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using Testcontainers.PostgreSql;

namespace DubbingPlatform.IntegrationTests.Admin;

/// <summary>
/// Task 036: admin authorization matrix.
/// Hermetic facts assert the frozen role matrix (every admin read denies
/// ProjectViewer and allows TenantAdmin/Service) and the 403 envelope.
/// Docker-gated facts replay the HTTP authz matrix end to end: anonymous
/// callers get 401 on every <c>/admin/...</c> endpoint, non-elevated callers
/// get 403 on every <c>/admin/...</c> endpoint, elevated callers succeed, and
/// destructive-shaped operations without a reason/audit body are rejected
/// (unknown destructive subpaths return 404 with the
/// <c>ADMIN_ROUTE_UNKNOWN</c> marker and never execute).
/// </summary>
public sealed class AdminAuthzTests
{
    private const string TestSigningKey = "test-signing-key-0123456789abcdef-test-signing-key-01";

    private const string TestAudience = "dubbing-api";

    private static readonly string[] AdminReads =
    [
        "/api/v1/admin/usage",
        "/api/v1/admin/quotas",
        "/api/v1/admin/provider-health",
        "/api/v1/admin/provider-routes",
        "/api/v1/admin/diagnostics/queues",
        "/api/v1/admin/diagnostics/dlq",
        "/api/v1/admin/diagnostics/leases",
        "/api/v1/admin/diagnostics/orphans",
        "/api/v1/admin/diagnostics/review-backlog",
        "/api/v1/admin/status",
        "/api/v1/admin/dlq/summary",
        "/api/v1/admin/leases/status",
        "/api/v1/admin/reviews/backlog",
    ];

    [Fact]
    public void Admin_Matrix_Denies_Viewer_Allows_Elevated()
    {
        foreach (var route in new[]
        {
            "GET /api/v1/admin/usage",
            "GET /api/v1/admin/quotas",
            "GET /api/v1/admin/provider-health",
            "GET /api/v1/admin/provider-routes",
            "GET /api/v1/admin/diagnostics/queues",
            "GET /api/v1/admin/diagnostics/dlq",
            "GET /api/v1/admin/diagnostics/leases",
            "GET /api/v1/admin/diagnostics/orphans",
            "GET /api/v1/admin/diagnostics/review-backlog",
            // Task 044: the operator-only local-GPU health read. It belongs in
            // this loop and NOT in `AdminReads`, and the difference matters.
            //
            // `AdminReads` feeds three tests that assert a LIVE status: 401
            // anonymous, 403 `ProjectViewer`, 200 `TenantAdmin`.
            // `/admin/local-gpu` has no `AdminController` route yet - it falls
            // to the catch-all and answers 404 `ADMIN_ROUTE_UNKNOWN` for every
            // caller - so adding it there would fail the 200 assertion with a
            // message about a missing route rather than about authorization.
            // `Admin_Destructive_Without_Reason_Rejected` is the existing
            // precedent for an un-provisioned admin path: it asserts the marker
            // instead of a status the route does not have.
            //
            // What this loop DOES pin is the authorization contract itself:
            // device inventory is `Service`/`TenantAdmin` only. When the route
            // is implemented, widening these roles fails here before it ships.
            "GET /api/v1/admin/local-gpu",
        })
        {
            Assert.True(RoleMatrix.IsAllowed(route, ["TenantAdmin"]));
            Assert.True(RoleMatrix.IsAllowed(route, ["Service"]));
            Assert.False(RoleMatrix.IsAllowed(route, ["ProjectViewer"]));
            Assert.False(RoleMatrix.IsAllowed(route, ["ProjectEditor"]));
            Assert.False(RoleMatrix.IsAllowed(route, ["Reviewer"]));
            // `ProjectOwner` is a membership role, not an elevated one, and it
            // must not be a back door into device inventory. Named explicitly
            // because the list above would otherwise never exercise it.
            Assert.False(RoleMatrix.IsAllowed(route, ["ProjectOwner"]));
        }
    }

    /// <summary>
    /// Task 044: the local-GPU route grants exactly the elevated pair, so a
    /// third name cannot be added later by accident. The matrix is the
    /// documented record of who may read infrastructure inventory, and an
    /// inventory route is exactly where an extra entry does the most damage.
    /// </summary>
    [Fact]
    public void Admin_LocalGpu_Matrix_Grants_Exactly_The_Elevated_Pair()
    {
        const string Route = "GET /api/v1/admin/local-gpu";

        Assert.Equal([Roles.Service, Roles.TenantAdmin], RoleMatrix.AllowedFor(Route));
        Assert.DoesNotContain(Roles.ProjectOwner, RoleMatrix.AllowedFor(Route));
        Assert.DoesNotContain(Roles.ProjectEditor, RoleMatrix.AllowedFor(Route));
        Assert.DoesNotContain(Roles.Reviewer, RoleMatrix.AllowedFor(Route));
        Assert.DoesNotContain(Roles.ProjectViewer, RoleMatrix.AllowedFor(Route));
    }

    [Fact]
    public void Admin_Forbidden_Maps_To_403_Envelope()
    {
        var (status, code, message, _) = ApiError.Map(new ForbiddenException("denied"));
        Assert.Equal(403, status);
        Assert.Equal(ErrorCodes.Forbidden, code);
        Assert.False(string.IsNullOrWhiteSpace(message));
    }

    [SkippableFact]
    public async Task Admin_Anonymous_401_Every_Endpoint()
    {
        using var factory = CreateFactory(null);
        using var client = factory.CreateClient();
        foreach (var path in AdminReads)
        {
            using var response = await client.GetAsync(path).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    [SkippableFact]
    public async Task Admin_NonElevated_403_Every_Endpoint()
    {
        var container = await StartPostgresAsync().ConfigureAwait(true);
        await using (container.ConfigureAwait(true))
        {
            var connectionString = container.GetConnectionString();
            await MigrateAsync(connectionString).ConfigureAwait(true);
            var tenantId = Guid.NewGuid();
            await SeedTenantAsync(connectionString, tenantId).ConfigureAwait(true);

            using var factory = CreateFactory(connectionString);
            using var client = factory.CreateClient();
            UseToken(client, tenantId, Guid.NewGuid(), "ProjectViewer");
            foreach (var path in AdminReads)
            {
                using var response = await client.GetAsync(path).ConfigureAwait(true);
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            }
        }
    }

    [SkippableFact]
    public async Task Admin_Elevated_200_Reads()
    {
        var container = await StartPostgresAsync().ConfigureAwait(true);
        await using (container.ConfigureAwait(true))
        {
            var connectionString = container.GetConnectionString();
            await MigrateAsync(connectionString).ConfigureAwait(true);
            var tenantId = Guid.NewGuid();
            await SeedTenantAsync(connectionString, tenantId).ConfigureAwait(true);

            using var factory = CreateFactory(connectionString);
            using var client = factory.CreateClient();
            UseToken(client, tenantId, Guid.NewGuid(), "TenantAdmin");
            foreach (var path in AdminReads)
            {
                using var response = await client.GetAsync(path).ConfigureAwait(true);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            }
        }
    }

    [SkippableFact]
    public async Task Admin_Destructive_Without_Reason_Rejected()
    {
        var container = await StartPostgresAsync().ConfigureAwait(true);
        await using (container.ConfigureAwait(true))
        {
            var connectionString = container.GetConnectionString();
            await MigrateAsync(connectionString).ConfigureAwait(true);
            var tenantId = Guid.NewGuid();
            await SeedTenantAsync(connectionString, tenantId).ConfigureAwait(true);

            using var factory = CreateFactory(connectionString);
            using var client = factory.CreateClient();
            UseToken(client, tenantId, Guid.NewGuid(), "TenantAdmin");

            // Destructive-shaped operations carry no reason/audit body here.
            // The admin surface is read-only: unknown destructive subpaths
            // must return 404 with the unknown-route marker and never
            // execute (never 2xx, never a silent mutation).
            var attempts = new (string Method, string Path, object? Body)[]
            {
                ("POST", $"/api/v1/admin/users/{Guid.NewGuid():N}/roles", new { role = "ProjectOwner" }),
                ("POST", "/api/v1/admin/diagnostics/dlq/redrive", new { entryId = "dlq-1" }),
                ("POST", "/api/v1/admin/diagnostics/dlq/discard", new { entryId = "dlq-1" }),
                ("POST", "/api/v1/admin/feature-flags/apply", new { flags = new { } }),
                ("PUT", "/api/v1/admin/tenants/tenant-1", new { name = "Renamed" }),
                ("DELETE", $"/api/v1/admin/leases/{Guid.NewGuid():N}", null),
            };

            foreach (var (method, path, body) in attempts)
            {
                using var request = new HttpRequestMessage(new HttpMethod(method), path);
                if (body is not null)
                {
                    request.Content = JsonContent.Create(body);
                }

                using var response = await client.SendAsync(request).ConfigureAwait(true);
                Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
                var payload = await response.Content.ReadAsStringAsync().ConfigureAwait(true);
                Assert.Contains(ApiError.AdminRouteUnknownMarker, payload, StringComparison.Ordinal);
            }
        }
    }

    private static WebApplicationFactory<CorrelationIdMiddleware> CreateFactory(string? connectionString)
    {
        var factory = new WebApplicationFactory<CorrelationIdMiddleware>();
        return factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) =>
            {
                var values = new Dictionary<string, string?>
                {
                    ["Auth:SigningKey"] = TestSigningKey,
                    ["Auth:Audience"] = TestAudience,
                    ["Auth:RequireHttps"] = "false",
                    ["Transport:Provider"] = "InMemory",
                };
                if (connectionString is not null)
                {
                    values["ConnectionStrings:Default"] = connectionString;
                }

                config.AddInMemoryCollection(values);
            });
        });
    }

    private static void UseToken(HttpClient client, Guid tenantId, Guid userId, params string[] roles)
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", CreateToken(tenantId, userId, roles));
    }

    private static string CreateToken(Guid tenantId, Guid userId, params string[] roles)
    {
        var handler = new JwtSecurityTokenHandler();
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(TestSigningKey));
        var claims = new List<Claim>
        {
            new("tid", tenantId.ToString()),
            new("sub", userId.ToString("D")),
        };
        foreach (var role in roles)
        {
            claims.Add(new Claim("roles", role));
        }

        var token = new JwtSecurityToken(
            audience: TestAudience,
            claims: claims,
            expires: DateTime.UtcNow.AddHours(1),
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));
        return handler.WriteToken(token);
    }

    private static async Task<PostgreSqlContainer> StartPostgresAsync()
    {
        try
        {
            var container = new PostgreSqlBuilder("postgres:16-alpine").Build();
            await container.StartAsync().ConfigureAwait(true);
            return container;
        }
#pragma warning disable CA1031 // Docker availability probe: any failure means skip.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Skip.If(true, $"Docker/PostgreSQL unavailable: {ex.Message}");
            throw new InvalidOperationException("Unreachable: Skip.If always throws.", ex);
        }
    }

    private static Microsoft.EntityFrameworkCore.DbContextOptions<AppDbContext> CreateOptions(string connectionString)
    {
        return new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention()
            .AddInterceptors(new TenantSessionInterceptor())
            .Options;
    }

    private static async Task MigrateAsync(string connectionString)
    {
        using (TenantContext.BeginMaintenanceScope())
        {
            var options = CreateOptions(connectionString);
            using var context = new AppDbContext(options);
            await context.Database.MigrateAsync().ConfigureAwait(true);
        }
    }

    private static async Task SeedTenantAsync(string connectionString, Guid tenantId)
    {
        using (TenantContext.BeginScope(tenantId))
        {
            var options = CreateOptions(connectionString);
            using var context = new AppDbContext(options);
            if (!await context.Tenants.AnyAsync(t => t.Id == tenantId).ConfigureAwait(true))
            {
                context.Tenants.Add(new Tenant(tenantId, $"Tenant {tenantId:N}", $"tenant-{tenantId:N}", DateTimeOffset.UtcNow));
                await context.SaveChangesAsync().ConfigureAwait(true);
            }
        }
    }
}
