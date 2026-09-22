using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using DubbingPlatform.Api.Middleware;
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

namespace DubbingPlatform.IntegrationTests.Projects;

/// <summary>
/// Task 007: dashboard summary plus project CRUD with archive semantics and
/// settings guards over PG. Skips when Docker is unavailable (CI runs live).
/// Cross-tenant single reads assert 403 FORBIDDEN (not 404): this preserves the
/// established <c>ProjectOwnershipHandler</c> + <c>ProjectsUploadsApiTests</c>
/// contract and keeps <c>prj_</c>/raw-GUID handling consistent; see report.
/// </summary>
public sealed class ProjectsApiTests
{
    private const string TestSigningKey = "test-signing-key-0123456789abcdef-test-signing-key-01";

    private const string TestAudience = "dubbing-api";

    private const string ValidProcessingSettings = "{\"schemaVersion\":1}";

    private const string AltProcessingSettings = "{\"schemaVersion\":1,\"outputProfile\":\"hq\"}";

    [SkippableFact]
    public async Task Dashboard_Shape_Returns_Seven_Sections()
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
            UseToken(client, tenantId, "TenantAdmin");

            var projectId = await CreateProjectAsync(client, new { name = "Dash", sourceLanguage = "en", targetLanguage = "es" }).ConfigureAwait(true);

