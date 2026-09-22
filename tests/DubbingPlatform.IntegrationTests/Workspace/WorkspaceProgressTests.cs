using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using DubbingPlatform.Api.Middleware;
using DubbingPlatform.Application.MultiTenancy;
using DubbingPlatform.Application.Workspace;
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

namespace DubbingPlatform.IntegrationTests.Workspace;

/// <summary>
/// Task 008: processing lifecycle, workspace aggregate, progress, and SSE shell
/// over PG. Skips when Docker is unavailable (CI runs live).
/// Cross-tenant project reads stay 403 per Task 007, except the SSE stream
/// which returns 404 to avoid leaking existence via an infinite transport.
/// </summary>
public sealed class WorkspaceProgressTests
{
    private const string TestSigningKey = "test-signing-key-0123456789abcdef-test-signing-key-01";

    private const string TestAudience = "dubbing-api";

    [SkippableFact]
    public async Task Start_Idempotency_Replay_Same_RunId_One_Run()
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

            var projectId = await CreateProjectAsync(client).ConfigureAwait(true);
            await PromoteToMediaReadyAsync(connectionString, tenantId, ParsePublicId(projectId)).ConfigureAwait(true);

            var key = Guid.NewGuid().ToString("N");
            var first = await SendStartAsync(client, projectId, key, body: new { }).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
            Assert.False(first.Headers.Contains(ProcessingReplayHeader()));
            var firstDoc = JsonDocument.Parse(await first.Content.ReadAsStringAsync().ConfigureAwait(true));
            var runId = firstDoc.RootElement.GetProperty("runId").GetString()!;
            Assert.StartsWith("run_", runId, StringComparison.Ordinal);

