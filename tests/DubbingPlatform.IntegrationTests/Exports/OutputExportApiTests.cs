using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using DubbingPlatform.Api.Middleware;
using DubbingPlatform.Application.Abstractions;
using DubbingPlatform.Application.MultiTenancy;
using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Domain.Enums;
using DubbingPlatform.Infrastructure.Persistence;
using DubbingPlatform.Infrastructure.Persistence.Interceptors;
using EFCore.NamingConventions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Testcontainers.PostgreSql;

namespace DubbingPlatform.IntegrationTests.Exports;

/// <summary>
/// Task 012A: output aggregate + export lifecycle over PG with fake storage.
/// Skips when Docker is unavailable (CI runs live).
/// Cross-tenant project access stays 403; cross-tenant export ids return 404.
/// </summary>
public sealed class OutputExportApiTests
{
    private const string TestSigningKey = "test-signing-key-0123456789abcdef-test-signing-key-01";

    private const string TestAudience = "dubbing-api";

    [SkippableFact]
    public async Task Output_Unavailable_NoRuns()
    {
        var container = await StartPostgresAsync().ConfigureAwait(true);
        await using (container.ConfigureAwait(true))
        {
            var connectionString = container.GetConnectionString();
            await MigrateAsync(connectionString).ConfigureAwait(true);
            var tenantId = Guid.NewGuid();
            await SeedTenantAsync(connectionString, tenantId).ConfigureAwait(true);

            var fake = new FakeStorage();
            using var factory = CreateFactory(connectionString, fake);
            using var client = factory.CreateClient();
            UseToken(client, tenantId, Guid.NewGuid(), "TenantAdmin");
            var projectId = await CreateProjectAsync(client).ConfigureAwait(true);

            using var response = await client.GetAsync($"/api/v1/projects/{projectId}/output").ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(true));
            var root = doc.RootElement;
            Assert.Equal("Unavailable", root.GetProperty("state").GetString());
            Assert.Equal("NO_RUNS_YET", root.GetProperty("reason").GetString());
            Assert.Equal(0, root.GetProperty("completeness").GetProperty("ready").GetInt32());
            Assert.Equal(0, root.GetProperty("completeness").GetProperty("total").GetInt32());
        }
    }

    [SkippableFact]
    public async Task Output_Partial_WithMissing()
    {
        var container = await StartPostgresAsync().ConfigureAwait(true);
        await using (container.ConfigureAwait(true))
        {
            var connectionString = container.GetConnectionString();
            await MigrateAsync(connectionString).ConfigureAwait(true);
            var tenantId = Guid.NewGuid();
            await SeedTenantAsync(connectionString, tenantId).ConfigureAwait(true);

            var fake = new FakeStorage();
            using var factory = CreateFactory(connectionString, fake);
            using var client = factory.CreateClient();
            UseToken(client, tenantId, Guid.NewGuid(), "TenantAdmin");
            var projectId = await CreateProjectAsync(client).ConfigureAwait(true);
            await SeedRunAsync(connectionString, tenantId, ParseProjectId(projectId), ProcessingRunStatus.Running, 1, 2, false).ConfigureAwait(true);

            using var response = await client.GetAsync($"/api/v1/projects/{projectId}/output").ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(true));
            var root = doc.RootElement;
            Assert.Equal("Generating", root.GetProperty("state").GetString());
            Assert.True(root.TryGetProperty("progressApproximate", out var progress));
            Assert.True(progress.ValueKind == JsonValueKind.Number);
            var completeness = root.GetProperty("completeness");
            Assert.Equal(1, completeness.GetProperty("ready").GetInt32());
            Assert.Equal(2, completeness.GetProperty("total").GetInt32());
            var payload = await response.Content.ReadAsStringAsync().ConfigureAwait(true);
            Assert.Contains("SEGMENT_PENDING", payload, StringComparison.Ordinal);
        }
    }

    [SkippableFact]
    public async Task Output_Ready_Full()
    {
        var container = await StartPostgresAsync().ConfigureAwait(true);
        await using (container.ConfigureAwait(true))
        {
            var connectionString = container.GetConnectionString();
            await MigrateAsync(connectionString).ConfigureAwait(true);
            var tenantId = Guid.NewGuid();
            await SeedTenantAsync(connectionString, tenantId).ConfigureAwait(true);

            var fake = new FakeStorage();
            using var factory = CreateFactory(connectionString, fake);
            using var client = factory.CreateClient();
            UseToken(client, tenantId, Guid.NewGuid(), "TenantAdmin");
            var projectId = await CreateProjectAsync(client).ConfigureAwait(true);
            var projectGuid = ParseProjectId(projectId);
            var runId = await SeedRunAsync(connectionString, tenantId, projectGuid, ProcessingRunStatus.Completed, 2, 2, true).ConfigureAwait(true);
            await SeedOutputArtifactsAsync(connectionString, tenantId, projectGuid, runId).ConfigureAwait(true);

            using var response = await client.GetAsync($"/api/v1/projects/{projectId}/output").ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(true));
            var root = doc.RootElement;
            Assert.Equal("Ready", root.GetProperty("state").GetString());
            Assert.Equal(2, root.GetProperty("completeness").GetProperty("ready").GetInt32());
            var videoUrl = root.GetProperty("items").GetProperty("video").GetProperty("downloadUrl").GetString();
            Assert.StartsWith("https://fake-storage.test/", videoUrl, StringComparison.Ordinal);
            Assert.True(fake.LastExpiry <= TimeSpan.FromMinutes(15));
        }
    }

    [SkippableFact]
    public async Task Output_Failed_ErrorCode()
    {
        var container = await StartPostgresAsync().ConfigureAwait(true);
        await using (container.ConfigureAwait(true))
        {
            var connectionString = container.GetConnectionString();
            await MigrateAsync(connectionString).ConfigureAwait(true);
            var tenantId = Guid.NewGuid();
            await SeedTenantAsync(connectionString, tenantId).ConfigureAwait(true);

            var fake = new FakeStorage();
            using var factory = CreateFactory(connectionString, fake);
            using var client = factory.CreateClient();
            UseToken(client, tenantId, Guid.NewGuid(), "TenantAdmin");
            var projectId = await CreateProjectAsync(client).ConfigureAwait(true);
            await SeedRunAsync(connectionString, tenantId, ParseProjectId(projectId), ProcessingRunStatus.Failed, 0, 1, false).ConfigureAwait(true);

            using var response = await client.GetAsync($"/api/v1/projects/{projectId}/output").ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(true));
            Assert.Equal("Failed", doc.RootElement.GetProperty("state").GetString());
            Assert.False(string.IsNullOrWhiteSpace(doc.RootElement.GetProperty("errorCode").GetString()));
        }
    }

    [SkippableFact]
    public async Task Output_NoInternalPaths()
    {
        var container = await StartPostgresAsync().ConfigureAwait(true);
        await using (container.ConfigureAwait(true))
        {
            var connectionString = container.GetConnectionString();
            await MigrateAsync(connectionString).ConfigureAwait(true);
            var tenantId = Guid.NewGuid();
            await SeedTenantAsync(connectionString, tenantId).ConfigureAwait(true);

            var fake = new FakeStorage();
            using var factory = CreateFactory(connectionString, fake);
            using var client = factory.CreateClient();
            UseToken(client, tenantId, Guid.NewGuid(), "TenantAdmin");
            var projectId = await CreateProjectAsync(client).ConfigureAwait(true);
            var projectGuid = ParseProjectId(projectId);
            var runId = await SeedRunAsync(connectionString, tenantId, projectGuid, ProcessingRunStatus.Completed, 2, 2, true).ConfigureAwait(true);
            await SeedOutputArtifactsAsync(connectionString, tenantId, projectGuid, runId).ConfigureAwait(true);

            using var response = await client.GetAsync($"/api/v1/projects/{projectId}/output").ConfigureAwait(true);
            var payload = await response.Content.ReadAsStringAsync().ConfigureAwait(true);
            Assert.DoesNotContain("storageKey", payload, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("ContentObject", payload, StringComparison.Ordinal);
            Assert.DoesNotContain("bucket", payload, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("/tmp/", payload, StringComparison.Ordinal);
            Assert.DoesNotContain("s3://", payload, StringComparison.OrdinalIgnoreCase);
        }
    }

    [SkippableFact]
    public async Task Export_Create_Idempotent_SingleRow()
    {
        var container = await StartPostgresAsync().ConfigureAwait(true);
        await using (container.ConfigureAwait(true))
        {
            var connectionString = container.GetConnectionString();
            await MigrateAsync(connectionString).ConfigureAwait(true);
            var tenantId = Guid.NewGuid();
            await SeedTenantAsync(connectionString, tenantId).ConfigureAwait(true);

            var fake = new FakeStorage();
            using var factory = CreateFactory(connectionString, fake);
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            UseToken(client, tenantId, Guid.NewGuid(), "TenantAdmin");
            var projectId = await CreateProjectAsync(client).ConfigureAwait(true);
            await SeedRunAsync(connectionString, tenantId, ParseProjectId(projectId), ProcessingRunStatus.Completed, 2, 2, true).ConfigureAwait(true);

            var key = NewKey();
            string firstId;
            using (var first = await SendExportAsync(client, projectId, "srt", null, true, key).ConfigureAwait(true))
            {
                Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
                var doc = JsonDocument.Parse(await first.Content.ReadAsStringAsync().ConfigureAwait(true));
                firstId = doc.RootElement.GetProperty("id").GetString()!;
            }

            using (var replay = await SendExportAsync(client, projectId, "srt", null, true, key).ConfigureAwait(true))
            {
                Assert.True(replay.StatusCode == HttpStatusCode.Accepted || replay.StatusCode == HttpStatusCode.OK);
                var doc = JsonDocument.Parse(await replay.Content.ReadAsStringAsync().ConfigureAwait(true));
                Assert.Equal(firstId, doc.RootElement.GetProperty("id").GetString());
            }

            Assert.Equal(1, await CountExportsAsync(connectionString, tenantId, ParseProjectId(projectId)).ConfigureAwait(true));
        }
    }

    [SkippableFact]
    public async Task Export_Partial_Guard_Requires_AllowPartial()
    {
        var container = await StartPostgresAsync().ConfigureAwait(true);
        await using (container.ConfigureAwait(true))
        {
            var connectionString = container.GetConnectionString();
            await MigrateAsync(connectionString).ConfigureAwait(true);
            var tenantId = Guid.NewGuid();
            await SeedTenantAsync(connectionString, tenantId).ConfigureAwait(true);

            var fake = new FakeStorage();
            using var factory = CreateFactory(connectionString, fake);
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            UseToken(client, tenantId, Guid.NewGuid(), "TenantAdmin");
            var projectId = await CreateProjectAsync(client).ConfigureAwait(true);
            // Failed run with 1/2 ready and no rendered output → OUTPUT_INCOMPLETE.
            await SeedRunAsync(connectionString, tenantId, ParseProjectId(projectId), ProcessingRunStatus.Failed, 1, 2, false).ConfigureAwait(true);

            using (var blocked = await SendExportAsync(client, projectId, "srt", null, false, NewKey()).ConfigureAwait(true))
            {
                Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
                var body = JsonDocument.Parse(await blocked.Content.ReadAsStringAsync().ConfigureAwait(true));
                var code = body.RootElement.GetProperty("error").GetProperty("code").GetString();
                Assert.True(
                    string.Equals(code, "OUTPUT_INCOMPLETE", StringComparison.Ordinal) || string.Equals(code, "EXPORT_INCOMPLETE", StringComparison.Ordinal),
                    $"Unexpected code {code}");
            }

            using (var allowed = await SendExportAsync(client, projectId, "srt", null, true, NewKey()).ConfigureAwait(true))
            {
                Assert.Equal(HttpStatusCode.Accepted, allowed.StatusCode);
            }
        }
    }

    [SkippableFact]
    public async Task Export_Download_302_TTL()
    {
        var container = await StartPostgresAsync().ConfigureAwait(true);
        await using (container.ConfigureAwait(true))
        {
            var connectionString = container.GetConnectionString();
            await MigrateAsync(connectionString).ConfigureAwait(true);
            var tenantId = Guid.NewGuid();
            await SeedTenantAsync(connectionString, tenantId).ConfigureAwait(true);

            var fake = new FakeStorage();
            using var factory = CreateFactory(connectionString, fake);
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            UseToken(client, tenantId, Guid.NewGuid(), "TenantAdmin");
            var projectId = await CreateProjectAsync(client).ConfigureAwait(true);
            var projectGuid = ParseProjectId(projectId);
            var runId = await SeedRunAsync(connectionString, tenantId, projectGuid, ProcessingRunStatus.Completed, 2, 2, true).ConfigureAwait(true);
            var exportId = await SeedCompletedExportAsync(connectionString, tenantId, projectGuid, runId).ConfigureAwait(true);

            using var response = await client.GetAsync($"/api/v1/projects/{projectId}/exports/{exportId}/download").ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.Found, response.StatusCode);
            var location = response.Headers.Location?.ToString();
            Assert.StartsWith("https://fake-storage.test/", location, StringComparison.Ordinal);
            Assert.True(fake.LastExpiry <= TimeSpan.FromMinutes(15));
            Assert.DoesNotContain("bucket", location, StringComparison.OrdinalIgnoreCase);
        }
    }

    [SkippableFact]
    public async Task Export_Download_NotReady_409()
    {
        var container = await StartPostgresAsync().ConfigureAwait(true);
        await using (container.ConfigureAwait(true))
        {
            var connectionString = container.GetConnectionString();
            await MigrateAsync(connectionString).ConfigureAwait(true);
            var tenantId = Guid.NewGuid();
            await SeedTenantAsync(connectionString, tenantId).ConfigureAwait(true);

            var fake = new FakeStorage();
            using var factory = CreateFactory(connectionString, fake);
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            UseToken(client, tenantId, Guid.NewGuid(), "TenantAdmin");
            var projectId = await CreateProjectAsync(client).ConfigureAwait(true);
            await SeedRunAsync(connectionString, tenantId, ParseProjectId(projectId), ProcessingRunStatus.Completed, 2, 2, true).ConfigureAwait(true);

            string pendingId;
            using (var created = await SendExportAsync(client, projectId, "srt", null, true, NewKey()).ConfigureAwait(true))
            {
                var doc = JsonDocument.Parse(await created.Content.ReadAsStringAsync().ConfigureAwait(true));
                pendingId = doc.RootElement.GetProperty("id").GetString()!;
            }

            using var response = await client.GetAsync($"/api/v1/projects/{projectId}/exports/{pendingId}/download").ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(true));
            Assert.Equal("EXPORT_NOT_READY", body.RootElement.GetProperty("error").GetProperty("code").GetString());
        }
    }

    [SkippableFact]
    public async Task Export_CrossTenant_Id_404()
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

            var fake = new FakeStorage();
            using var factory = CreateFactory(connectionString, fake);
            using var clientA = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            UseToken(clientA, tenantA, Guid.NewGuid(), "TenantAdmin");
            var projectA = await CreateProjectAsync(clientA).ConfigureAwait(true);
            var runA = await SeedRunAsync(connectionString, tenantA, ParseProjectId(projectA), ProcessingRunStatus.Completed, 1, 1, true).ConfigureAwait(true);
            var exportA = await SeedCompletedExportAsync(connectionString, tenantA, ParseProjectId(projectA), runA).ConfigureAwait(true);

            using var clientB = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            UseToken(clientB, tenantB, Guid.NewGuid(), "TenantAdmin");
            var projectB = await CreateProjectAsync(clientB).ConfigureAwait(true);

            using var response = await clientB.GetAsync($"/api/v1/projects/{projectB}/exports/{exportA}").ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(true));
            Assert.Equal("NOT_FOUND", body.RootElement.GetProperty("error").GetProperty("code").GetString());
        }
    }

    [SkippableFact]
    public async Task Output_GenerationState_Matches_State()
    {
        var container = await StartPostgresAsync().ConfigureAwait(true);
        await using (container.ConfigureAwait(true))
        {
            var connectionString = container.GetConnectionString();
            await MigrateAsync(connectionString).ConfigureAwait(true);
            var tenantId = Guid.NewGuid();
            await SeedTenantAsync(connectionString, tenantId).ConfigureAwait(true);

            var fake = new FakeStorage();
            using var factory = CreateFactory(connectionString, fake);
            using var client = factory.CreateClient();
            UseToken(client, tenantId, Guid.NewGuid(), "TenantAdmin");
            var projectId = await CreateProjectAsync(client).ConfigureAwait(true);
            await SeedRunAsync(connectionString, tenantId, ParseProjectId(projectId), ProcessingRunStatus.Running, 1, 2, false).ConfigureAwait(true);

            using var response = await client.GetAsync($"/api/v1/projects/{projectId}/output").ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(true));
            var root = doc.RootElement;
            Assert.Equal(root.GetProperty("state").GetString(), root.GetProperty("generationState").GetString());
            var items = root.GetProperty("items");
            foreach (var name in new[] { "video", "audio", "transcript", "translation", "timeline", "speakers" })
            {
                if (items.TryGetProperty(name, out var entry) && entry.ValueKind == JsonValueKind.Object)
                {
                    Assert.Equal(entry.GetProperty("state").GetString(), entry.GetProperty("generationState").GetString());
                }
            }

            var qc = items.GetProperty("qc");
            Assert.Equal(qc.GetProperty("state").GetString(), qc.GetProperty("generationState").GetString());
            var completeness = root.GetProperty("completeness");
            Assert.Equal(1, completeness.GetProperty("ready").GetInt32());
            Assert.Equal(2, completeness.GetProperty("total").GetInt32());
        }
    }

    private sealed class FakeStorage : IArtifactStorage
    {
        public TimeSpan LastExpiry { get; private set; } = TimeSpan.FromMinutes(15);

        public Task UploadAsync(Stream content, string storageKey, string contentType, CancellationToken ct)
        {
            return Task.CompletedTask;
        }

        public Task<Stream> DownloadAsync(string storageKey, CancellationToken ct)
        {
            return Task.FromResult<Stream>(new MemoryStream([1, 2, 3], writable: false));
        }

        public Task<bool> ExistsAsync(string storageKey, CancellationToken ct)
        {
            return Task.FromResult(true);
        }

        public Task DeleteAsync(string storageKey, CancellationToken ct)
        {
            return Task.CompletedTask;
        }

        public Task<string> GetPresignedDownloadUrlAsync(string storageKey, TimeSpan expiry, CancellationToken ct)
        {
            LastExpiry = expiry;
            return Task.FromResult($"https://fake-storage.test/{storageKey}?exp={(int)expiry.TotalSeconds}");
        }

        public Task<string> GetPresignedUploadUrlAsync(string storageKey, TimeSpan expiry, CancellationToken ct)
        {
            LastExpiry = expiry;
            return Task.FromResult($"https://fake-storage.test/{storageKey}?exp={(int)expiry.TotalSeconds}");
        }

        public Task<string?> GetStorageChecksumAsync(string storageKey, CancellationToken ct)
        {
            return Task.FromResult<string?>(null);
        }
    }

    private static string NewKey()
    {
        return Guid.NewGuid().ToString("N");
    }

    private static Guid ParseProjectId(string publicId)
    {
        var hex = publicId.StartsWith("prj_", StringComparison.Ordinal)
            ? publicId.Substring("prj_".Length)
            : publicId;
        return Guid.ParseExact(hex, "N");
    }

    private static WebApplicationFactory<CorrelationIdMiddleware> CreateFactory(string connectionString, FakeStorage fake)
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
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<IArtifactStorage>(fake);
            });
        });
    }

    private static void UseToken(HttpClient client, Guid tenantId, Guid userId, params string[] roles)
    {
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", CreateToken(tenantId, userId, roles));
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

    private static async Task<string> CreateProjectAsync(HttpClient client)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/projects")
        {
            Content = JsonContent.Create(new { name = $"O-{Guid.NewGuid():N}", sourceLanguage = "en", targetLanguage = "es" }),
        };
        request.Headers.Add("Idempotency-Key", NewKey());
        using var response = await client.SendAsync(request).ConfigureAwait(true);
        var payload = await response.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"Create failed: {(int)response.StatusCode} {payload}");
        using var document = JsonDocument.Parse(payload);
        return document.RootElement.GetProperty("id").GetString()!;
    }

    private static async Task<HttpResponseMessage> SendExportAsync(
        HttpClient client, string projectId, string format, string? profile, bool allowPartial, string key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/projects/{projectId}/exports")
        {
            Content = JsonContent.Create(new { format, profile, allowPartial }),
        };
        request.Headers.Add("Idempotency-Key", key);
        return await client.SendAsync(request).ConfigureAwait(true);
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

    private static async Task<Guid> SeedRunAsync(
        string connectionString, Guid tenantId, Guid projectId,
        ProcessingRunStatus status, int readySegments, int totalSegments, bool withVersions)
    {
        var now = DateTimeOffset.UtcNow;
        var runId = Guid.NewGuid();
        using (TenantContext.BeginScope(tenantId))
        {
            using var context = new AppDbContext(CreateOptions(connectionString));
            context.Set<ProcessingRun>().Add(new ProcessingRun(
                runId, tenantId, projectId, 0, status, "1.0.0",
                new string('a', 64), new string('b', 64), new string('c', 64),
                now, now, now, null));
            for (var i = 0; i < totalSegments; i++)
            {
                var segmentId = Guid.NewGuid();
                var segStatus = i < readySegments ? "Completed" : "Pending";
                context.Set<SpeechSegment>().Add(new SpeechSegment(
                    segmentId, tenantId, projectId, runId, i, i * 1000, (i + 1) * 1000, segStatus, null, now));
                if (withVersions && i < readySegments)
                {
                    context.Set<TranscriptVersion>().Add(new TranscriptVersion(
                        Guid.NewGuid(), tenantId, projectId, runId, segmentId,
                        "mock", "m1", "en", $"transcript {i}", 0.9, null, true, false, now));
                    context.Set<TranslationVersion>().Add(new TranslationVersion(
                        Guid.NewGuid(), tenantId, projectId, runId, segmentId,
                        $"translation {i}", [], 0.9, 0.9, 0.9, "mock", "m1", null, null, true, now));
                }
            }

            await context.SaveChangesAsync().ConfigureAwait(true);
        }

        return runId;
    }

    private static async Task SeedOutputArtifactsAsync(
        string connectionString, Guid tenantId, Guid projectId, Guid runId)
    {
        var now = DateTimeOffset.UtcNow;
        using (TenantContext.BeginScope(tenantId))
        {
            using var context = new AppDbContext(CreateOptions(connectionString));
            foreach (var type in new[] { ArtifactType.Transcript, ArtifactType.Translation, ArtifactType.Timeline, ArtifactType.MixedAudio, ArtifactType.QcReport })
            {
                var contentId = Guid.NewGuid();
                var artifactId = Guid.NewGuid();
                var hash = string.Concat(Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"));
                var storageKey = $"fake/{tenantId:N}/{artifactId:N}.bin";
                context.Set<ContentObject>().Add(new ContentObject(
                    contentId, tenantId, hash, hash, 10, "application/octet-stream", storageKey, ContentObjectStatus.Committed, now, now));
                context.Set<Artifact>().Add(new Artifact(
                    artifactId, tenantId, projectId, runId, StageType.Render, type, "1",
                    contentId, null, null, null, null, ArtifactStatus.Committed, null, now));
                if (type == ArtifactType.MixedAudio)
                {
                    context.Set<OutputAsset>().Add(new OutputAsset(
                        Guid.NewGuid(), tenantId, projectId, runId, artifactId, "Video", 2000, "mp4", now));
                    var audioContent = Guid.NewGuid();
                    var audioArtifact = Guid.NewGuid();
                    var audioHash = string.Concat(Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"));
                    var audioKey = $"fake/{tenantId:N}/{audioArtifact:N}.bin";
                    context.Set<ContentObject>().Add(new ContentObject(
                        audioContent, tenantId, audioHash, audioHash, 10, "application/octet-stream", audioKey, ContentObjectStatus.Committed, now, now));
                    context.Set<Artifact>().Add(new Artifact(
                        audioArtifact, tenantId, projectId, runId, StageType.Render, ArtifactType.MixedAudio, "1",
                        audioContent, null, null, null, null, ArtifactStatus.Committed, null, now));
                    context.Set<OutputAsset>().Add(new OutputAsset(
                        Guid.NewGuid(), tenantId, projectId, runId, audioArtifact, "Audio", 2000, "mp3", now));
                }
            }

            await context.SaveChangesAsync().ConfigureAwait(true);
        }
    }

    private static async Task<string> SeedCompletedExportAsync(
        string connectionString, Guid tenantId, Guid projectId, Guid runId)
    {
        var now = DateTimeOffset.UtcNow;
        var exportId = Guid.NewGuid();
        var contentId = Guid.NewGuid();
        var artifactId = Guid.NewGuid();
        var hash = string.Concat(Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"));
        var storageKey = $"fake/{tenantId:N}/{artifactId:N}.srt";
        using (TenantContext.BeginScope(tenantId))
        {
            using var context = new AppDbContext(CreateOptions(connectionString));
            context.Set<ContentObject>().Add(new ContentObject(
                contentId, tenantId, hash, hash, 12, "application/x-subrip", storageKey, ContentObjectStatus.Committed, now, now));
            context.Set<Artifact>().Add(new Artifact(
                artifactId, tenantId, projectId, runId, StageType.Render, ArtifactType.Export, "1",
                contentId, null, null, null, null, ArtifactStatus.Committed, null, now));
            context.Set<ExportJob>().Add(new ExportJob(
                exportId, tenantId, projectId, runId, ExportFormat.Srt,
                ExportJobStatus.Completed, artifactId.ToString("N"), "{\"completed\":1}", false, now, now));
            context.Set<ExportArtifact>().Add(new ExportArtifact(Guid.NewGuid(), tenantId, exportId, artifactId, now));
            await context.SaveChangesAsync().ConfigureAwait(true);
        }

        return string.Concat("exp_", exportId.ToString("N"));
    }

    private static async Task<long> CountExportsAsync(string connectionString, Guid tenantId, Guid projectId)
    {
        using (TenantContext.BeginScope(tenantId))
        {
            using var context = new AppDbContext(CreateOptions(connectionString));
            return await context.Set<ExportJob>().LongCountAsync(e => e.ProjectId == projectId).ConfigureAwait(true);
        }
    }
}
