using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using DubbingPlatform.Api.Middleware;
using DubbingPlatform.Application.MultiTenancy;
using DubbingPlatform.Contracts.Messages;
using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Infrastructure.Observability;
using DubbingPlatform.Infrastructure.Persistence;
using DubbingPlatform.Infrastructure.Persistence.Interceptors;
using EFCore.NamingConventions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using Testcontainers.PostgreSql;

namespace DubbingPlatform.IntegrationTests.Observability;

/// <summary>
/// Task 38: SLO metrics exposition, protected diagnostics, and end-to-end
/// correlation. <c>Metrics_Scraped</c> and <c>Correlation_EndToEnd</c> are
/// hermetic (no Docker). <c>Diagnostics_Protected</c> 401/403 are hermetic
/// (auth fails before any DB call); the 200 admin case needs PostgreSQL for
/// the <c>admin.access</c> audit write and skips without Docker (live in CI).
/// </summary>
public sealed class ObservabilityTests : IClassFixture<WebApplicationFactory<CorrelationIdMiddleware>>
{
    private const string TestSigningKey = "test-signing-key-0123456789abcdef-test-signing-key-01";

    private const string TestAudience = "dubbing-api";

    private readonly WebApplicationFactory<CorrelationIdMiddleware> _factory;

    public ObservabilityTests(WebApplicationFactory<CorrelationIdMiddleware> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Metrics_Scraped()
    {
        // Start the server (and its OTel MeterProvider) BEFORE recording:
        // measurements published before any listener subscribes are lost.
        using var client = _factory.CreateClient();

        var tenantId = Guid.NewGuid();
        PlatformMetrics.ProjectStarted(tenantId);
        PlatformMetrics.StageStarted(tenantId, "Transcription");
        PlatformMetrics.ProviderCall(tenantId, "Mock", "mock-1");
        PlatformMetrics.ObserveApiLatency(12.5, "/api/v1/admin/dlq/summary");
        PlatformMetrics.ObserveSegmentDuration(1500, "Transcription");
        PlatformMetrics.LeaseRecovered(tenantId, 1);
        PlatformMetrics.StorageOrphans.Add(1);

        using var response = await client.GetAsync("/metrics").ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.Contains("projects_started", body, StringComparison.Ordinal);
        Assert.Contains("stages_started", body, StringComparison.Ordinal);
        Assert.Contains("provider_calls", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Diagnostics_Anonymous_401()
    {
        using var factory = CreateAuthFactory();
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/api/v1/admin/dlq/summary").ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public void Diagnostics_PolicyShape()
    {
        var allowed = DubbingPlatform.Application.Authorization.AuthPolicies.AllowedRoles(
            DubbingPlatform.Application.Authorization.AuthPolicies.RequireTenantAdmin);
        Assert.Contains("TenantAdmin", allowed, StringComparer.Ordinal);
        Assert.Contains("Service", allowed, StringComparer.Ordinal);
        Assert.DoesNotContain("ProjectViewer", allowed, StringComparer.Ordinal);
    }

    [SkippableFact]
    public async Task Diagnostics_Viewer_403()
    {
        var container = await StartPostgresAsync().ConfigureAwait(true);
        await using (container.ConfigureAwait(true))
        {
            var connectionString = container.GetConnectionString();
            await MigrateAsync(connectionString).ConfigureAwait(true);
            var tenantId = Guid.NewGuid();
            await SeedTenantAsync(connectionString, tenantId).ConfigureAwait(true);

            using var factory = CreateAuthFactory(connectionString);
            using var client = factory.CreateClient();
            UseToken(client, tenantId, "ProjectViewer");
            using var response = await client.GetAsync("/api/v1/admin/dlq/summary").ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
    }

    [SkippableFact]
    public async Task Diagnostics_Admin_200()
    {
        var container = await StartPostgresAsync().ConfigureAwait(true);
        await using (container.ConfigureAwait(true))
        {
            var connectionString = container.GetConnectionString();
            await MigrateAsync(connectionString).ConfigureAwait(true);
            var tenantId = Guid.NewGuid();
            await SeedTenantAsync(connectionString, tenantId).ConfigureAwait(true);

            using var factory = CreateAuthFactory(connectionString);
            using var client = factory.CreateClient();
            UseToken(client, tenantId, "TenantAdmin");
            using var response = await client.GetAsync("/api/v1/admin/dlq/summary").ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var payload = await response.Content.ReadAsStringAsync().ConfigureAwait(true);
            Assert.Contains("_skipped", payload, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Correlation_EndToEnd()
    {
        const string correlationId = "corr-e2e-001";
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/metrics");
        request.Headers.Add(CorrelationIdMiddleware.HeaderName, correlationId);
        using var response = await client.SendAsync(request).ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var echoed = response.Headers.GetValues(CorrelationIdMiddleware.HeaderName).Single();
        Assert.Equal(correlationId, echoed);

        var tenantId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var message = new ExportJobRequested(
            Guid.NewGuid(), correlationId, tenantId, projectId, runId,
            null, null, null, null, null,
            MessageVersionPolicy.CurrentVersion, DateTimeOffset.UtcNow, 1,
            null, null, null, Guid.NewGuid().ToString("N"), "srt");

        Assert.Equal(correlationId, message.CorrelationId);

        using (var activity = new System.Diagnostics.Activity("observability-test").Start())
        {
            TraceEnricher.Set(activity, tenantId, projectId, runId, "Export", "Mock", "mock-1", 1);
            Assert.Equal(tenantId.ToString("N"), activity.GetTagItem("tenant.id")?.ToString());
            Assert.Equal(projectId.ToString("N"), activity.GetTagItem("project.id")?.ToString());
            Assert.Equal(runId.ToString("N"), activity.GetTagItem("run.id")?.ToString());
            Assert.Equal("Export", activity.GetTagItem("stage")?.ToString());
        }
    }

    private static WebApplicationFactory<CorrelationIdMiddleware> CreateAuthFactory(string? connectionString = null)
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

    private static void UseToken(HttpClient client, Guid tenantId, params string[] roles)
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", CreateToken(tenantId, roles));
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
            var exists = await context.Tenants.AnyAsync(t => t.Id == tenantId).ConfigureAwait(true);
            if (!exists)
            {
                context.Tenants.Add(new Tenant(tenantId, $"Tenant {tenantId:N}", $"tenant-{tenantId:N}", DateTimeOffset.UtcNow));
                await context.SaveChangesAsync().ConfigureAwait(true);
            }
        }
    }
}