            var second = await SendStartAsync(client, projectId, key, body: new { }).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.OK, second.StatusCode);
            Assert.True(second.Headers.Contains(ProcessingReplayHeader()));
            Assert.Equal("true", second.Headers.GetValues(ProcessingReplayHeader()).First());
            var secondDoc = JsonDocument.Parse(await second.Content.ReadAsStringAsync().ConfigureAwait(true));
            Assert.Equal(runId, secondDoc.RootElement.GetProperty("runId").GetString());

            var count = await CountRunsAsync(connectionString, tenantId, ParsePublicId(projectId)).ConfigureAwait(true);
            Assert.Equal(1, count);
        }
    }

    [SkippableFact]
    public async Task Start_Missing_Key_400()
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

            var projectId = await CreateProjectAsync(client).ConfigureAwait(true);
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/projects/{projectId}/processing")
            {
                Content = JsonContent.Create(new { }),
            };
            using var response = await client.SendAsync(request).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(true));
            Assert.Equal("IDEMPOTENCY_KEY_REQUIRED", body.RootElement.GetProperty("error").GetProperty("code").GetString());
        }
    }

    [SkippableFact]
    public async Task Start_Active_Conflict_409_Unless_Force_With_Retry_Permission()
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

            var projectId = await CreateProjectAsync(client).ConfigureAwait(true);
            await PromoteToMediaReadyAsync(connectionString, tenantId, ParsePublicId(projectId)).ConfigureAwait(true);

            var first = await SendStartAsync(client, projectId, NewKey(), body: new { }).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);

            var second = await SendStartAsync(client, projectId, NewKey(), body: new { }).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
            var secondBody = JsonDocument.Parse(await second.Content.ReadAsStringAsync().ConfigureAwait(true));
            Assert.Equal("RUN_ALREADY_ACTIVE", secondBody.RootElement.GetProperty("error").GetProperty("code").GetString());

            // Force without retry permission (viewer) still 409 (actually 403 first, but force+viewer cannot start anyway).
            // Force with retry permission (TenantAdmin) bypasses and creates a second run.
            using var forced = await SendStartAsync(client, $"{projectId}/processing?force=true", NewKey(), projectIdIsFullPath: true, body: new { }).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.Accepted, forced.StatusCode);
        }
    }

    [SkippableFact]
    public async Task Cancel_Idempotent_Twice_202_And_Terminal_409()
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

            var projectId = await CreateProjectAsync(client).ConfigureAwait(true);
            await PromoteToMediaReadyAsync(connectionString, tenantId, ParsePublicId(projectId)).ConfigureAwait(true);
            var runId = await StartRunAsync(client, projectId).ConfigureAwait(true);

            var first = await SendCancelAsync(client, projectId, runId, NewKey()).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);

            var second = await SendCancelAsync(client, projectId, runId, NewKey()).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.Accepted, second.StatusCode);

            await SetRunStatusAsync(connectionString, tenantId, ParseRunId(runId), ProcessingRunStatus.Completed).ConfigureAwait(true);
            var terminal = await SendCancelAsync(client, projectId, runId, NewKey()).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.Conflict, terminal.StatusCode);
            var body = JsonDocument.Parse(await terminal.Content.ReadAsStringAsync().ConfigureAwait(true));
            Assert.Equal("RUN_ALREADY_TERMINAL", body.RootElement.GetProperty("error").GetProperty("code").GetString());
        }
    }

    [SkippableFact]
    public async Task Retry_Success_Copies_Hash_And_Config_Changed_409()
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

            var projectId = await CreateProjectAsync(client).ConfigureAwait(true);
            var projectGuid = ParsePublicId(projectId);
            await PromoteToMediaReadyAsync(connectionString, tenantId, projectGuid).ConfigureAwait(true);
            var runId = await StartRunAsync(client, projectId).ConfigureAwait(true);

            var oldHash = await GetRunConfigHashAsync(connectionString, tenantId, ParseRunId(runId)).ConfigureAwait(true);
            await SetRunAndProjectFailedAsync(connectionString, tenantId, projectGuid, ParseRunId(runId)).ConfigureAwait(true);

            // Success: copies config hash and links retryOfRunId.
            var retry = await SendRetryAsync(client, projectId, runId, NewKey()).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.Accepted, retry.StatusCode);
            var retryDoc = JsonDocument.Parse(await retry.Content.ReadAsStringAsync().ConfigureAwait(true));
            var newRunId = retryDoc.RootElement.GetProperty("runId").GetString()!;
            Assert.NotEqual(runId, newRunId);
            Assert.Equal(runId, retryDoc.RootElement.GetProperty("retryOfRunId").GetString());
            Assert.Equal(oldHash, retryDoc.RootElement.GetProperty("configHash").GetString());

            // Reuse of the start key for a different payload (retry) → 422.
            // Use a fresh project to isolate: start key then retry with same key.
            var project2 = await CreateProjectAsync(client).ConfigureAwait(true);
            var project2Guid = ParsePublicId(project2);
            await PromoteToMediaReadyAsync(connectionString, tenantId, project2Guid).ConfigureAwait(true);
            var sharedKey = NewKey();
            var started = await SendStartAsync(client, project2, sharedKey, body: new { }).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);
            var startedDoc = JsonDocument.Parse(await started.Content.ReadAsStringAsync().ConfigureAwait(true));
            var startedRun = startedDoc.RootElement.GetProperty("runId").GetString()!;
            await SetRunAndProjectFailedAsync(connectionString, tenantId, project2Guid, ParseRunId(startedRun)).ConfigureAwait(true);
            var reused = await SendRetryAsync(client, project2, startedRun, sharedKey).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, reused.StatusCode);
            var reusedBody = JsonDocument.Parse(await reused.Content.ReadAsStringAsync().ConfigureAwait(true));
            Assert.Equal("IDEMPOTENCY_KEY_REUSED", reusedBody.RootElement.GetProperty("error").GetProperty("code").GetString());

            // Config changed since run → 409 with current hash.
            await PatchProcessingSettingsAsync(client, projectId, "{\"schemaVersion\":1,\"outputProfile\":\"hq\"}").ConfigureAwait(true);
            var currentHash = await GetProjectConfigHashAsync(connectionString, tenantId, projectGuid).ConfigureAwait(true);
            Assert.NotEqual(oldHash, currentHash);
            var stale = await SendRetryAsync(client, projectId, runId, NewKey()).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
            var staleBody = JsonDocument.Parse(await stale.Content.ReadAsStringAsync().ConfigureAwait(true));
            Assert.Equal("CONFIG_CHANGED_SINCE_RUN", staleBody.RootElement.GetProperty("error").GetProperty("code").GetString());
            Assert.Contains(currentHash, await stale.Content.ReadAsStringAsync().ConfigureAwait(true), StringComparison.Ordinal);
        }
    }

    [SkippableFact]
    public async Task Workspace_Shape_Bounded_Queries()
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

            var projectId = await CreateProjectAsync(client).ConfigureAwait(true);
            await PromoteToMediaReadyAsync(connectionString, tenantId, ParsePublicId(projectId)).ConfigureAwait(true);
            await StartRunAsync(client, projectId).ConfigureAwait(true);

            using var response = await client.GetAsync($"/api/v1/projects/{projectId}/workspace").ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(true));

            foreach (var section in new[] { "project", "media", "run", "phase", "stage", "progress", "review", "warnings", "output", "cost", "activity", "permissions" })
            {
                Assert.True(body.RootElement.TryGetProperty(section, out _), $"Missing section '{section}'.");
            }

            var progress = body.RootElement.GetProperty("progress");
            Assert.True(progress.TryGetProperty("percentApproximate", out _));
            Assert.True(progress.TryGetProperty("currentStage", out _));
            Assert.True(progress.TryGetProperty("updatedAt", out _));

            var review = body.RootElement.GetProperty("review");
            Assert.True(review.TryGetProperty("pendingCount", out _));
            Assert.True(review.TryGetProperty("oldestWaitingAt", out _));

            var cost = body.RootElement.GetProperty("cost");
            Assert.True(cost.TryGetProperty("runCost", out _));
            Assert.True(cost.TryGetProperty("monthToDate", out _));

            var activity = body.RootElement.GetProperty("activity");
            Assert.True(activity.TryGetProperty("recent", out var recent));
            Assert.True(recent.GetArrayLength() <= 10);

            var permissions = body.RootElement.GetProperty("permissions");
            Assert.True(permissions.TryGetProperty("allowedActions", out _));

            Assert.True(response.Headers.Contains("X-Workspace-Queries"));
            var queries = int.Parse(response.Headers.GetValues("X-Workspace-Queries").First(), System.Globalization.CultureInfo.InvariantCulture);
            Assert.True(queries <= WorkspaceService.QueryCeiling, $"Workspace used {queries} queries (ceiling {WorkspaceService.QueryCeiling}).");
        }
    }

    [SkippableFact]
    public async Task Progress_Approximate_Field_And_No_Stages_Zero_Null()
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

            var projectId = await CreateProjectAsync(client).ConfigureAwait(true);
            await PromoteToMediaReadyAsync(connectionString, tenantId, ParsePublicId(projectId)).ConfigureAwait(true);
            await StartRunAsync(client, projectId).ConfigureAwait(true);

            using var progress = await client.GetAsync($"/api/v1/projects/{projectId}/progress").ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.OK, progress.StatusCode);
            var doc = JsonDocument.Parse(await progress.Content.ReadAsStringAsync().ConfigureAwait(true));
            Assert.True(doc.RootElement.TryGetProperty("percentApproximate", out var percent));
            Assert.True(doc.RootElement.TryGetProperty("currentStage", out _));
            Assert.True(doc.RootElement.TryGetProperty("notEta", out var notEta));
            Assert.True(notEta.GetBoolean());
            Assert.Equal(
                doc.RootElement.GetProperty("percentageIndicator").GetInt32(),
                percent.GetInt32());

            // Run with no stages yet → zeros, not 404.
            var bareProject = await CreateProjectAsync(client).ConfigureAwait(true);
            var bareGuid = ParsePublicId(bareProject);
            await PromoteToMediaReadyAsync(connectionString, tenantId, bareGuid).ConfigureAwait(true);
            var bareRun = Guid.NewGuid();
            await SeedBareRunAsync(connectionString, tenantId, bareGuid, bareRun).ConfigureAwait(true);

            using var bare = await client.GetAsync($"/api/v1/projects/{bareProject}/progress").ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.OK, bare.StatusCode);
            var bareDoc = JsonDocument.Parse(await bare.Content.ReadAsStringAsync().ConfigureAwait(true));
            Assert.Equal(0, bareDoc.RootElement.GetProperty("percentApproximate").GetInt32());
            Assert.Equal(JsonValueKind.Null, bareDoc.RootElement.GetProperty("currentStage").ValueKind);
        }
    }

    [SkippableFact]
    public async Task Archived_Blocks_Start_Retry_409()
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

            var projectId = await CreateProjectAsync(client).ConfigureAwait(true);
            using (var archive = await client.PostAsync($"/api/v1/projects/{projectId}/archive", new StringContent(string.Empty)).ConfigureAwait(true))
            {
                Assert.Equal(HttpStatusCode.OK, archive.StatusCode);
            }

            var start = await SendStartAsync(client, projectId, NewKey(), body: new { }).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.Conflict, start.StatusCode);
            var startBody = JsonDocument.Parse(await start.Content.ReadAsStringAsync().ConfigureAwait(true));
            Assert.Equal("PROJECT_ARCHIVED", startBody.RootElement.GetProperty("error").GetProperty("code").GetString());

            // Seed a failed run directly so retry has a target even though archived.
            var runId = Guid.NewGuid();
            await SeedRunAsync(connectionString, tenantId, ParsePublicId(projectId), ProcessingRunStatus.Failed, runId).ConfigureAwait(true);
            var retry = await SendRetryAsync(client, projectId, ToRunId(runId), NewKey()).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.Conflict, retry.StatusCode);
            var retryBody = JsonDocument.Parse(await retry.Content.ReadAsStringAsync().ConfigureAwait(true));
            Assert.Equal("PROJECT_ARCHIVED", retryBody.RootElement.GetProperty("error").GetProperty("code").GetString());
        }
    }

    [SkippableFact]
    public async Task Authz_Viewer_Cannot_Start_But_Can_Read()
    {
        var container = await StartPostgresAsync().ConfigureAwait(true);
        await using (container.ConfigureAwait(true))
        {
            var connectionString = container.GetConnectionString();
            await MigrateAsync(connectionString).ConfigureAwait(true);
            var tenantId = Guid.NewGuid();
            await SeedTenantAsync(connectionString, tenantId).ConfigureAwait(true);

            using var factory = CreateFactory(connectionString);
            using var admin = factory.CreateClient();
            UseToken(admin, tenantId, "TenantAdmin");
            var projectId = await CreateProjectAsync(admin).ConfigureAwait(true);

            using var viewer = factory.CreateClient();
            UseToken(viewer, tenantId, "ProjectViewer");
            var start = await SendStartAsync(viewer, projectId, NewKey(), body: new { }).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.Forbidden, start.StatusCode);

            using var workspace = await viewer.GetAsync($"/api/v1/projects/{projectId}/workspace").ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.OK, workspace.StatusCode);

            using var runs = await viewer.GetAsync($"/api/v1/projects/{projectId}/processing?page=1&pageSize=20").ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.OK, runs.StatusCode);
        }
    }

    [SkippableFact]
    public async Task Stream_Auth_And_Tenant_Checks()
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
            var projectId = await CreateProjectAsync(clientA).ConfigureAwait(true);

            // Unauthenticated → 401.
            using var anon = factory.CreateClient();
            using var anonResponse = await anon.GetAsync($"/api/v1/projects/{projectId}/progress/stream").ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.Unauthorized, anonResponse.StatusCode);

            // Cross-tenant → 404 (no existence leak on the stream transport).
            using var clientB = factory.CreateClient();
            UseToken(clientB, tenantB, "TenantAdmin");
            using var cross = await clientB.GetAsync($"/api/v1/projects/{projectId}/progress/stream").ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.NotFound, cross.StatusCode);

            // Query access_token succeeds and streams event-stream with Last-Event-ID accepted.
            var token = CreateToken(tenantA, "TenantAdmin");
            using var queryClient = factory.CreateClient();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/projects/{projectId}/progress/stream?access_token={Uri.EscapeDataString(token)}");
            request.Headers.TryAddWithoutValidation("Last-Event-ID", "42");
            using var streamed = await queryClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.OK, streamed.StatusCode);
            Assert.Equal("text/event-stream", streamed.Content.Headers.ContentType?.MediaType);
            cts.Cancel();
        }
    }

    [SkippableFact]
    public async Task Run_List_Paginated_And_Thin_Projections_200()
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

            var projectId = await CreateProjectAsync(client).ConfigureAwait(true);
            await PromoteToMediaReadyAsync(connectionString, tenantId, ParsePublicId(projectId)).ConfigureAwait(true);
            await StartRunAsync(client, projectId).ConfigureAwait(true);

            using var list = await client.GetAsync($"/api/v1/projects/{projectId}/processing?page=1&pageSize=20").ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.OK, list.StatusCode);
            var listDoc = JsonDocument.Parse(await list.Content.ReadAsStringAsync().ConfigureAwait(true));
            Assert.True(listDoc.RootElement.TryGetProperty("items", out _));
            Assert.True(listDoc.RootElement.TryGetProperty("total", out _));
            Assert.True(listDoc.RootElement.TryGetProperty("hasMore", out _));
            Assert.True(listDoc.RootElement.GetProperty("total").GetInt64() >= 1);

            foreach (var path in new[] { "activity", "output", "quality" })
            {
                using var projection = await client.GetAsync($"/api/v1/projects/{projectId}/{path}").ConfigureAwait(true);
                Assert.Equal(HttpStatusCode.OK, projection.StatusCode);
            }
        }
    }

    private static string ProcessingReplayHeader()
    {
        return "Idempotent-Replayed";
    }

    private static string NewKey()
    {
        return Guid.NewGuid().ToString("N");
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
            new("sub", Guid.NewGuid().ToString("D")),
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
            Content = JsonContent.Create(new { name = $"W-{Guid.NewGuid():N}", sourceLanguage = "en", targetLanguage = "es" }),
        };
        request.Headers.Add("Idempotency-Key", NewKey());
        using var response = await client.SendAsync(request).ConfigureAwait(true);
        var payload = await response.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"Create failed: {(int)response.StatusCode} {payload}");
        using var document = JsonDocument.Parse(payload);
        return document.RootElement.GetProperty("id").GetString()!;
    }

    private static async Task<HttpResponseMessage> SendStartAsync(HttpClient client, string projectIdOrPath, string key, object? body, bool projectIdIsFullPath = false)
    {
        var url = projectIdIsFullPath ? $"/api/v1/projects/{projectIdOrPath}" : $"/api/v1/projects/{projectIdOrPath}/processing";
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(body ?? new { }),
        };
        request.Headers.Add("Idempotency-Key", key);
        return await client.SendAsync(request).ConfigureAwait(true);
    }

    private static async Task<string> StartRunAsync(HttpClient client, string projectId)
    {
        using var response = await SendStartAsync(client, projectId, NewKey(), body: new { }).ConfigureAwait(true);
        var payload = await response.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.True(response.StatusCode == HttpStatusCode.Accepted, $"Start failed: {(int)response.StatusCode} {payload}");
        using var document = JsonDocument.Parse(payload);
        return document.RootElement.GetProperty("runId").GetString()!;
    }

    private static async Task<HttpResponseMessage> SendCancelAsync(HttpClient client, string projectId, string runId, string key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/projects/{projectId}/processing/{runId}/cancel")
        {
            Content = JsonContent.Create(new { reason = "test" }),
        };
        request.Headers.Add("Idempotency-Key", key);
        return await client.SendAsync(request).ConfigureAwait(true);
    }

    private static async Task<HttpResponseMessage> SendRetryAsync(HttpClient client, string projectId, string runId, string key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/projects/{projectId}/processing/{runId}/retry")
        {
            Content = new StringContent(string.Empty, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("Idempotency-Key", key);
        return await client.SendAsync(request).ConfigureAwait(true);
    }

    private static async Task PatchProcessingSettingsAsync(HttpClient client, string projectId, string processingSettingsJson)
    {
        using var request = new HttpRequestMessage(HttpMethod.Patch, $"/api/v1/projects/{projectId}")
        {
            Content = new StringContent(string.Concat("{\"processingSettings\":", processingSettingsJson, "}"), Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("Idempotency-Key", NewKey());
        using var response = await client.SendAsync(request).ConfigureAwait(true);
        var payload = await response.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"Patch failed: {(int)response.StatusCode} {payload}");
    }

    private static Guid ParsePublicId(string publicId)
    {
        var hex = publicId.StartsWith("prj_", StringComparison.Ordinal)
            ? publicId.Substring("prj_".Length)
            : publicId;
        return Guid.ParseExact(hex, "N");
    }

    private static Guid ParseRunId(string publicId)
    {
        var hex = publicId.StartsWith("run_", StringComparison.Ordinal)
            ? publicId.Substring("run_".Length)
            : publicId;
        return Guid.ParseExact(hex.Replace("-", string.Empty, StringComparison.Ordinal), "N");
    }

    private static string ToRunId(Guid id)
    {
        return string.Concat("run_", id.ToString("N"));
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

    private static async Task PromoteToMediaReadyAsync(string connectionString, Guid tenantId, Guid projectId)
    {
        var now = DateTimeOffset.UtcNow;
        var contentId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var hash = new string('a', 64);
        using (TenantContext.BeginScope(tenantId))
        {
            using var context = new AppDbContext(CreateOptions(connectionString));
            context.Set<ContentObject>().Add(new ContentObject(
                contentId, tenantId, hash, hash, 1024, "video/mp4",
                string.Concat(tenantId.ToString("N"), "/seed/source.mp4"),
                ContentObjectStatus.Committed, now, now));
            context.Set<MediaAsset>().Add(new MediaAsset(
                assetId, tenantId, projectId, contentId, "source.mp4", "mp4", "aac", "h264",
                1024, 2000, 48000, 2, "stereo", MediaAssetStatus.Valid, null, hash, now));
            await context.SaveChangesAsync().ConfigureAwait(true);
        }

        using (TenantContext.BeginMaintenanceScope())
        {
            using var context = new AppDbContext(CreateOptions(connectionString));
            await context.Database.ExecuteSqlRawAsync(
                "UPDATE dubbing_projects SET status = {0}, updated_at = {1} WHERE id = {2}",
                ProjectStatus.MediaReady.ToString(), now, projectId).ConfigureAwait(true);
        }
    }

    private static async Task SeedBareRunAsync(string connectionString, Guid tenantId, Guid projectId, Guid runId)
    {
        var now = DateTimeOffset.UtcNow;
        using (TenantContext.BeginScope(tenantId))
        {
            using var context = new AppDbContext(CreateOptions(connectionString));
            context.Set<ProcessingRun>().Add(new ProcessingRun(
                runId, tenantId, projectId, 0, ProcessingRunStatus.Pending,
                "1.0.0", new string('a', 64), new string('b', 64), new string('c', 64),
                now, now, null, null));
            await context.SaveChangesAsync().ConfigureAwait(true);
        }
    }

    private static async Task SeedRunAsync(string connectionString, Guid tenantId, Guid projectId, ProcessingRunStatus status, Guid? runId = null)
    {
        var now = DateTimeOffset.UtcNow;
        using (TenantContext.BeginScope(tenantId))
        {
            using var context = new AppDbContext(CreateOptions(connectionString));
            context.Set<ProcessingRun>().Add(new ProcessingRun(
                runId ?? Guid.NewGuid(), tenantId, projectId, 0, status,
                "1.0.0", new string('a', 64), new string('b', 64), new string('c', 64),
                now, now, now, null));
            await context.SaveChangesAsync().ConfigureAwait(true);
        }
    }

    private static async Task SetRunStatusAsync(string connectionString, Guid tenantId, Guid runId, ProcessingRunStatus status)
    {
        using (TenantContext.BeginMaintenanceScope())
        {
            using var context = new AppDbContext(CreateOptions(connectionString));
            await context.Database.ExecuteSqlRawAsync(
                "UPDATE processing_runs SET status = {0}, updated_at = {1} WHERE id = {2}",
                status.ToString(), DateTimeOffset.UtcNow, runId).ConfigureAwait(true);
        }
    }

    private static async Task SetRunAndProjectFailedAsync(string connectionString, Guid tenantId, Guid projectId, Guid runId)
    {
        var now = DateTimeOffset.UtcNow;
        using (TenantContext.BeginMaintenanceScope())
        {
            using var context = new AppDbContext(CreateOptions(connectionString));
            await context.Database.ExecuteSqlRawAsync(
                "UPDATE processing_runs SET status = {0}, updated_at = {1} WHERE id = {2}",
                ProcessingRunStatus.Failed.ToString(), now, runId).ConfigureAwait(true);
            await context.Database.ExecuteSqlRawAsync(
                "UPDATE dubbing_projects SET status = {0}, updated_at = {1} WHERE id = {2}",
                ProjectStatus.Failed.ToString(), now, projectId).ConfigureAwait(true);
        }
    }

    private static async Task<long> CountRunsAsync(string connectionString, Guid tenantId, Guid projectId)
    {
        using (TenantContext.BeginScope(tenantId))
        {
            using var context = new AppDbContext(CreateOptions(connectionString));
            return await context.Set<ProcessingRun>().LongCountAsync(r => r.ProjectId == projectId).ConfigureAwait(true);
        }
    }

    private static async Task<string> GetRunConfigHashAsync(string connectionString, Guid tenantId, Guid runId)
    {
        using (TenantContext.BeginScope(tenantId))
        {
            using var context = new AppDbContext(CreateOptions(connectionString));
            var run = await context.Set<ProcessingRun>().AsNoTracking().FirstAsync(r => r.Id == runId).ConfigureAwait(true);
            return run.ConfigurationHash;
        }
    }

    private static async Task<string> GetProjectConfigHashAsync(string connectionString, Guid tenantId, Guid projectId)
    {
        using (TenantContext.BeginScope(tenantId))
        {
            using var context = new AppDbContext(CreateOptions(connectionString));
            var project = await context.Set<DubbingProject>().AsNoTracking().FirstAsync(p => p.Id == projectId).ConfigureAwait(true);
            return project.ConfigurationHash;
        }
    }
}
