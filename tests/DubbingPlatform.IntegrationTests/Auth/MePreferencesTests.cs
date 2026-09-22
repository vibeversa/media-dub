using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using DubbingPlatform.Api.Middleware;
using DubbingPlatform.Application.Authorization;
using DubbingPlatform.Application.MultiTenancy;
using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Domain.Enums;
using DubbingPlatform.Infrastructure.Persistence;
using DubbingPlatform.Infrastructure.Persistence.Interceptors;
using EFCore.NamingConventions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using Testcontainers.PostgreSql;
using Xunit.Abstractions;

namespace DubbingPlatform.IntegrationTests.Auth;

/// <summary>
/// Task 006: login/refresh/logout, /me, and preferences over PG.
/// Skips when Docker is unavailable (CI runs live). Rate limiting is relaxed
/// (<c>AuthRateLimit:Enabled=false</c>) on every factory except the dedicated
/// burst test, which opts into the default 5/min/IP budget.
/// </summary>
public sealed class MePreferencesTests
{
    private const string TestSigningKey = "test-signing-key-0123456789abcdef-test-signing-key-01";

    private const string TestAudience = "dubbing-api";

    private readonly ITestOutputHelper _output;

    public MePreferencesTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void Permission_Set_Contract_Matches_Twelve_Names()
    {
        string[] expected =
        [
            "project.view",
            "project.edit",
            "project.delete",
            "processing.start",
            "processing.cancel",
            "processing.retry",
            "review.view",
            "review.resolve",
            "export.create",
            "export.download",
            "admin.manage",
            "diagnostics.view",
        ];

        Assert.Equal(expected.OrderBy(p => p, StringComparer.Ordinal), Permissions.All.OrderBy(p => p, StringComparer.Ordinal));
        Assert.Equal(12, Permissions.All.Length);
        Assert.All(expected, p => Assert.True(Permissions.IsKnown(p)));
        Assert.False(Permissions.IsKnown("project.admin"));

        Assert.Empty(PermissionResolver.Resolve(TenantUserStatus.Disabled, [ProjectRole.ProjectOwner]));

        var owner = PermissionResolver.Resolve(TenantUserStatus.Active, [ProjectRole.ProjectOwner]);
        Assert.Contains(Permissions.ProjectDelete, owner);
        Assert.Contains(Permissions.DiagnosticsView, owner);
        Assert.DoesNotContain(Permissions.AdminManage, owner);

        var editor = PermissionResolver.Resolve(TenantUserStatus.Active, [ProjectRole.ProjectEditor]);
        Assert.Contains(Permissions.ProjectEdit, editor);
        Assert.DoesNotContain(Permissions.ProjectDelete, editor);
        Assert.DoesNotContain(Permissions.DiagnosticsView, editor);

        var reviewer = PermissionResolver.Resolve(TenantUserStatus.Active, [ProjectRole.Reviewer]);
        Assert.Contains(Permissions.ReviewResolve, reviewer);
        Assert.DoesNotContain(Permissions.ProjectEdit, reviewer);

        var viewer = PermissionResolver.Resolve(TenantUserStatus.Active, [ProjectRole.ProjectViewer]);
        Assert.Contains(Permissions.ProjectView, viewer);
        Assert.Contains(Permissions.ExportDownload, viewer);
        Assert.DoesNotContain(Permissions.ReviewResolve, viewer);

        var admin = PermissionResolver.Resolve(TenantUserStatus.Active, [], [Roles.TenantAdmin]);
        Assert.Equal(
            Permissions.All.OrderBy(p => p, StringComparer.Ordinal),
            admin.OrderBy(p => p, StringComparer.Ordinal));
    }

