using System.Diagnostics.Metrics;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using DubbingPlatform.Api.Errors;
using DubbingPlatform.Api.Middleware;
using DubbingPlatform.Api.Observability;
using DubbingPlatform.Api.Sse;
using DubbingPlatform.Application.MultiTenancy;
using DubbingPlatform.Application.Reviews;
using DubbingPlatform.Application.Security;
using DubbingPlatform.Contracts.Messages;
using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Infrastructure.Observability;
using DubbingPlatform.Infrastructure.Persistence;
using DubbingPlatform.Infrastructure.Persistence.Interceptors;
using EFCore.NamingConventions;
using Microsoft.AspNetCore.Http;
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

    [Fact]
    public void BackendMetrics_Smoke_Emits_All_Task038_Instruments()
    {
        var tenantId = Guid.NewGuid();
        var longCounts = new Dictionary<string, long>(StringComparer.Ordinal);
        var doubleCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var tagSnapshots = new Dictionary<string, List<KeyValuePair<string, object?>>>(StringComparer.Ordinal);

        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (string.Equals(instrument.Meter.Name, BackendMetrics.MeterName, StringComparison.Ordinal))
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, state) =>
        {
            longCounts[instrument.Name] = longCounts.TryGetValue(instrument.Name, out var prior) ? prior + measurement : measurement;
            tagSnapshots[instrument.Name] = tags.ToArray().ToList();
        });
        listener.SetMeasurementEventCallback<double>((instrument, measurement, tags, state) =>
        {
            doubleCounts[instrument.Name] = doubleCounts.TryGetValue(instrument.Name, out var prior) ? prior + 1 : 1;
            tagSnapshots[instrument.Name] = tags.ToArray().ToList();
        });
        listener.Start();

        // SSE connect -> counter; reconnect storm stays a bounded counter, not a flood.
        BackendMetrics.SseConnected(tenantId, "/api/v1/projects/stream");
        BackendMetrics.SseReconnected(tenantId, "/api/v1/projects/stream");
        // Failed projection -> failure counter (code only, never content).
        BackendMetrics.NotificationProjectionFailed(tenantId, "notification.project");
        // Read-model query latency histogram.
        BackendMetrics.ObserveReadModelLatency(42.5, tenantId, "progress.read");
        // Upload funnel stages.
        BackendMetrics.UploadFunnelStage(tenantId, "initiated");
        BackendMetrics.UploadFunnelStage(tenantId, "chunk_received");
        BackendMetrics.UploadFunnelStage(tenantId, "completed");
        // Review/export/preview latencies (review/export forward frozen counts too).
        BackendMetrics.ObserveReviewLatency(120.0, tenantId, "review.resolve");
        BackendMetrics.ObserveExportLatency(250.0, tenantId, failed: false, operation: "export.generate");
        BackendMetrics.ObserveExportLatency(300.0, tenantId, failed: true, operation: "export.generate");
        BackendMetrics.ObservePreviewLatency(80.0, tenantId, "preview.generate");
        // Legacy correlation minting outcome counter.
        BackendMetrics.RecordCorrelationOutcome(propagated: true);
        BackendMetrics.RecordCorrelationOutcome(propagated: false);
        // Invalid latencies are dropped, never recorded.
        BackendMetrics.ObserveReadModelLatency(double.NaN, tenantId, "progress.read");
        BackendMetrics.ObservePreviewLatency(-1.0, tenantId, "preview.generate");

        Assert.Equal(1, longCounts[BackendMetrics.SseConnectionsName]);
        Assert.Equal(1, longCounts[BackendMetrics.SseReconnectsName]);
        Assert.Equal(1, longCounts[BackendMetrics.NotificationProjectionFailuresName]);
        Assert.Equal(3, longCounts[BackendMetrics.UploadFunnelName]);
        Assert.Equal(2, longCounts[BackendMetrics.CorrelationMintedName]);
        Assert.Equal(1, doubleCounts[BackendMetrics.ReadModelQueryLatencyName]);
        Assert.Equal(1, doubleCounts[BackendMetrics.ReviewLatencyName]);
        Assert.Equal(2, doubleCounts[BackendMetrics.ExportLatencyName]);
        Assert.Equal(1, doubleCounts[BackendMetrics.PreviewLatencyName]);

        // Every measurement carries a hashed tenant label, never the raw id.
        var rawTenant = tenantId.ToString("N");
        foreach (var entry in tagSnapshots)
        {
            var tenantHash = entry.Value.FirstOrDefault(t => string.Equals(t.Key, "tenant_hash", StringComparison.Ordinal)).Value?.ToString();
            if (entry.Key == BackendMetrics.CorrelationMintedName)
            {
                continue;
            }

            Assert.NotNull(tenantHash);
            Assert.NotEqual(rawTenant, tenantHash);
            Assert.DoesNotContain(rawTenant, string.Join(";", entry.Value.Select(t => t.Value?.ToString() ?? string.Empty)), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void BackendMetrics_TenantHash_And_RouteTemplate_Policy()
    {
        var tenantId = Guid.NewGuid();
        var first = BackendMetrics.HashTenantId(tenantId);
        Assert.Equal(first, BackendMetrics.HashTenantId(tenantId));
        Assert.Equal(16, first.Length);
        Assert.Matches("^[0-9a-f]{16}$", first);
        Assert.DoesNotContain(tenantId.ToString("N"), first, StringComparison.Ordinal);
        Assert.Equal("none", BackendMetrics.HashTenantId(Guid.Empty));

        // Route-template labeling: ids collapse, queries never survive.
        var raw = tenantId.ToString("N");
        var normalized = BackendMetrics.NormalizeRoute($"/api/v1/projects/{raw}?token=hunter2&next=/x#frag");
        Assert.Equal("/api/v1/projects/{id}", normalized);
        Assert.DoesNotContain(raw, normalized, StringComparison.Ordinal);
        Assert.DoesNotContain("token", normalized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("?", normalized, StringComparison.Ordinal);
        Assert.Equal("/api/v1/projects/{id}", BackendMetrics.NormalizeRoute($"/api/v1/projects/{tenantId:D}/"));
        Assert.Equal("unknown", BackendMetrics.NormalizeRoute(null));
        Assert.Equal("/", BackendMetrics.NormalizeRoute("   "));
    }

    [Fact]
    public void Correlation_Missing_Minted_With_PropagatedFalse()
    {
        var (minted, propagated) = CorrelationMiddleware.ResolveWithPropagation(null);
        Assert.False(propagated);
        Assert.Matches("^[0-9a-f]{32}$", minted);

        var (blank, blankPropagated) = CorrelationMiddleware.ResolveWithPropagation("   ");
        Assert.False(blankPropagated);
        Assert.NotEqual(minted, blank);

        var (unsafeId, unsafePropagated) = CorrelationMiddleware.ResolveWithPropagation("bad id! with spaces");
        Assert.False(unsafePropagated);
        Assert.Matches("^[0-9a-f]{32}$", unsafeId);
    }

    [Fact]
    public void Correlation_Propagated_Passthrough()
    {
        const string callerId = "corr-e2e-001";
        var (resolved, propagated) = CorrelationMiddleware.ResolveWithPropagation(callerId);
        Assert.True(propagated);
        Assert.Equal(callerId, resolved);
    }

    [Fact]
    public async Task CorrelationMiddleware_Invoke_Marks_Propagation()
    {
        // Propagated caller id: same id echoed, flag true.
        var propagatedContext = new DefaultHttpContext();
        propagatedContext.Request.Headers[CorrelationMiddleware.HeaderName] = "corr-action-7";
        var propagatedNext = false;
        var propagatedMiddleware = new CorrelationMiddleware(_ =>
        {
            propagatedNext = true;
            return Task.CompletedTask;
        });
        await propagatedMiddleware.InvokeAsync(propagatedContext).ConfigureAwait(true);
        Assert.True(propagatedNext);
        Assert.Equal("corr-action-7", propagatedContext.Items[CorrelationMiddleware.ItemKey]);
        Assert.Equal(true, propagatedContext.Items[CorrelationMiddleware.PropagatedItemKey]);
        Assert.Equal(true, CorrelationMiddleware.WasPropagated(propagatedContext));
        Assert.Equal("corr-action-7", propagatedContext.Response.Headers[CorrelationMiddleware.HeaderName].ToString());

        // Legacy path: minted id, flag false, still echoed so support can trace both sides.
        var mintedContext = new DefaultHttpContext();
        var mintedMiddleware = new CorrelationMiddleware(_ => Task.CompletedTask);
        await mintedMiddleware.InvokeAsync(mintedContext).ConfigureAwait(true);
        Assert.Equal(false, mintedContext.Items[CorrelationMiddleware.PropagatedItemKey]);
        Assert.Equal(false, CorrelationMiddleware.WasPropagated(mintedContext));
        var mintedId = Assert.IsType<string>(mintedContext.Items[CorrelationMiddleware.ItemKey]);
        Assert.Matches("^[0-9a-f]{32}$", mintedId);
        Assert.Equal(mintedId, mintedContext.Response.Headers[CorrelationMiddleware.HeaderName].ToString());
    }

    [Fact]
    public void ReviewVersionConflict_Carries_Correlation_In_Envelope()
    {
        var (statusCode, code, message, details) = ApiError.Map(new ReviewVersionConflictException(7));
        Assert.Equal(409, statusCode);
        Assert.Equal("REVIEW_VERSION_CONFLICT", code);
        Assert.Equal(7, details["currentVersion"]);

        // Support traceability: the id rides in the envelope, never internals.
        const string correlationId = "corr-review-009";
        var envelope = new ErrorResponse(new ErrorBody(code, message, correlationId, details));
        var json = JsonSerializer.Serialize(envelope, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        Assert.Contains("\"correlationId\":\"corr-review-009\"", json, StringComparison.Ordinal);
        Assert.Contains("REVIEW_VERSION_CONFLICT", json, StringComparison.Ordinal);

        // 500s stay generic: message carries no stack or secret.
        var (internalStatus, internalCode, internalMessage, _) = ApiError.Map(new InvalidOperationException("boom"));
        Assert.Equal(500, internalStatus);
        Assert.Equal("INTERNAL_ERROR", internalCode);
        Assert.Equal(ApiError.GenericInternalMessage, internalMessage);
    }

    [Fact]
    public void Scrubber_Never_Emits_Sensitive_Fixtures()
    {
        const string fixture = "token=hunter2&password=s3cret Bearer abc.def.ghi https://cdn.example.com/signed?sig=xyz user transcript body here";
        var redacted = SecretRedactor.Redact(fixture) ?? string.Empty;
        Assert.DoesNotContain("hunter2", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("s3cret", redacted, StringComparison.Ordinal);
        Assert.Contains(SecretRedactor.RedactedValue, redacted, StringComparison.Ordinal);
        Assert.True(SecretRedactor.IsSensitiveKey("accessToken"));
        Assert.True(SecretRedactor.IsSensitiveKey("ApiKey"));
        Assert.False(SecretRedactor.IsSensitiveKey("route"));

        var leaked = SsePayloadPolicy.ScanFrame("{\"signedUrl\":\"https://x\",\"token\":\"abc\"}");
        Assert.Contains("signedUrl", leaked, StringComparer.OrdinalIgnoreCase);
        Assert.Empty(SsePayloadPolicy.ScanFrame("{\"eventType\":\"stage.progress\",\"percent\":42}"));

        var normalized = BackendMetrics.NormalizeRoute("/api/v1/media/mine?access_token=abc&media=bytes");
        Assert.DoesNotContain("access_token", normalized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("?", normalized, StringComparison.Ordinal);
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
