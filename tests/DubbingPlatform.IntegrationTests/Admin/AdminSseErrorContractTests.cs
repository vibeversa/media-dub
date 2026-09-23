using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using DubbingPlatform.Api.Errors;
using DubbingPlatform.Api.Middleware;
using DubbingPlatform.Api.Sse;
using DubbingPlatform.Application.Authorization;
using DubbingPlatform.Application.Diagnostics;
using DubbingPlatform.Application.Errors;
using DubbingPlatform.Application.Exceptions;
using DubbingPlatform.Application.MultiTenancy;
using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Domain.Enums;
using DubbingPlatform.Infrastructure.Persistence;
using DubbingPlatform.Infrastructure.Persistence.Interceptors;
using EFCore.NamingConventions;
using FluentValidation;
using FluentValidationResults = FluentValidation.Results.ValidationFailure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using Testcontainers.PostgreSql;

namespace DubbingPlatform.IntegrationTests.Admin;

/// <summary>
/// Task 013: frozen admin reads, SSE contract, and error envelope.
/// Hermetic facts assert the closed 14-type set, envelope fields, payload
/// allowlist scan, mapping table, replay window, and oversize drop without
/// Docker. Docker-gated facts replay the HTTP authz matrix, zero-shapes,
/// unknown-route marker, cross-tenant 404, and stream resume in CI.
/// </summary>
public sealed class AdminSseErrorContractTests
{
    private const string TestSigningKey = "test-signing-key-0123456789abcdef-test-signing-key-01";

    private const string TestAudience = "dubbing-api";

    private static readonly string[] ExpectedTypes =
    [
        "project.status_changed",
        "run.status_changed",
        "stage.started",
        "stage.progress",
        "stage.completed",
        "stage.failed",
        "stage.review_required",
        "review.created",
        "review.resolved",
        "export.created",
        "export.completed",
        "export.failed",
        "notification.created",
        "output.ready",
    ];