    [SkippableFact]
    public async Task Login_Me_Preferences_RoundTrip()
    {
        var container = await StartPostgresAsync().ConfigureAwait(true);
        await using (container.ConfigureAwait(true))
        {
            var connectionString = container.GetConnectionString();
            await MigrateAsync(connectionString).ConfigureAwait(true);
            var tenantId = Guid.NewGuid();
            await SeedTenantAsync(connectionString, tenantId).ConfigureAwait(true);
            var userId = await SeedUserAsync(connectionString, tenantId, "sub-roundtrip", TenantUserStatus.Active).ConfigureAwait(true);
            var projectId = await SeedProjectAsync(connectionString, tenantId).ConfigureAwait(true);
            await SeedMembershipAsync(connectionString, tenantId, projectId, userId, ProjectRole.ProjectOwner).ConfigureAwait(true);

            using var factory = CreateFactory(connectionString);
            using var client = factory.CreateClient();

            var tokens = await LoginAsync(client, tenantId, "sub-roundtrip").ConfigureAwait(true);
            Assert.False(string.IsNullOrWhiteSpace(tokens.AccessToken));
            Assert.False(string.IsNullOrWhiteSpace(tokens.RefreshToken));
            Assert.Contains(".", tokens.RefreshToken, StringComparison.Ordinal);

            UseToken(client, tokens.AccessToken);
            using var meResponse = await client.GetAsync("/api/v1/me").ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.OK, meResponse.StatusCode);
            var me = JsonDocument.Parse(await meResponse.Content.ReadAsStringAsync().ConfigureAwait(true));
            Assert.Equal(userId.ToString("D"), me.RootElement.GetProperty("user").GetProperty("id").GetString());
            Assert.Equal(tenantId.ToString("D"), me.RootElement.GetProperty("tenant").GetProperty("id").GetString());
            Assert.Contains("ProjectOwner", me.RootElement.GetProperty("roles").EnumerateArray().Select(e => e.GetString()));
            var permissions = me.RootElement.GetProperty("permissions").EnumerateArray().Select(e => e.GetString()!).ToList();
            Assert.Contains("project.delete", permissions);
            Assert.DoesNotContain("admin.manage", permissions);
            Assert.All(permissions, p => Assert.True(Permissions.IsKnown(p)));
            Assert.False(string.IsNullOrWhiteSpace(me.RootElement.GetProperty("locale").GetString()));
            Assert.True(me.RootElement.TryGetProperty("featureFlags", out _));
            Assert.True(me.RootElement.TryGetProperty("session", out var session));
            Assert.True(session.TryGetProperty("expiresAt", out _));

            using var putResponse = await client.PutAsync(
                "/api/v1/me/preferences",
                JsonContent.Create(new { preferences = new { locale = "de-DE", theme = "dark" } })).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.OK, putResponse.StatusCode);

            using var getResponse = await client.GetAsync("/api/v1/me/preferences").ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
            var prefs = JsonDocument.Parse(await getResponse.Content.ReadAsStringAsync().ConfigureAwait(true));
            Assert.Equal("de-DE", prefs.RootElement.GetProperty("preferences").GetProperty("locale").GetString());
            Assert.Equal("dark", prefs.RootElement.GetProperty("preferences").GetProperty("theme").GetString());

