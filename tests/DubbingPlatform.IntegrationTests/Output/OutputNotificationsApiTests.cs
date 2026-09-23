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

namespace DubbingPlatform.IntegrationTests.Output;

/// <summary>
/// Task 012 (combined, superseded by 012A+012B): output + export + notification
/// round-trip over PG with fake storage. Skips when Docker is unavailable.
/// </summary>
public sealed class OutputNotificationsApiTests
{
    private const string TestSigningKey = "test-signing-key-0123456789abcdef-test-signing-key-01";

    private const string TestAudience = "dubbing-api";

    [SkippableFact]
    public async Task Output_Unavailable_BeforeRuns()
    {
        var container = await StartPostgresAsync().ConfigureAwait(true);
        await using (container.ConfigureAwait(true))
        {
            var connectionString = container.GetConnectionString();
            await MigrateAsync(connectionString).ConfigureAwait(true);
            var tenantId = Guid.NewGuid();
            await SeedTenantAsync(connectionString, tenantId).ConfigureAwait(true);

            using var factory = CreateFactory(connectionString, new FakeStorage());
            using var client = factory.CreateClient();
            UseToken(client, tenantId, Guid.NewGuid(), "TenantAdmin");
            var projectId = await CreateProjectAsync(client).ConfigureAwait(true);

            using var response = await client.GetAsync($"/api/v1/projects/{projectId}/output").ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(true));
            Assert.Equal("Unavailable", doc.RootElement.GetProperty("state").GetString());
            Assert.Equal("NO_RUNS_YET", doc.RootElement.GetProperty("reason").GetString());
        }
    }

    [SkippableFact]
    public async Task Output_Partial_Labels_Missing()
    {
        var container = await StartPostgresAsync().ConfigureAwait(true);
        await using (container.ConfigureAwait(true))
        {
            var connectionString = container.GetConnectionString();
            await MigrateAsync(connectionString).ConfigureAwait(true);
            var tenantId = Guid.NewGuid();
            await SeedTenantAsync(connectionString, tenantId).ConfigureAwait(true);

            using var factory = CreateFactory(connectionString, new FakeStorage());
            using var client = factory.CreateClient();
            UseToken(client, tenantId, Guid.NewGuid(), "TenantAdmin");
            var projectId = await CreateProjectAsync(client).ConfigureAwait(true);
            await SeedRunAsync(connectionString, tenantId, ParseProjectId(projectId), ProcessingRunStatus.Running, 1, 2).ConfigureAwait(true);

            using var response = await client.GetAsync($"/api/v1/projects/{projectId}/output").ConfigureAwait(true);
            var payload = await response.Content.ReadAsStringAsync().ConfigureAwait(true);
            var doc = JsonDocument.Parse(payload);
            Assert.Equal("Generating", doc.RootElement.GetProperty("state").GetString());
            Assert.Contains("SEGMENT_PENDING", payload, StringComparison.Ordinal);
            Assert.DoesNotContain("storageKey", payload, StringComparison.OrdinalIgnoreCase);
        }
    }

    [SkippableFact]
    public async Task Export_Idempotent_And_Download_Redirect()
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
            var runId = await SeedRunAsync(connectionString, tenantId, projectGuid, ProcessingRunStatus.Completed, 2, 2).ConfigureAwait(true);
            var exportId = await SeedCompletedExportAsync(connectionString, tenantId, projectGuid, runId).ConfigureAwait(true);

            using var download = await client.GetAsync($"/api/v1/projects/{projectId}/exports/{exportId}/download").ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.Found, download.StatusCode);
            Assert.StartsWith("https://fake-storage.test/", download.Headers.Location?.ToString(), StringComparison.Ordinal);
            Assert.True(fake.LastExpiry <= TimeSpan.FromMinutes(15));
        }
    }

    [SkippableFact]
    public async Task Export_Partial_Guard_409()
    {
        var container = await StartPostgresAsync().ConfigureAwait(true);
        await using (container.ConfigureAwait(true))
        {
            var connectionString = container.GetConnectionString();
            await MigrateAsync(connectionString).ConfigureAwait(true);
            var tenantId = Guid.NewGuid();
            await SeedTenantAsync(connectionString, tenantId).ConfigureAwait(true);

            using var factory = CreateFactory(connectionString, new FakeStorage());
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            UseToken(client, tenantId, Guid.NewGuid(), "TenantAdmin");
            var projectId = await CreateProjectAsync(client).ConfigureAwait(true);
            await SeedRunAsync(connectionString, tenantId, ParseProjectId(projectId), ProcessingRunStatus.Failed, 1, 2).ConfigureAwait(true);

            using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/projects/{projectId}/exports")
            {
                Content = JsonContent.Create(new { format = "srt", allowPartial = false }),
            };
            request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
            using var response = await client.SendAsync(request).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(true));
            var code = body.RootElement.GetProperty("error").GetProperty("code").GetString();
            Assert.True(
                string.Equals(code, "OUTPUT_INCOMPLETE", StringComparison.Ordinal) || string.Equals(code, "EXPORT_INCOMPLETE", StringComparison.Ordinal),
                $"Unexpected {code}");
        }
    }

    [SkippableFact]
    public async Task Notifications_RoundTrip_Idempotent()
    {
        var container = await StartPostgresAsync().ConfigureAwait(true);
        await using (container.ConfigureAwait(true))
        {
            var connectionString = container.GetConnectionString();
            await MigrateAsync(connectionString).ConfigureAwait(true);
            var tenantId = Guid.NewGuid();
            var recipient = Guid.NewGuid();
            await SeedTenantAsync(connectionString, tenantId).ConfigureAwait(true);
            await SeedNotificationAsync(connectionString, tenantId, recipient).ConfigureAwait(true);

            using var factory = CreateFactory(connectionString, new FakeStorage());
            using var client = factory.CreateClient();
            UseToken(client, tenantId, recipient, "ProjectViewer");

            using var list = await client.GetAsync("/api/v1/notifications?unreadOnly=true").ConfigureAwait(true);
            var listDoc = JsonDocument.Parse(await list.Content.ReadAsStringAsync().ConfigureAwait(true));
            Assert.Equal(1, listDoc.RootElement.GetProperty("total").GetInt64());

            using var count = await client.GetAsync("/api/v1/notifications/unread-count").ConfigureAwait(true);
            var countDoc = JsonDocument.Parse(await count.Content.ReadAsStringAsync().ConfigureAwait(true));
            Assert.Equal(1, countDoc.RootElement.GetProperty("unreadCount").GetInt64());

            var id = listDoc.RootElement.GetProperty("items").EnumerateArray().First().GetProperty("id").GetString()!;
            using var read1 = await client.PostAsync($"/api/v1/notifications/{id}/read", null).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.OK, read1.StatusCode);
            using var read2 = await client.PostAsync($"/api/v1/notifications/{id}/read", null).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.OK, read2.StatusCode);

            using var readAll = await client.PostAsync("/api/v1/notifications/read-all", null).ConfigureAwait(true);
            var readAllDoc = JsonDocument.Parse(await readAll.Content.ReadAsStringAsync().ConfigureAwait(true));
            Assert.Equal(0, readAllDoc.RootElement.GetProperty("markedCount").GetInt32());
        }
    }

    [SkippableFact]
    public async Task CrossTenant_404()
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

            using var factory = CreateFactory(connectionString, new FakeStorage());
            using var clientA = factory.CreateClient();
            UseToken(clientA, tenantA, Guid.NewGuid(), "TenantAdmin");
            var projectA = await CreateProjectAsync(clientA).ConfigureAwait(true);

            using var clientB = factory.CreateClient();
            UseToken(clientB, tenantB, Guid.NewGuid(), "TenantAdmin");

            using var output = await clientB.GetAsync($"/api/v1/projects/{projectA}/output").ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.NotFound, output.StatusCode);
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
            return Task.FromResult<Stream>(new MemoryStream([1], writable: false));
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
            Content = JsonContent.Create(new { name = $"C-{Guid.NewGuid():N}", sourceLanguage = "en", targetLanguage = "es" }),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        using var response = await client.SendAsync(request).ConfigureAwait(true);
        var payload = await response.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"Create failed: {(int)response.StatusCode} {payload}");
        using var document = JsonDocument.Parse(payload);
        return document.RootElement.GetProperty("id").GetString()!;
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
        ProcessingRunStatus status, int readySegments, int totalSegments)
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
                context.Set<SpeechSegment>().Add(new SpeechSegment(
                    segmentId, tenantId, projectId, runId, i, i * 1000, (i + 1) * 1000,
                    i < readySegments ? "Completed" : "Pending", null, now));
                if (i < readySegments)
                {
                    context.Set<TranscriptVersion>().Add(new TranscriptVersion(
                        Guid.NewGuid(), tenantId, projectId, runId, segmentId,
                        "mock", "m1", "en", $"t {i}", 0.9, null, true, false, now));
                    context.Set<TranslationVersion>().Add(new TranslationVersion(
                        Guid.NewGuid(), tenantId, projectId, runId, segmentId,
                        $"l {i}", [], 0.9, 0.9, 0.9, "mock", "m1", null, null, true, now));
                }
            }

            await context.SaveChangesAsync().ConfigureAwait(true);
        }

        return runId;
    }

    private static async Task<string> SeedCompletedExportAsync(
        string connectionString, Guid tenantId, Guid projectId, Guid runId)
    {
        var now = DateTimeOffset.UtcNow;
        var exportId = Guid.NewGuid();
        var contentId = Guid.NewGuid();
        var artifactId = Guid.NewGuid();
        var hash = string.Concat(Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"));
        using (TenantContext.BeginScope(tenantId))
        {
            using var context = new AppDbContext(CreateOptions(connectionString));
            context.Set<ContentObject>().Add(new ContentObject(
                contentId, tenantId, hash, hash, 12, "application/x-subrip", $"fake/{tenantId:N}/{artifactId:N}.srt",
                ContentObjectStatus.Committed, now, now));
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

    private static async Task SeedNotificationAsync(string connectionString, Guid tenantId, Guid recipient)
    {
        var now = DateTimeOffset.UtcNow;
        using (TenantContext.BeginScope(tenantId))
        {
            using var context = new AppDbContext(CreateOptions(connectionString));
            context.Set<Notification>().Add(new Notification(
                Guid.NewGuid(), tenantId, recipient, Guid.NewGuid(),
                NotificationType.ProcessingCompleted, NotificationSeverity.Info,
                "Run completed", "Run finished.",
                "ProcessingRun", Guid.NewGuid().ToString("N"), Guid.NewGuid(),
                null, now, null));
            await context.SaveChangesAsync().ConfigureAwait(true);
        }
    }
}