            using var response = await client.GetAsync("/api/v1/dashboard/summary").ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(true));

            Assert.True(body.RootElement.TryGetProperty("projectCounts", out var counts));
            Assert.True(counts.TryGetProperty("active", out _));
            Assert.True(counts.TryGetProperty("archived", out _));
            Assert.True(counts.TryGetProperty("total", out _));
            Assert.Equal(1, counts.GetProperty("active").GetInt32());
            Assert.Equal(1, counts.GetProperty("total").GetInt32());

            Assert.True(body.RootElement.TryGetProperty("recentOutputs", out var outputs));
            Assert.True(outputs.GetArrayLength() <= 5);

            Assert.True(body.RootElement.TryGetProperty("storage", out var storage));
            Assert.True(storage.TryGetProperty("usedBytes", out _));
            Assert.True(storage.TryGetProperty("quotaBytes", out _));

            Assert.True(body.RootElement.TryGetProperty("cost", out var cost));
            Assert.True(cost.TryGetProperty("monthToDate", out _));
            Assert.True(cost.TryGetProperty("currency", out _));

            Assert.True(body.RootElement.TryGetProperty("quota", out var quota));
            Assert.True(quota.TryGetProperty("remaining", out _));
            Assert.True(quota.TryGetProperty("resetsAt", out _));

            Assert.True(body.RootElement.TryGetProperty("warnings", out var warnings));
            foreach (var warning in warnings.EnumerateArray())
            {
                Assert.True(warning.TryGetProperty("code", out _));
                Assert.True(warning.TryGetProperty("message", out _));
            }

            Assert.True(body.RootElement.TryGetProperty("backlog", out var backlog));
            Assert.True(backlog.TryGetProperty("pendingReviews", out _));
            Assert.True(backlog.TryGetProperty("runningJobs", out _));

            Assert.NotEmpty(projectId);
        }
    }

    [SkippableFact]
    public async Task List_Filters_Pagination_Sort_And_Clamped()
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
            UseToken(client, tenantId, "TenantAdmin");

            var alpha = await CreateProjectAsync(client, new { name = "Alpha", sourceLanguage = "en", targetLanguage = "es" }).ConfigureAwait(true);
            await CreateProjectAsync(client, new { name = "Beta", sourceLanguage = "en", targetLanguage = "fr" }).ConfigureAwait(true);
            await CreateProjectAsync(client, new { name = "Gamma", sourceLanguage = "en", targetLanguage = "de" }).ConfigureAwait(true);

            // Archive Alpha; default list must exclude it.
            using (var archive = await client.PostAsync($"/api/v1/projects/{alpha}/archive", new StringContent(string.Empty)).ConfigureAwait(true))
            {
                Assert.Equal(HttpStatusCode.OK, archive.StatusCode);
            }

            using var def = await client.GetAsync("/api/v1/projects?page=1&pageSize=20").ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.OK, def.StatusCode);
            var defDoc = JsonDocument.Parse(await def.Content.ReadAsStringAsync().ConfigureAwait(true));
            Assert.Equal(2, defDoc.RootElement.GetProperty("total").GetInt64());
            Assert.True(defDoc.RootElement.TryGetProperty("sort", out _));
            Assert.True(defDoc.RootElement.TryGetProperty("sortDir", out _));
            Assert.False(defDoc.RootElement.GetProperty("clamped").GetBoolean());

            using var onlyArchived = await client.GetAsync("/api/v1/projects?archived=true").ConfigureAwait(true);
            var archivedDoc = JsonDocument.Parse(await onlyArchived.Content.ReadAsStringAsync().ConfigureAwait(true));
            Assert.Equal(1, archivedDoc.RootElement.GetProperty("total").GetInt64());

            using var all = await client.GetAsync("/api/v1/projects?archived=all").ConfigureAwait(true);
            var allDoc = JsonDocument.Parse(await all.Content.ReadAsStringAsync().ConfigureAwait(true));
            Assert.Equal(3, allDoc.RootElement.GetProperty("total").GetInt64());

            using var search = await client.GetAsync("/api/v1/projects?archived=all&search=beta").ConfigureAwait(true);
            var searchDoc = JsonDocument.Parse(await search.Content.ReadAsStringAsync().ConfigureAwait(true));
            Assert.Equal(1, searchDoc.RootElement.GetProperty("total").GetInt64());

            using var sorted = await client.GetAsync("/api/v1/projects?archived=all&sort=name&sortDir=asc").ConfigureAwait(true);
            var sortedDoc = JsonDocument.Parse(await sorted.Content.ReadAsStringAsync().ConfigureAwait(true));
            var names = sortedDoc.RootElement.GetProperty("items").EnumerateArray()
                .Select(e => e.GetProperty("name").GetString()).ToList();
            Assert.Equal(new[] { "Alpha", "Beta", "Gamma" }, names);

            using var paged = await client.GetAsync("/api/v1/projects?archived=all&sort=name&sortDir=asc&page=2&pageSize=2").ConfigureAwait(true);
            var pagedDoc = JsonDocument.Parse(await paged.Content.ReadAsStringAsync().ConfigureAwait(true));
            Assert.Equal(3, pagedDoc.RootElement.GetProperty("total").GetInt64());
            Assert.Single(pagedDoc.RootElement.GetProperty("items").EnumerateArray());

            using var clamped = await client.GetAsync("/api/v1/projects?archived=all&pageSize=500").ConfigureAwait(true);
            var clampedDoc = JsonDocument.Parse(await clamped.Content.ReadAsStringAsync().ConfigureAwait(true));
            Assert.Equal(HttpStatusCode.OK, clamped.StatusCode);
            Assert.Equal(100, clampedDoc.RootElement.GetProperty("pageSize").GetInt32());
            Assert.True(clampedDoc.RootElement.GetProperty("clamped").GetBoolean());
        }
    }

    [SkippableFact]
    public async Task Create_And_Get_RoundTrip_With_Audit()
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
            UseToken(client, tenantId, "TenantAdmin");

            var id = await CreateProjectAsync(client, new { name = "Trailer", sourceLanguage = "en", targetLanguage = "es", description = "Launch" }).ConfigureAwait(true);
            Assert.StartsWith("prj_", id, StringComparison.Ordinal);

            using var get = await client.GetAsync($"/api/v1/projects/{id}").ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.OK, get.StatusCode);
            Assert.True(get.Headers.ETag is not null);
            var doc = JsonDocument.Parse(await get.Content.ReadAsStringAsync().ConfigureAwait(true));
            Assert.Equal("Trailer", doc.RootElement.GetProperty("name").GetString());
            Assert.Equal("es", doc.RootElement.GetProperty("targetLanguage").GetString());
            Assert.False(doc.RootElement.GetProperty("isArchived").GetBoolean());
            Assert.True(doc.RootElement.TryGetProperty("settingsVersion", out _));
            Assert.True(doc.RootElement.TryGetProperty("configurationHash", out _));

            var audit = await FindAuditAsync(connectionString, tenantId, "project.create").ConfigureAwait(true);
            Assert.NotNull(audit);

            // Duplicate names are allowed.
            var second = await CreateProjectAsync(client, new { name = "Trailer", sourceLanguage = "en", targetLanguage = "fr" }).ConfigureAwait(true);
            Assert.NotEqual(id, second);

            // Empty and overlong names are rejected.
            using var empty = await SendPostAsync(client, "/api/v1/projects", new { name = "", sourceLanguage = "en", targetLanguage = "es" }).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);

            using var overlong = await SendPostAsync(client, "/api/v1/projects", new { name = new string('n', 201), sourceLanguage = "en", targetLanguage = "es" }).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.BadRequest, overlong.StatusCode);
        }
    }

    [SkippableFact]
    public async Task Patch_Language_Immutable_400()
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
            UseToken(client, tenantId, "TenantAdmin");

            var id = await CreateProjectAsync(client, new { name = "Lang", sourceLanguage = "en", targetLanguage = "es" }).ConfigureAwait(true);

            using var patch = await SendPatchAsync(client, $"/api/v1/projects/{id}", new { targetLanguage = "fr", name = "Changed" }).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.BadRequest, patch.StatusCode);
            var body = JsonDocument.Parse(await patch.Content.ReadAsStringAsync().ConfigureAwait(true));
            Assert.Equal("LANGUAGE_IMMUTABLE", body.RootElement.GetProperty("error").GetProperty("code").GetString());

            // Target language unchanged on the server (no partial apply).
            using var get = await client.GetAsync($"/api/v1/projects/{id}").ConfigureAwait(true);
            var doc = JsonDocument.Parse(await get.Content.ReadAsStringAsync().ConfigureAwait(true));
            Assert.Equal("es", doc.RootElement.GetProperty("targetLanguage").GetString());
            Assert.Equal("Lang", doc.RootElement.GetProperty("name").GetString());
        }
    }

    [SkippableFact]
    public async Task Patch_Unknown_Field_400_No_Partial_Apply()
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
            UseToken(client, tenantId, "TenantAdmin");

            var id = await CreateProjectAsync(client, new { name = "Before", sourceLanguage = "en", targetLanguage = "es" }).ConfigureAwait(true);

            using var patch = await SendPatchAsync(client, $"/api/v1/projects/{id}", new { name = "After", bogusField = 1 }).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.BadRequest, patch.StatusCode);

            using var get = await client.GetAsync($"/api/v1/projects/{id}").ConfigureAwait(true);
            var doc = JsonDocument.Parse(await get.Content.ReadAsStringAsync().ConfigureAwait(true));
            Assert.Equal("Before", doc.RootElement.GetProperty("name").GetString());
        }
    }

    [SkippableFact]
    public async Task Settings_Guard_409_With_Active_Run()
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
            UseToken(client, tenantId, "TenantAdmin");

            var id = await CreateProjectAsync(client, new { name = "Guarded", sourceLanguage = "en", targetLanguage = "es" }).ConfigureAwait(true);
            var projectGuid = ParsePublicId(id);
            await SeedRunAsync(connectionString, tenantId, projectGuid, ProcessingRunStatus.Running).ConfigureAwait(true);

            using var patch = await SendPatchRawAsync(
                client, $"/api/v1/projects/{id}",
                string.Concat("{\"processingSettings\":", ValidProcessingSettings, "}")).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.Conflict, patch.StatusCode);
            var body = JsonDocument.Parse(await patch.Content.ReadAsStringAsync().ConfigureAwait(true));
            Assert.Equal("SETTINGS_LOCKED_ACTIVE_RUN", body.RootElement.GetProperty("error").GetProperty("code").GetString());
        }
    }

    [SkippableFact]
    public async Task Settings_Success_Bumps_Version_Changes_Hash_And_Audits()
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
            UseToken(client, tenantId, "TenantAdmin");

            var id = await CreateProjectAsync(client, new { name = "Hashy", sourceLanguage = "en", targetLanguage = "es" }).ConfigureAwait(true);

            using var before = await client.GetAsync($"/api/v1/projects/{id}").ConfigureAwait(true);
            var beforeDoc = JsonDocument.Parse(await before.Content.ReadAsStringAsync().ConfigureAwait(true));
            var oldHash = beforeDoc.RootElement.GetProperty("configurationHash").GetString()!;
            var oldVersion = beforeDoc.RootElement.GetProperty("settingsVersion").GetInt32();

            using var patch = await SendPatchRawAsync(
                client, $"/api/v1/projects/{id}",
                string.Concat("{\"processingSettings\":", AltProcessingSettings, "}")).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.OK, patch.StatusCode);

            using var after = await client.GetAsync($"/api/v1/projects/{id}").ConfigureAwait(true);
            var afterDoc = JsonDocument.Parse(await after.Content.ReadAsStringAsync().ConfigureAwait(true));
            var newHash = afterDoc.RootElement.GetProperty("configurationHash").GetString()!;
            var newVersion = afterDoc.RootElement.GetProperty("settingsVersion").GetInt32();

            Assert.Equal(oldVersion + 1, newVersion);
            Assert.NotEqual(oldHash, newHash);

            var audit = await FindAuditAsync(connectionString, tenantId, "project.patch").ConfigureAwait(true);
            Assert.NotNull(audit);
            Assert.Contains(oldHash, audit, StringComparison.Ordinal);
            Assert.Contains(newHash, audit, StringComparison.Ordinal);
        }
    }

    [SkippableFact]
    public async Task Archive_Is_Idempotent()
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
            UseToken(client, tenantId, "TenantAdmin");

            var id = await CreateProjectAsync(client, new { name = "Arch", sourceLanguage = "en", targetLanguage = "es" }).ConfigureAwait(true);

            using var first = await client.PostAsync($"/api/v1/projects/{id}/archive", new StringContent(string.Empty)).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);

            using var second = await client.PostAsync($"/api/v1/projects/{id}/archive", new StringContent(string.Empty)).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.OK, second.StatusCode);

            using var get = await client.GetAsync($"/api/v1/projects/{id}").ConfigureAwait(true);
            var doc = JsonDocument.Parse(await get.Content.ReadAsStringAsync().ConfigureAwait(true));
            Assert.True(doc.RootElement.GetProperty("isArchived").GetBoolean());

            using var unarchive = await client.PostAsync($"/api/v1/projects/{id}/unarchive", new StringContent(string.Empty)).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.OK, unarchive.StatusCode);

            using var active = await client.GetAsync($"/api/v1/projects/{id}").ConfigureAwait(true);
            var activeDoc = JsonDocument.Parse(await active.Content.ReadAsStringAsync().ConfigureAwait(true));
            Assert.False(activeDoc.RootElement.GetProperty("isArchived").GetBoolean());
        }
    }

    [SkippableFact]
    public async Task Delete_With_Active_Run_409()
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
            UseToken(client, tenantId, "TenantAdmin");

            var id = await CreateProjectAsync(client, new { name = "Doomed", sourceLanguage = "en", targetLanguage = "es" }).ConfigureAwait(true);
            await SeedRunAsync(connectionString, tenantId, ParsePublicId(id), ProcessingRunStatus.Running).ConfigureAwait(true);

            using var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/projects/{id}");
            request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
            using var response = await client.SendAsync(request).ConfigureAwait(true);

            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(true));
            Assert.Equal("PROJECT_HAS_ACTIVE_RUN", body.RootElement.GetProperty("error").GetProperty("code").GetString());
        }
    }

    [SkippableFact]
    public async Task Cross_Tenant_Read_Is_Isolated_403()
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

            using var factory = CreateFactory(connectionString);
            using var clientA = factory.CreateClient();
            UseToken(clientA, tenantA, "TenantAdmin");
            var id = await CreateProjectAsync(clientA, new { name = "Secret", sourceLanguage = "en", targetLanguage = "es" }).ConfigureAwait(true);

            using var clientB = factory.CreateClient();
            UseToken(clientB, tenantB, "TenantAdmin");
            using var response = await clientB.GetAsync($"/api/v1/projects/{id}").ConfigureAwait(true);

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(true));
            Assert.Equal("FORBIDDEN", body.RootElement.GetProperty("error").GetProperty("code").GetString());
        }
    }

    [SkippableFact]
    public async Task ETag_Conflict_409_On_Stale_Version()
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
            UseToken(client, tenantId, "TenantAdmin");

            var id = await CreateProjectAsync(client, new { name = "Versioned", sourceLanguage = "en", targetLanguage = "es" }).ConfigureAwait(true);

            using var get = await client.GetAsync($"/api/v1/projects/{id}").ConfigureAwait(true);
            var doc = JsonDocument.Parse(await get.Content.ReadAsStringAsync().ConfigureAwait(true));
            var version = doc.RootElement.GetProperty("settingsVersion").GetInt32();

            // Stale If-Match.
            using var stale = await SendPatchWithMatchAsync(
                client, $"/api/v1/projects/{id}",
                string.Concat("{\"processingSettings\":", ValidProcessingSettings, "}"),
                version + 99).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
            var staleBody = JsonDocument.Parse(await stale.Content.ReadAsStringAsync().ConfigureAwait(true));
            Assert.Equal("SETTINGS_VERSION_CONFLICT", staleBody.RootElement.GetProperty("error").GetProperty("code").GetString());

            // Stale body version.
            using var staleBody2 = await SendPatchRawAsync(
                client, $"/api/v1/projects/{id}",
                string.Concat("{\"processingSettings\":", ValidProcessingSettings, ",\"settingsVersion\":", (version + 99).ToString(System.Globalization.CultureInfo.InvariantCulture), "}")).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.Conflict, staleBody2.StatusCode);

            // Fresh version succeeds.
            using var fresh = await SendPatchWithMatchAsync(
                client, $"/api/v1/projects/{id}",
                string.Concat("{\"processingSettings\":", ValidProcessingSettings, "}"),
                version).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.OK, fresh.StatusCode);
        }
    }

    private static WebApplicationFactory<CorrelationIdMiddleware> CreateFactory(string connectionString)
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
                    ["Transport:Provider"] = "InMemory",
                };
                config.AddInMemoryCollection(values);
            });
        });
    }

    private static void UseToken(HttpClient client, Guid tenantId, params string[] roles)
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", CreateToken(tenantId, roles));
    }

    private static string CreateToken(Guid tenantId, params string[] roles)
    {
        var handler = new JwtSecurityTokenHandler();
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(TestSigningKey));
        var claims = new List<Claim>
        {
            new("tid", tenantId.ToString()),
            new("sub", "test-user"),
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

    private static async Task<string> CreateProjectAsync(HttpClient client, object body)
    {
        using var response = await SendPostAsync(client, "/api/v1/projects", body).ConfigureAwait(true);
        var payload = await response.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"Create failed: {(int)response.StatusCode} {payload}");
        using var document = JsonDocument.Parse(payload);
        return document.RootElement.GetProperty("id").GetString()!;
    }

    private static async Task<HttpResponseMessage> SendPostAsync(HttpClient client, string url, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        return await client.SendAsync(request).ConfigureAwait(true);
    }

    private static async Task<HttpResponseMessage> SendPatchAsync(HttpClient client, string url, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Patch, url)
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        return await client.SendAsync(request).ConfigureAwait(true);
    }

    private static async Task<HttpResponseMessage> SendPatchRawAsync(HttpClient client, string url, string json)
    {
        using var request = new HttpRequestMessage(HttpMethod.Patch, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        return await client.SendAsync(request).ConfigureAwait(true);
    }

    private static async Task<HttpResponseMessage> SendPatchWithMatchAsync(HttpClient client, string url, string json, int version)
    {
        using var request = new HttpRequestMessage(HttpMethod.Patch, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        request.Headers.TryAddWithoutValidation("If-Match", string.Concat("\"", version.ToString(System.Globalization.CultureInfo.InvariantCulture), "\""));
        return await client.SendAsync(request).ConfigureAwait(true);
    }

    private static Guid ParsePublicId(string publicId)
    {
        var hex = publicId.StartsWith("prj_", StringComparison.Ordinal)
            ? publicId.Substring("prj_".Length)
            : publicId;
        return Guid.ParseExact(hex, "N");
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

    private static async Task SeedRunAsync(string connectionString, Guid tenantId, Guid projectId, ProcessingRunStatus status)
    {
        using (TenantContext.BeginScope(tenantId))
        {
            using var context = new AppDbContext(CreateOptions(connectionString));
            var now = DateTimeOffset.UtcNow;
            context.Set<ProcessingRun>().Add(new ProcessingRun(
                Guid.NewGuid(), tenantId, projectId, 1, status,
                "1.0.0", new string('a', 64), new string('b', 64), new string('c', 64),
                now, now, now, null));
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