            using var overwrite = await client.PutAsync(
                "/api/v1/me/preferences",
                JsonContent.Create(new { preferences = new { locale = "fr-FR" } })).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.OK, overwrite.StatusCode);
            using var reloaded = await client.GetAsync("/api/v1/me/preferences").ConfigureAwait(true);
            var reloadedDoc = JsonDocument.Parse(await reloaded.Content.ReadAsStringAsync().ConfigureAwait(true));
            Assert.Equal("fr-FR", reloadedDoc.RootElement.GetProperty("preferences").GetProperty("locale").GetString());

            var audit = await FindAuditAsync(connectionString, tenantId, "auth.login").ConfigureAwait(true);
            Assert.NotNull(audit);
            Assert.DoesNotContain(tokens.RefreshToken, audit, StringComparison.Ordinal);
        }
    }

    [SkippableFact]
    public async Task Unknown_Preference_Key_400()
    {
        var container = await StartPostgresAsync().ConfigureAwait(true);
        await using (container.ConfigureAwait(true))
        {
            var connectionString = container.GetConnectionString();
            await MigrateAsync(connectionString).ConfigureAwait(true);
            var tenantId = Guid.NewGuid();
            await SeedTenantAsync(connectionString, tenantId).ConfigureAwait(true);
            await SeedUserAsync(connectionString, tenantId, "sub-unknown-key", TenantUserStatus.Active).ConfigureAwait(true);

            using var factory = CreateFactory(connectionString);
            using var client = factory.CreateClient();
            var tokens = await LoginAsync(client, tenantId, "sub-unknown-key").ConfigureAwait(true);
            UseToken(client, tokens.AccessToken);

            using var response = await client.PutAsync(
                "/api/v1/me/preferences",
                JsonContent.Create(new { preferences = new { apiSecret = "hunter2" } })).ConfigureAwait(true);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(true));
            Assert.Equal("PREFERENCE_KEY_UNKNOWN", body.RootElement.GetProperty("error").GetProperty("code").GetString());
        }
    }

    [SkippableFact]
    public async Task Oversize_Preference_Value_413()
    {
        var container = await StartPostgresAsync().ConfigureAwait(true);
        await using (container.ConfigureAwait(true))
        {
            var connectionString = container.GetConnectionString();
            await MigrateAsync(connectionString).ConfigureAwait(true);
            var tenantId = Guid.NewGuid();
            await SeedTenantAsync(connectionString, tenantId).ConfigureAwait(true);
            await SeedUserAsync(connectionString, tenantId, "sub-oversize", TenantUserStatus.Active).ConfigureAwait(true);

            using var factory = CreateFactory(connectionString);
            using var client = factory.CreateClient();
            var tokens = await LoginAsync(client, tenantId, "sub-oversize").ConfigureAwait(true);
            UseToken(client, tokens.AccessToken);

            using var response = await client.PutAsync(
                "/api/v1/me/preferences",
                JsonContent.Create(new { preferences = new { locale = new string('x', 5000) } })).ConfigureAwait(true);

            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        }
    }

    [SkippableFact]
    public async Task Disabled_User_403_On_Me()
    {
        var container = await StartPostgresAsync().ConfigureAwait(true);
        await using (container.ConfigureAwait(true))
        {
            var connectionString = container.GetConnectionString();
            await MigrateAsync(connectionString).ConfigureAwait(true);
            var tenantId = Guid.NewGuid();
            await SeedTenantAsync(connectionString, tenantId).ConfigureAwait(true);
            var userId = await SeedUserAsync(connectionString, tenantId, "sub-disabled", TenantUserStatus.Active).ConfigureAwait(true);
            var projectId = await SeedProjectAsync(connectionString, tenantId).ConfigureAwait(true);
            await SeedMembershipAsync(connectionString, tenantId, projectId, userId, ProjectRole.ProjectOwner).ConfigureAwait(true);

            using var factory = CreateFactory(connectionString);
            using var client = factory.CreateClient();
            var tokens = await LoginAsync(client, tenantId, "sub-disabled").ConfigureAwait(true);

            await SetUserStatusAsync(connectionString, tenantId, userId, TenantUserStatus.Disabled).ConfigureAwait(true);

            UseToken(client, tokens.AccessToken);
            using var meResponse = await client.GetAsync("/api/v1/me").ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.Forbidden, meResponse.StatusCode);
            var body = JsonDocument.Parse(await meResponse.Content.ReadAsStringAsync().ConfigureAwait(true));
            Assert.Equal("USER_DISABLED", body.RootElement.GetProperty("error").GetProperty("code").GetString());

            using var prefsResponse = await client.GetAsync("/api/v1/me/preferences").ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.Forbidden, prefsResponse.StatusCode);
        }
    }

    [SkippableFact]
    public async Task Refresh_Rotation_Rejects_Reuse_And_Revokes_Family()
    {
        var container = await StartPostgresAsync().ConfigureAwait(true);
        await using (container.ConfigureAwait(true))
        {
            var connectionString = container.GetConnectionString();
            await MigrateAsync(connectionString).ConfigureAwait(true);
            var tenantId = Guid.NewGuid();
            await SeedTenantAsync(connectionString, tenantId).ConfigureAwait(true);
            await SeedUserAsync(connectionString, tenantId, "sub-refresh", TenantUserStatus.Active).ConfigureAwait(true);

            using var factory = CreateFactory(connectionString);
            using var client = factory.CreateClient();
            var tokens = await LoginAsync(client, tenantId, "sub-refresh").ConfigureAwait(true);

            using var first = await client.PostAsync(
                "/api/v1/auth/refresh",
                JsonContent.Create(new { refreshToken = tokens.RefreshToken })).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
            var rotated = JsonDocument.Parse(await first.Content.ReadAsStringAsync().ConfigureAwait(true));
            var secondToken = rotated.RootElement.GetProperty("refreshToken").GetString()!;
            Assert.NotEqual(tokens.RefreshToken, secondToken);

            using var reuse = await client.PostAsync(
                "/api/v1/auth/refresh",
                JsonContent.Create(new { refreshToken = tokens.RefreshToken })).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.Unauthorized, reuse.StatusCode);
            var reuseBody = JsonDocument.Parse(await reuse.Content.ReadAsStringAsync().ConfigureAwait(true));
            Assert.Equal("TOKEN_REUSED", reuseBody.RootElement.GetProperty("error").GetProperty("code").GetString());

            using var familyDead = await client.PostAsync(
                "/api/v1/auth/refresh",
                JsonContent.Create(new { refreshToken = secondToken })).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.Unauthorized, familyDead.StatusCode);
            var familyBody = JsonDocument.Parse(await familyDead.Content.ReadAsStringAsync().ConfigureAwait(true));
            var familyCode = familyBody.RootElement.GetProperty("error").GetProperty("code").GetString();
            Assert.True(
                string.Equals(familyCode, "TOKEN_REUSED", StringComparison.Ordinal)
                || string.Equals(familyCode, "TOKEN_EXPIRED", StringComparison.Ordinal),
                $"Unexpected code '{familyCode}'.");
        }
    }

    [SkippableFact]
    public async Task Refresh_Unknown_Token_Expired_And_Bad_Credentials_Invalid()
    {
        var container = await StartPostgresAsync().ConfigureAwait(true);
        await using (container.ConfigureAwait(true))
        {
            var connectionString = container.GetConnectionString();
            await MigrateAsync(connectionString).ConfigureAwait(true);
            var tenantId = Guid.NewGuid();
            await SeedTenantAsync(connectionString, tenantId).ConfigureAwait(true);
            await SeedUserAsync(connectionString, tenantId, "sub-real", TenantUserStatus.Active).ConfigureAwait(true);

            using var factory = CreateFactory(connectionString);
            using var client = factory.CreateClient();

            using var unknown = await client.PostAsync(
                "/api/v1/auth/refresh",
                JsonContent.Create(new { refreshToken = string.Concat(Guid.NewGuid().ToString("N"), ".bogus-secret") })).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
            var unknownBody = JsonDocument.Parse(await unknown.Content.ReadAsStringAsync().ConfigureAwait(true));
            Assert.Equal("TOKEN_EXPIRED", unknownBody.RootElement.GetProperty("error").GetProperty("code").GetString());

            using var bad = await client.PostAsync(
                "/api/v1/auth/login",
                JsonContent.Create(new { tenantId, externalSubject = "sub-nobody" })).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.Unauthorized, bad.StatusCode);
            var badBody = JsonDocument.Parse(await bad.Content.ReadAsStringAsync().ConfigureAwait(true));
            Assert.Equal("INVALID_CREDENTIALS", badBody.RootElement.GetProperty("error").GetProperty("code").GetString());
        }
    }

    [SkippableFact]
    public async Task Logout_Is_Idempotent()
    {
        var container = await StartPostgresAsync().ConfigureAwait(true);
        await using (container.ConfigureAwait(true))
        {
            var connectionString = container.GetConnectionString();
            await MigrateAsync(connectionString).ConfigureAwait(true);
            var tenantId = Guid.NewGuid();
            await SeedTenantAsync(connectionString, tenantId).ConfigureAwait(true);
            await SeedUserAsync(connectionString, tenantId, "sub-logout", TenantUserStatus.Active).ConfigureAwait(true);

            using var factory = CreateFactory(connectionString);
            using var client = factory.CreateClient();
            var tokens = await LoginAsync(client, tenantId, "sub-logout").ConfigureAwait(true);

            using var first = await client.PostAsync(
                "/api/v1/auth/logout",
                JsonContent.Create(new { refreshToken = tokens.RefreshToken })).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);

            using var second = await client.PostAsync(
                "/api/v1/auth/logout",
                JsonContent.Create(new { refreshToken = tokens.RefreshToken })).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.OK, second.StatusCode);

            using var unknown = await client.PostAsync(
                "/api/v1/auth/logout",
                JsonContent.Create(new { refreshToken = string.Concat(Guid.NewGuid().ToString("N"), ".bogus") })).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.OK, unknown.StatusCode);

            var audit = await FindAuditAsync(connectionString, tenantId, "auth.logout").ConfigureAwait(true);
            Assert.NotNull(audit);
        }
    }

    [SkippableFact]
    public async Task Cross_Tenant_Isolation()
    {
        var container = await StartPostgresAsync().ConfigureAwait(true);
        await using (container.ConfigureAwait(true))
        {
            var connectionString = container.GetConnectionString();
            await MigrateAsync(connectionString).ConfigureAwait(true);
            var tenantA = Guid.NewGuid();
            var tenantB = Guid.NewGuid();
            await SeedTenantAsync(connectionString, tenantA).ConfigureAwait(true);
            await SeedTenantAsync(connectionString, tenantB).ConfigureAwait(true);
            await SeedUserAsync(connectionString, tenantA, "sub-shared", TenantUserStatus.Active).ConfigureAwait(true);
            await SeedUserAsync(connectionString, tenantB, "sub-shared", TenantUserStatus.Active).ConfigureAwait(true);

            using var factory = CreateFactory(connectionString);
            using var client = factory.CreateClient();

            var tokensA = await LoginAsync(client, tenantA, "sub-shared").ConfigureAwait(true);
            UseToken(client, tokensA.AccessToken);
            using var meA = await client.GetAsync("/api/v1/me").ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.OK, meA.StatusCode);
            var meDoc = JsonDocument.Parse(await meA.Content.ReadAsStringAsync().ConfigureAwait(true));
            Assert.Equal(tenantA.ToString("D"), meDoc.RootElement.GetProperty("tenant").GetProperty("id").GetString());

            using var putA = await client.PutAsync(
                "/api/v1/me/preferences",
                JsonContent.Create(new { preferences = new { theme = "dark" } })).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.OK, putA.StatusCode);

            using var clientB = factory.CreateClient();
            var tokensB = await LoginAsync(clientB, tenantB, "sub-shared").ConfigureAwait(true);
            UseToken(clientB, tokensB.AccessToken);
            using var prefsB = await clientB.GetAsync("/api/v1/me/preferences").ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.OK, prefsB.StatusCode);
            var prefsDoc = JsonDocument.Parse(await prefsB.Content.ReadAsStringAsync().ConfigureAwait(true));
            Assert.False(prefsDoc.RootElement.GetProperty("preferences").TryGetProperty("theme", out _));

            using var cross = await client.PostAsync(
                "/api/v1/auth/login",
                JsonContent.Create(new { tenantId = tenantA, externalSubject = "sub-missing" })).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.Unauthorized, cross.StatusCode);
        }
    }

    [SkippableFact]
    public async Task Missing_Tenant_Claim_401_Tenant_Required()
    {
        var container = await StartPostgresAsync().ConfigureAwait(true);
        await using (container.ConfigureAwait(true))
        {
            var connectionString = container.GetConnectionString();
            await MigrateAsync(connectionString).ConfigureAwait(true);

            using var factory = CreateFactory(connectionString);
            using var client = factory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", CreateTokenWithoutTenant());

            using var response = await client.GetAsync("/api/v1/me").ConfigureAwait(true);

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(true));
            Assert.Equal("TENANT_REQUIRED", body.RootElement.GetProperty("error").GetProperty("code").GetString());
        }
    }

    [SkippableFact]
    public async Task Hint_Present_Policy_Denies_403()
    {
        var container = await StartPostgresAsync().ConfigureAwait(true);
        await using (container.ConfigureAwait(true))
        {
            var connectionString = container.GetConnectionString();
            await MigrateAsync(connectionString).ConfigureAwait(true);
            var tenantId = Guid.NewGuid();
            await SeedTenantAsync(connectionString, tenantId).ConfigureAwait(true);
            var userId = await SeedUserAsync(connectionString, tenantId, "sub-viewer", TenantUserStatus.Active).ConfigureAwait(true);
            var projectId = await SeedProjectAsync(connectionString, tenantId).ConfigureAwait(true);
            await SeedMembershipAsync(connectionString, tenantId, projectId, userId, ProjectRole.ProjectViewer).ConfigureAwait(true);

            using var factory = CreateFactory(connectionString);
            using var client = factory.CreateClient();
            var tokens = await LoginAsync(client, tenantId, "sub-viewer").ConfigureAwait(true);
            UseToken(client, tokens.AccessToken);

            using var meResponse = await client.GetAsync("/api/v1/me").ConfigureAwait(true);
            var me = JsonDocument.Parse(await meResponse.Content.ReadAsStringAsync().ConfigureAwait(true));
            var permissions = me.RootElement.GetProperty("permissions").EnumerateArray().Select(e => e.GetString()!).ToList();
            Assert.Contains("project.view", permissions);

            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/projects")
            {
                Content = JsonContent.Create(new { sourceLanguage = "en", targetLanguage = "es" }),
            };
            request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
            using var create = await client.SendAsync(request).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
        }
    }

    [SkippableFact]
    public async Task Login_Burst_Rate_Limited_429()
    {
        var container = await StartPostgresAsync().ConfigureAwait(true);
        await using (container.ConfigureAwait(true))
        {
            var connectionString = container.GetConnectionString();
            await MigrateAsync(connectionString).ConfigureAwait(true);
            var tenantId = Guid.NewGuid();
            await SeedTenantAsync(connectionString, tenantId).ConfigureAwait(true);
            await SeedUserAsync(connectionString, tenantId, "sub-burst", TenantUserStatus.Active).ConfigureAwait(true);

            using var factory = CreateFactory(connectionString, rateLimitEnabled: true);
            using var client = factory.CreateClient();

            HttpStatusCode? last = null;
            for (var attempt = 0; attempt < 8; attempt++)
            {
                using var response = await client.PostAsync(
                    "/api/v1/auth/login",
                    JsonContent.Create(new { tenantId, externalSubject = "sub-burst" })).ConfigureAwait(true);
                last = response.StatusCode;
                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    break;
                }
            }

            Assert.Equal(HttpStatusCode.TooManyRequests, last);
        }
    }

    private static WebApplicationFactory<CorrelationIdMiddleware> CreateFactory(string connectionString, bool rateLimitEnabled = false)
    {
        var factory = new WebApplicationFactory<CorrelationIdMiddleware>();
        return factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) =>
            {
                var values = new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Default"] = connectionString,
                    ["Auth:SigningKey"] = TestSigningKey,
                    ["Auth:Audience"] = TestAudience,
                    ["Auth:RequireHttps"] = "false",
                    ["AuthRateLimit:Enabled"] = rateLimitEnabled ? "true" : "false",
                    ["Transport:Provider"] = "InMemory",
                };
                config.AddInMemoryCollection(values);
            });
        });
    }

    private static void UseToken(HttpClient client, string accessToken)
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
    }

    private static string CreateTokenWithoutTenant()
    {
        var handler = new JwtSecurityTokenHandler();
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(TestSigningKey));
        var token = new JwtSecurityToken(
            audience: TestAudience,
            claims: [new Claim("sub", Guid.NewGuid().ToString("D"))],
            expires: DateTime.UtcNow.AddHours(1),
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));
        return handler.WriteToken(token);
    }

    private static async Task<(string AccessToken, string RefreshToken)> LoginAsync(HttpClient client, Guid tenantId, string subject)
    {
        using var response = await client.PostAsync(
            "/api/v1/auth/login",
            JsonContent.Create(new { tenantId, externalSubject = subject })).ConfigureAwait(true);
        var payload = await response.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.True(response.IsSuccessStatusCode, $"Login failed: {(int)response.StatusCode} {payload}");
        using var document = JsonDocument.Parse(payload);
        return (
            document.RootElement.GetProperty("accessToken").GetString()!,
            document.RootElement.GetProperty("refreshToken").GetString()!);
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

    private static DbContextOptions<AppDbContext> CreateOptions(string connectionString)
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
            using var context = new AppDbContext(CreateOptions(connectionString));
            await context.Database.MigrateAsync().ConfigureAwait(true);
        }
    }

    private static async Task SeedTenantAsync(string connectionString, Guid tenantId)
    {
        using (TenantContext.BeginScope(tenantId))
        {
            using var context = new AppDbContext(CreateOptions(connectionString));
            if (!await context.Tenants.AnyAsync(t => t.Id == tenantId).ConfigureAwait(true))
            {
                context.Tenants.Add(new Tenant(tenantId, $"Tenant {tenantId:N}", $"tenant-{tenantId:N}", DateTimeOffset.UtcNow));
                await context.SaveChangesAsync().ConfigureAwait(true);
            }
        }
    }

    private static async Task<Guid> SeedUserAsync(string connectionString, Guid tenantId, string subject, TenantUserStatus status)
    {
        var userId = Guid.NewGuid();
        using (TenantContext.BeginScope(tenantId))
        {
            using var context = new AppDbContext(CreateOptions(connectionString));
            context.Set<TenantUser>().Add(new TenantUser(
                userId, tenantId, subject, $"{subject}@example.com", subject,
                status, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
            await context.SaveChangesAsync().ConfigureAwait(true);
        }

        return userId;
    }

    private static async Task SetUserStatusAsync(string connectionString, Guid tenantId, Guid userId, TenantUserStatus status)
    {
        using (TenantContext.BeginScope(tenantId))
        {
            using var context = new AppDbContext(CreateOptions(connectionString));
            var user = await context.Set<TenantUser>().SingleAsync(u => u.Id == userId).ConfigureAwait(true);
            if (status == TenantUserStatus.Disabled)
            {
                user.Disable(DateTimeOffset.UtcNow);
            }
            else
            {
                user.Enable(DateTimeOffset.UtcNow);
            }

            await context.SaveChangesAsync().ConfigureAwait(true);
        }
    }

    private static async Task<Guid> SeedProjectAsync(string connectionString, Guid tenantId)
    {
        var projectId = Guid.NewGuid();
        using (TenantContext.BeginScope(tenantId))
        {
            using var context = new AppDbContext(CreateOptions(connectionString));
            context.DubbingProjects.Add(new DubbingProject(
                projectId, tenantId, "en", "de", ProjectStatus.Created,
                "{}", new string('a', 64), null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
            await context.SaveChangesAsync().ConfigureAwait(true);
        }

        return projectId;
    }

    private static async Task SeedMembershipAsync(string connectionString, Guid tenantId, Guid projectId, Guid userId, ProjectRole role)
    {
        using (TenantContext.BeginScope(tenantId))
        {
            using var context = new AppDbContext(CreateOptions(connectionString));
            context.Set<ProjectMembership>().Add(new ProjectMembership(
                Guid.NewGuid(), tenantId, projectId, userId, role, null, DateTimeOffset.UtcNow));
            await context.SaveChangesAsync().ConfigureAwait(true);
        }
    }

    private static async Task<string?> FindAuditAsync(string connectionString, Guid tenantId, string action)
    {
        using (TenantContext.BeginScope(tenantId))
        {
            using var context = new AppDbContext(CreateOptions(connectionString));
            var match = await context.Set<AuditEvent>()
                .AsNoTracking()
                .Where(e => e.Action == action)
                .OrderByDescending(e => e.CreatedAt)
                .FirstOrDefaultAsync().ConfigureAwait(true);
            return match?.DetailsJson;
        }
    }
}