    [Fact]
    public void Sse_Closed_Type_Set_Is_Exactly_14()
    {
        Assert.Equal(14, SseEventTypes.All.Length);
        foreach (var type in ExpectedTypes)
        {
            Assert.Contains(type, SseEventTypes.All, StringComparer.Ordinal);
            Assert.True(SseEventTypes.IsKnown(type));
        }

        Assert.False(SseEventTypes.IsKnown("stage.unknown"));
        Assert.False(SseEventTypes.IsKnown(null));
        Assert.False(SseEventTypes.IsKnown(string.Empty));
        Assert.Equal(14, SseEventTypes.All.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Sse_Envelope_Carries_All_Fields_And_SchemaVersion_1()
    {
        var envelope = new SseEnvelope(
            Guid.NewGuid().ToString("N"),
            SseEnvelope.CurrentSchemaVersion,
            SseEventTypes.NotificationCreated,
            Guid.NewGuid().ToString("N"),
            Guid.NewGuid().ToString("N"),
            Guid.NewGuid().ToString("N"),
            DateTimeOffset.UtcNow,
            new Dictionary<string, object?> { ["notificationId"] = Guid.NewGuid().ToString("N"), ["status"] = "Open" },
            "corr-013");

        Assert.Equal(1, envelope.SchemaVersion);
        var frame = envelope.ToFrame();
        Assert.NotNull(frame);
        Assert.Contains("id: ", frame, StringComparison.Ordinal);
        Assert.Contains("event: notification.created", frame, StringComparison.Ordinal);

        var dataLine = frame.Split('\n').First(l => l.StartsWith("data: ", StringComparison.Ordinal))["data: ".Length..];
        using var document = JsonDocument.Parse(dataLine);
        var root = document.RootElement;
        foreach (var field in new[] { "eventId", "schemaVersion", "eventType", "tenantId", "projectId", "processingRunId", "occurredAt", "payload" })
        {
            Assert.True(root.TryGetProperty(field, out _), $"Missing envelope field '{field}'.");
        }

        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.True(root.TryGetProperty("correlationId", out _));
    }

    [Fact]
    public void Sse_Unknown_Type_Fails_Serialization()
    {
        var envelope = new SseEnvelope(
            "e1", 1, "stage.unknown", "t", null, null,
            DateTimeOffset.UtcNow, new Dictionary<string, object?>(), "corr");
        Assert.Throws<InvalidOperationException>(() => envelope.ToFrame());
    }

    [Fact]
    public void Sse_Payload_Forbidden_Keys_Scan()
    {
        foreach (var forbidden in SsePayloadPolicy.ForbiddenKeys)
        {
            Assert.True(SsePayloadPolicy.IsForbiddenKey(forbidden));
            var payload = new Dictionary<string, object?> { [forbidden] = "x" };
            Assert.Throws<InvalidOperationException>(() => SsePayloadPolicy.Validate(payload));
            Assert.Contains(forbidden, SsePayloadPolicy.ScanFrame($"{{\"{forbidden}\":\"x\"}}"), StringComparer.OrdinalIgnoreCase);
        }

        var clean = new Dictionary<string, object?>
        {
            ["projectId"] = "abc",
            ["status"] = "Running",
            ["percentApproximate"] = 42,
            ["count"] = 3,
            ["code"] = "PROVIDER_TIMEOUT",
            ["occurredAt"] = DateTimeOffset.UtcNow,
        };
        SsePayloadPolicy.Validate(clean);
        var envelope = new SseEnvelope(
            "e1", 1, SseEventTypes.StageProgress, "t", "p", null,
            DateTimeOffset.UtcNow, clean, "corr");
        Assert.NotNull(envelope.ToFrame());
    }

    [Fact]
    public void Sse_Oversize_Payload_Dropped_Stream_Stays_Open()
    {
        var big = new string('x', 70 * 1024);
        var envelope = new SseEnvelope(
            "e-big", 1, SseEventTypes.StageProgress, "t", "p", null,
            DateTimeOffset.UtcNow,
            new Dictionary<string, object?> { ["note"] = big },
            "corr");
        Assert.Null(envelope.ToFrame());
        Assert.Equal("sse.payload_dropped_total", SseMetrics.PayloadDroppedMetricName);
    }

    [Fact]
    public void Sse_Replay_Window_Last100_With_Truncation_Hint()
    {
        SseEventBuffer.Clear();
        var key = string.Concat("replay-", Guid.NewGuid().ToString("N"));
        for (var index = 0; index < SseEnvelope.ReplayWindow + 1; index++)
        {
            SseEventBuffer.Append(key, new SseEnvelope(
                string.Concat("e", index.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                1, SseEventTypes.StageProgress, "t", "p", null,
                DateTimeOffset.UtcNow, new Dictionary<string, object?>(), "corr"));
        }

        var (foundEvicted, _, truncated) = SseEventBuffer.ReplayAfter(key, "e0");
        Assert.False(foundEvicted);
        Assert.True(truncated);

        var (found, missed, truncatedTail) = SseEventBuffer.ReplayAfter(key, "e99");
        Assert.True(found);
        Assert.False(truncatedTail);
        Assert.Single(missed);

        var (none, empty, noTruncation) = SseEventBuffer.ReplayAfter(key, null);
        Assert.False(none);
        Assert.Empty(empty);
        Assert.False(noTruncation);
        SseEventBuffer.Clear();
    }

    [Fact]
    public void Error_Envelope_Shape_And_Mapping_Table()
    {
        AssertMapping(new DubbingPlatform.Domain.Exceptions.DomainException("bad"), 400, ErrorCodes.ValidationFailed);
        AssertMapping(new ValidationException([new FluentValidationResults("Name", "required")]), 400, ErrorCodes.ValidationFailed);
        AssertMapping(new UnauthorizedAccessException("nope"), 401, ErrorCodes.Unauthorized);
        AssertMapping(new ErrorCodeException(ErrorCodes.TokenExpired, "expired"), 401, ErrorCodes.TokenExpired);
        AssertMapping(new ErrorCodeException(ErrorCodes.TokenReused, "reused"), 401, ErrorCodes.TokenReused);
        AssertMapping(new ErrorCodeException(ErrorCodes.InvalidCredentials, "bad"), 401, ErrorCodes.InvalidCredentials);
        AssertMapping(new ForbiddenException("denied"), 403, ErrorCodes.Forbidden);
        AssertMapping(new NotFoundException("missing"), 404, ErrorCodes.NotFound);
        AssertMapping(new ConflictException("clash"), 409, ErrorCodes.Conflict);
        AssertMapping(new ErrorCodeException(ErrorCodes.SelectionConflict, "stale"), 409, ErrorCodes.SelectionConflict);
        AssertMapping(new ErrorCodeException(ErrorCodes.ReviewVersionConflict, "stale"), 409, ErrorCodes.ReviewVersionConflict);
        AssertMapping(new ErrorCodeException(ErrorCodes.SettingsLockedActiveRun, "locked"), 409, ErrorCodes.SettingsLockedActiveRun);
        AssertMapping(new ErrorCodeException(ErrorCodes.RunAlreadyActive, "active"), 409, ErrorCodes.RunAlreadyActive);
        AssertMapping(new ErrorCodeException(ErrorCodes.Conflict, "PREVIEW_STATE_CONFLICT: busy"), 409, ErrorCodes.Conflict);
        AssertMapping(new QuotaExceededException("too much"), 429, ErrorCodes.QuotaExceeded);
        AssertMapping(new RateLimitedException("slow"), 429, ErrorCodes.RateLimited);
        AssertMapping(new ErrorCodeException(ErrorCodes.ProviderFailed, "downstream"), 502, ErrorCodes.ProviderFailed);
        AssertMapping(new ErrorCodeException(ErrorCodes.ProviderInvalidResponse, "bad json"), 502, ErrorCodes.ProviderInvalidResponse);
    }

    [Fact]
    public void Error_500_Never_Leaks_Internals()
    {
        var (status, code, message, _) = ApiError.Map(
            new InvalidOperationException("Host=db Password=hunter2 failed."));
        Assert.Equal(500, status);
        Assert.Equal(ErrorCodes.InternalError, code);
        Assert.Equal("An unexpected error occurred.", message);
        Assert.DoesNotContain("hunter2", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Admin_Unknown_Marker_Rides_On_NotFound_And_Matrix_Covers_Routes()
    {
        var (status, code, message, _) = ApiError.Map(
            new NotFoundException($"{ApiError.AdminRouteUnknownMarker}: admin route 'nope' was not found."));
        Assert.Equal(404, status);
        Assert.Equal(ErrorCodes.NotFound, code);
        Assert.Contains(ApiError.AdminRouteUnknownMarker, message, StringComparison.Ordinal);

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
        })
        {
            Assert.True(RoleMatrix.IsAllowed(route, ["TenantAdmin"]));
            Assert.True(RoleMatrix.IsAllowed(route, ["Service"]));
            Assert.False(RoleMatrix.IsAllowed(route, ["ProjectViewer"]));
        }
    }

    [SkippableFact]
    public async Task Admin_Anonymous_401()
    {
        using var factory = CreateFactory(null);
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/api/v1/admin/usage").ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [SkippableFact]
    public async Task Admin_Viewer_403()
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
            using var response = await client.GetAsync("/api/v1/admin/usage").ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
    }

    [SkippableFact]
    public async Task Admin_Admin_200_Usage_Quotas()
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

            using var usage = await client.GetAsync("/api/v1/admin/usage").ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.OK, usage.StatusCode);
            using var usageDoc = JsonDocument.Parse(await usage.Content.ReadAsStringAsync().ConfigureAwait(true));
            foreach (var field in new[] { "correlationId", "storageUsedBytes", "storageQuotaBytes", "monthCostUsd", "activeRuns", "pendingReviews" })
            {
                Assert.True(usageDoc.RootElement.TryGetProperty(field, out _), $"Missing usage field '{field}'.");
            }

            using var quotas = await client.GetAsync("/api/v1/admin/quotas").ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.OK, quotas.StatusCode);
            using var quotasDoc = JsonDocument.Parse(await quotas.Content.ReadAsStringAsync().ConfigureAwait(true));
            Assert.True(quotasDoc.RootElement.TryGetProperty("maxStorageBytes", out _));
        }
    }

    [SkippableFact]
    public async Task Admin_Diagnostics_Empty_Zero_Shapes()
    {
        var container = await StartPostgresAsync().ConfigureAwait(true);
        await using (container.ConfigureAwait(true))
        {
            var connectionString = container.GetConnectionString();
            await MigrateAsync(connectionString).ConfigureAwait(true);
            var tenantId = Guid.NewGuid();
            await SeedTenantAsync(connectionString, tenantId).ConfigureAwait(true);
            var userId = Guid.NewGuid();
            await SeedMembershipAsync(connectionString, tenantId, userId).ConfigureAwait(true);

            using var factory = CreateFactory(connectionString);
            using var client = factory.CreateClient();
            UseToken(client, tenantId, userId, "TenantAdmin");

            using var dlq = await client.GetAsync("/api/v1/admin/diagnostics/dlq").ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.OK, dlq.StatusCode);
            using var dlqDoc = JsonDocument.Parse(await dlq.Content.ReadAsStringAsync().ConfigureAwait(true));
            Assert.Equal(0, dlqDoc.RootElement.GetProperty("depth").GetInt64());

            using var leases = await client.GetAsync("/api/v1/admin/diagnostics/leases").ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.OK, leases.StatusCode);
            using var leasesDoc = JsonDocument.Parse(await leases.Content.ReadAsStringAsync().ConfigureAwait(true));
            Assert.Equal(0, leasesDoc.RootElement.GetProperty("total").GetInt64());

            using var orphans = await client.GetAsync("/api/v1/admin/diagnostics/orphans").ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.OK, orphans.StatusCode);
            using var orphansDoc = JsonDocument.Parse(await orphans.Content.ReadAsStringAsync().ConfigureAwait(true));
            Assert.Equal(0, orphansDoc.RootElement.GetProperty("items").GetArrayLength());
            Assert.False(orphansDoc.RootElement.GetProperty("hasMore").GetBoolean());

            using var queues = await client.GetAsync("/api/v1/admin/diagnostics/queues").ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.OK, queues.StatusCode);

            using var backlog = await client.GetAsync("/api/v1/admin/diagnostics/review-backlog").ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.OK, backlog.StatusCode);

            using var health = await client.GetAsync("/api/v1/admin/provider-health").ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);

            using var routes = await client.GetAsync("/api/v1/admin/provider-routes").ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.OK, routes.StatusCode);
        }
    }

    [SkippableFact]
    public async Task Admin_Unknown_Subpath_404_Marker()
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
            using var response = await client.GetAsync("/api/v1/admin/no-such-route").ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(true);
            Assert.Contains("NOT_FOUND", body, StringComparison.Ordinal);
            Assert.Contains(ApiError.AdminRouteUnknownMarker, body, StringComparison.Ordinal);
        }
    }

    [SkippableFact]
    public async Task Admin_CrossTenant_Resource_404()
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
            var executionId = await SeedStageExecutionAsync(connectionString, tenantA).ConfigureAwait(true);

            using var factory = CreateFactory(connectionString);
            using var clientB = factory.CreateClient();
            UseToken(clientB, tenantB, Guid.NewGuid(), "TenantAdmin");
            using var cross = await clientB.GetAsync($"/api/v1/admin/stages/{executionId:N}").ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.NotFound, cross.StatusCode);
        }
    }

    [SkippableFact]
    public async Task Stream_Accepts_LastEventId_As_EventStream()
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
            var projectId = await CreateProjectAsync(client).ConfigureAwait(true);

            var token = CreateToken(tenantId, Guid.NewGuid(), "TenantAdmin");
            using var queryClient = factory.CreateClient();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"/api/v1/projects/{projectId}/progress/stream?access_token={Uri.EscapeDataString(token)}");
            request.Headers.TryAddWithoutValidation("Last-Event-ID", Guid.NewGuid().ToString("N"));
            using var streamed = await queryClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.OK, streamed.StatusCode);
            Assert.Equal("text/event-stream", streamed.Content.Headers.ContentType?.MediaType);
            cts.Cancel();
        }
    }

    private static void AssertMapping(Exception exception, int expectedStatus, string expectedCode)
    {
        var (status, code, _, _) = ApiError.Map(exception);
        Assert.Equal(expectedStatus, status);
        Assert.Equal(expectedCode, code);
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

    private static async Task<string> CreateProjectAsync(HttpClient client)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/projects")
        {
            Content = System.Net.Http.Json.JsonContent.Create(new { name = $"A-{Guid.NewGuid():N}", sourceLanguage = "en", targetLanguage = "es" }),
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

    private static async Task SeedMembershipAsync(string connectionString, Guid tenantId, Guid userId)
    {
        using (TenantContext.BeginScope(tenantId))
        {
            var options = CreateOptions(connectionString);
            using var context = new AppDbContext(options);
            var now = DateTimeOffset.UtcNow;
            context.Set<TenantUser>().Add(new TenantUser(
                userId, tenantId, "sub-" + userId.ToString("N"), "owner@example.com", "Owner", TenantUserStatus.Active, now, now));
            context.Set<ProjectMembership>().Add(new ProjectMembership(
                Guid.NewGuid(), tenantId, Guid.NewGuid(), userId, ProjectRole.ProjectOwner, null, now));
            await context.SaveChangesAsync().ConfigureAwait(true);
        }
    }

    private static async Task<Guid> SeedStageExecutionAsync(string connectionString, Guid tenantId)
    {
        var now = DateTimeOffset.UtcNow;
        var id = Guid.NewGuid();
        using (TenantContext.BeginScope(tenantId))
        {
            var options = CreateOptions(connectionString);
            using var context = new AppDbContext(options);
            context.Set<StageExecution>().Add(new StageExecution(
                id, tenantId, Guid.NewGuid(), Guid.NewGuid(), StageType.Transcription,
                ScopeType.Run, "run", null, 1, StageStatus.Running,
                "worker-a", "lease-" + Guid.NewGuid().ToString("N"), 1, now.AddMinutes(5),
                now, null, null, "config", "snapshot", null, null, null, now, now));
            await context.SaveChangesAsync().ConfigureAwait(true);
        }

        return id;
    }
}
