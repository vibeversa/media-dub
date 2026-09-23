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

namespace DubbingPlatform.IntegrationTests.Reviews;

/// <summary>
/// Task 011: review context aggregate plus hardened mutations over PG.
/// Skips when Docker is unavailable (CI runs live).
/// Cross-tenant review ids return 404 (no leak); project mismatch is N/A here
/// (flat <c>/api/v1/reviews/{id}</c> routes carry no project segment).
/// </summary>
public sealed class ReviewContextTests
{
    private const string TestSigningKey = "test-signing-key-0123456789abcdef-test-signing-key-01";

    private const string TestAudience = "dubbing-api";

    [SkippableFact]
    public async Task Context_Shape_Single_Call()
    {
        var container = await StartPostgresAsync().ConfigureAwait(true);
        await using (container.ConfigureAwait(true))
        {
            var connectionString = container.GetConnectionString();
            await MigrateAsync(connectionString).ConfigureAwait(true);
            var tenantId = Guid.NewGuid();
            var ownerId = Guid.NewGuid();
            await SeedTenantAsync(connectionString, tenantId).ConfigureAwait(true);

            using var factory = CreateFactory(connectionString);
            using var client = factory.CreateClient();
            UseToken(client, tenantId, ownerId, "TenantAdmin");

            var projectId = await CreateProjectAsync(client).ConfigureAwait(true);
            var projectGuid = ParseProjectId(projectId);
            var seed = await SeedReviewAsync(connectionString, tenantId, projectGuid).ConfigureAwait(true);

            using var response = await client.GetAsync(
                $"/api/v1/reviews/{ToReviewId(seed.ReviewId)}/context").ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(true));
            var root = doc.RootElement;

            var item = root.GetProperty("item");
            Assert.Equal("Segment", item.GetProperty("type").GetString());
            Assert.Equal("high", item.GetProperty("severity").GetString());
            Assert.Equal("Open", item.GetProperty("status").GetString());
            Assert.Equal(0, item.GetProperty("version").GetInt32());

            Assert.Equal(projectId, root.GetProperty("project").GetProperty("id").GetString());

            var run = root.GetProperty("run");
            Assert.Equal("Running", run.GetProperty("status").GetString());
            Assert.False(string.IsNullOrWhiteSpace(run.GetProperty("configHash").GetString()));

            var segment = root.GetProperty("segment");
            Assert.Equal(0, segment.GetProperty("startMs").GetInt32());
            Assert.Equal(2000, segment.GetProperty("endMs").GetInt32());
            Assert.StartsWith("spk_", segment.GetProperty("speakerId").GetString(), StringComparison.Ordinal);

            var versions = root.GetProperty("versions");
            Assert.Single(versions.GetProperty("transcript").EnumerateArray());
            Assert.Single(versions.GetProperty("translation").EnumerateArray());
            Assert.Equal(1, versions.GetProperty("selectionVersion").GetInt32());
            Assert.False(versions.GetProperty("truncated").GetBoolean());

            var voice = root.GetProperty("voice");
            Assert.Equal("stock-es-ctx", voice.GetProperty("voiceId").GetString());
            Assert.Equal("not_required", voice.GetProperty("consentState").GetString());

            var audio = root.GetProperty("audio");
            Assert.True(audio.TryGetProperty("signedUrl", out var signedUrl));
            Assert.Equal(JsonValueKind.Null, signedUrl.ValueKind);

            var sync = root.GetProperty("sync");
            Assert.Equal(300, sync.GetProperty("offsetMs").GetInt32());
            Assert.True(sync.GetProperty("driftFlag").GetBoolean());

            var qc = root.GetProperty("qc");
            Assert.NotEmpty(qc.GetProperty("issues").EnumerateArray().ToList());
            Assert.True(qc.TryGetProperty("evidenceArtifactIds", out _));

            var allowed = root.GetProperty("actions").GetProperty("allowed").EnumerateArray()
                .Select(e => e.GetString()).ToList();
            Assert.Contains("resolve", allowed);
            Assert.Contains("dismiss", allowed);
            Assert.Contains("resolve-with-edit", allowed);

            var permissions = root.GetProperty("permissions");
            Assert.True(permissions.GetProperty("canResolve").GetBoolean());
            Assert.True(permissions.GetProperty("canEdit").GetBoolean());

            Assert.Empty(root.GetProperty("history").EnumerateArray().ToList());
            Assert.False(root.GetProperty("truncated").GetBoolean());
        }
    }

    [SkippableFact]
    public async Task Version_Conflict_409_With_Refresh()
    {
        var container = await StartPostgresAsync().ConfigureAwait(true);
        await using (container.ConfigureAwait(true))
        {
            var connectionString = container.GetConnectionString();
            await MigrateAsync(connectionString).ConfigureAwait(true);
            var tenantId = Guid.NewGuid();
            var ownerId = Guid.NewGuid();
            await SeedTenantAsync(connectionString, tenantId).ConfigureAwait(true);

            using var factory = CreateFactory(connectionString);
            using var client = factory.CreateClient();
            UseToken(client, tenantId, ownerId, "TenantAdmin");

            var projectId = await CreateProjectAsync(client).ConfigureAwait(true);
            var seed = await SeedReviewAsync(connectionString, tenantId, ParseProjectId(projectId)).ConfigureAwait(true);
            var review = ToReviewId(seed.ReviewId);

            using (var first = await SendMutationAsync(client, review, "resolve", 0, "first pass", null, NewKey()).ConfigureAwait(true))
            {
                Assert.Equal(HttpStatusCode.OK, first.StatusCode);
            }

            using (var stale = await SendMutationAsync(client, review, "dismiss", 0, "stale attempt", null, NewKey()).ConfigureAwait(true))
            {
                Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
                var body = JsonDocument.Parse(await stale.Content.ReadAsStringAsync().ConfigureAwait(true));
                var error = body.RootElement.GetProperty("error");
                Assert.Equal("REVIEW_VERSION_CONFLICT", error.GetProperty("code").GetString());
                Assert.Equal(1, error.GetProperty("details").GetProperty("currentVersion").GetInt32());
            }
        }
    }

    [SkippableFact]
    public async Task Idempotent_Replay_No_Second_Transition()
    {
        var container = await StartPostgresAsync().ConfigureAwait(true);
        await using (container.ConfigureAwait(true))
        {
            var connectionString = container.GetConnectionString();
            await MigrateAsync(connectionString).ConfigureAwait(true);
            var tenantId = Guid.NewGuid();
            var ownerId = Guid.NewGuid();
            await SeedTenantAsync(connectionString, tenantId).ConfigureAwait(true);

            using var factory = CreateFactory(connectionString);
            using var client = factory.CreateClient();
            UseToken(client, tenantId, ownerId, "TenantAdmin");

            var projectId = await CreateProjectAsync(client).ConfigureAwait(true);
            var seed = await SeedReviewAsync(connectionString, tenantId, ParseProjectId(projectId)).ConfigureAwait(true);
            var review = ToReviewId(seed.ReviewId);
            var key = NewKey();

            string firstBody;
            using (var first = await SendMutationAsync(client, review, "resolve", 0, "confirm", null, key).ConfigureAwait(true))
            {
                Assert.Equal(HttpStatusCode.OK, first.StatusCode);
                Assert.False(first.Headers.Contains("Idempotent-Replayed"));
                firstBody = await first.Content.ReadAsStringAsync().ConfigureAwait(true);
            }

            Assert.Equal(1, await CountDecisionsAsync(connectionString, tenantId, seed.ReviewId).ConfigureAwait(true));

            using (var replay = await SendMutationAsync(client, review, "resolve", 0, "confirm", null, key).ConfigureAwait(true))
            {
                Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
                Assert.Equal("true", replay.Headers.GetValues("Idempotent-Replayed").Single());
                var replayBody = await replay.Content.ReadAsStringAsync().ConfigureAwait(true);
                Assert.Equal(
                    JsonDocument.Parse(firstBody).RootElement.GetProperty("version").GetInt32(),
                    JsonDocument.Parse(replayBody).RootElement.GetProperty("version").GetInt32());
            }

            Assert.Equal(1, await CountDecisionsAsync(connectionString, tenantId, seed.ReviewId).ConfigureAwait(true));
        }
    }

    [SkippableFact]
    public async Task Reason_Required_400()
    {
        var container = await StartPostgresAsync().ConfigureAwait(true);
        await using (container.ConfigureAwait(true))
        {
            var connectionString = container.GetConnectionString();
            await MigrateAsync(connectionString).ConfigureAwait(true);
            var tenantId = Guid.NewGuid();
            var ownerId = Guid.NewGuid();
            await SeedTenantAsync(connectionString, tenantId).ConfigureAwait(true);

            using var factory = CreateFactory(connectionString);
            using var client = factory.CreateClient();
            UseToken(client, tenantId, ownerId, "TenantAdmin");

            var projectId = await CreateProjectAsync(client).ConfigureAwait(true);
            var seed = await SeedReviewAsync(connectionString, tenantId, ParseProjectId(projectId)).ConfigureAwait(true);
            var review = ToReviewId(seed.ReviewId);

            using (var missing = await SendMutationAsync(client, review, "resolve", 0, null, null, NewKey()).ConfigureAwait(true))
            {
                Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
                var body = JsonDocument.Parse(await missing.Content.ReadAsStringAsync().ConfigureAwait(true));
                Assert.Equal("REVIEW_REASON_REQUIRED", body.RootElement.GetProperty("error").GetProperty("code").GetString());
            }

            using (var blank = await SendMutationAsync(client, review, "resolve", 0, "   ", null, NewKey()).ConfigureAwait(true))
            {
                Assert.Equal(HttpStatusCode.BadRequest, blank.StatusCode);
            }

            Assert.Equal(0, await CountDecisionsAsync(connectionString, tenantId, seed.ReviewId).ConfigureAwait(true));
        }
    }

    [SkippableFact]
    public async Task Resolve_With_Edit_Creates_Version_And_Links()
    {
        var container = await StartPostgresAsync().ConfigureAwait(true);
        await using (container.ConfigureAwait(true))
        {
            var connectionString = container.GetConnectionString();
            await MigrateAsync(connectionString).ConfigureAwait(true);
            var tenantId = Guid.NewGuid();
            var ownerId = Guid.NewGuid();
            await SeedTenantAsync(connectionString, tenantId).ConfigureAwait(true);

            using var factory = CreateFactory(connectionString);
            using var client = factory.CreateClient();
            UseToken(client, tenantId, ownerId, "TenantAdmin");

            var projectId = await CreateProjectAsync(client).ConfigureAwait(true);
            var projectGuid = ParseProjectId(projectId);
            var seed = await SeedReviewAsync(connectionString, tenantId, projectGuid).ConfigureAwait(true);
            var review = ToReviewId(seed.ReviewId);

            string manualVersionId;
            using (var response = await SendMutationAsync(
                client, review, "resolve-with-edit", 0, "fix translation", "hola mundo corregido", NewKey()).ConfigureAwait(true))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(true));
                Assert.Equal("ResolvedWithEdit", doc.RootElement.GetProperty("status").GetString());
                Assert.Equal(1, doc.RootElement.GetProperty("version").GetInt32());
                manualVersionId = doc.RootElement.GetProperty("manualVersionId").GetString()!;
                Assert.False(string.IsNullOrWhiteSpace(manualVersionId));
                Assert.Equal("Translation", doc.RootElement.GetProperty("versionKind").GetString());
            }

            var manualId = Guid.Parse(manualVersionId);
            using (TenantContext.BeginScope(tenantId))
            {
                using var db = new AppDbContext(CreateOptions(connectionString));
                var rows = await db.Set<TranslationVersion>()
                    .AsNoTracking()
                    .Where(v => v.SegmentId == seed.SegmentId)
                    .OrderBy(v => v.CreatedAt)
                    .ToListAsync().ConfigureAwait(true);
                Assert.Equal(2, rows.Count);
                var created = Assert.Single(rows, v => v.Id == manualId);
                Assert.Equal("hola mundo corregido", created.PrimaryText);
                Assert.True(created.IsSelected);
                Assert.Equal("manual", created.Provider);
                Assert.Single(rows, v => v.Id == seed.TranslationV1);

                var reviewRow = await db.Set<ReviewItem>()
                    .AsNoTracking()
                    .FirstAsync(r => r.Id == seed.ReviewId).ConfigureAwait(true);
                Assert.Equal(ReviewStatus.ResolvedWithEdit, reviewRow.Status);
            }

            using (var context = await client.GetAsync(
                $"/api/v1/reviews/{review}/context").ConfigureAwait(true))
            {
                Assert.Equal(HttpStatusCode.OK, context.StatusCode);
                var doc = JsonDocument.Parse(await context.Content.ReadAsStringAsync().ConfigureAwait(true));
                Assert.Equal(2, doc.RootElement.GetProperty("versions").GetProperty("translation").EnumerateArray().Count());
                Assert.Single(doc.RootElement.GetProperty("history").EnumerateArray());
            }
        }
    }

    [SkippableFact]
    public async Task Already_Resolved_409()
    {
        var container = await StartPostgresAsync().ConfigureAwait(true);
        await using (container.ConfigureAwait(true))
        {
            var connectionString = container.GetConnectionString();
            await MigrateAsync(connectionString).ConfigureAwait(true);
            var tenantId = Guid.NewGuid();
            var ownerId = Guid.NewGuid();
            await SeedTenantAsync(connectionString, tenantId).ConfigureAwait(true);

            using var factory = CreateFactory(connectionString);
            using var client = factory.CreateClient();
            UseToken(client, tenantId, ownerId, "TenantAdmin");

            var projectId = await CreateProjectAsync(client).ConfigureAwait(true);
            var seed = await SeedReviewAsync(connectionString, tenantId, ParseProjectId(projectId)).ConfigureAwait(true);
            var review = ToReviewId(seed.ReviewId);

            using (var first = await SendMutationAsync(client, review, "resolve", 0, "done", null, NewKey()).ConfigureAwait(true))
            {
                Assert.Equal(HttpStatusCode.OK, first.StatusCode);
            }

            using (var second = await SendMutationAsync(client, review, "resolve", 1, "again", null, NewKey()).ConfigureAwait(true))
            {
                Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
                var body = JsonDocument.Parse(await second.Content.ReadAsStringAsync().ConfigureAwait(true));
                var error = body.RootElement.GetProperty("error");
                Assert.Equal("REVIEW_ALREADY_RESOLVED", error.GetProperty("code").GetString());
                Assert.Equal("Approved", error.GetProperty("details").GetProperty("currentStatus").GetString());
            }
        }
    }

    [SkippableFact]
    public async Task Reopen_Flow()
    {
        var container = await StartPostgresAsync().ConfigureAwait(true);
        await using (container.ConfigureAwait(true))
        {
            var connectionString = container.GetConnectionString();
            await MigrateAsync(connectionString).ConfigureAwait(true);
            var tenantId = Guid.NewGuid();
            var ownerId = Guid.NewGuid();
            await SeedTenantAsync(connectionString, tenantId).ConfigureAwait(true);

            using var factory = CreateFactory(connectionString);
            using var client = factory.CreateClient();
            UseToken(client, tenantId, ownerId, "TenantAdmin");

            var projectId = await CreateProjectAsync(client).ConfigureAwait(true);
            var seed = await SeedReviewAsync(connectionString, tenantId, ParseProjectId(projectId)).ConfigureAwait(true);
            var review = ToReviewId(seed.ReviewId);

            using (var resolved = await SendMutationAsync(client, review, "dismiss", 0, "not good", null, NewKey()).ConfigureAwait(true))
            {
                Assert.Equal(HttpStatusCode.OK, resolved.StatusCode);
            }

            using (var reopened = await SendMutationAsync(client, review, "reopen", 1, "second look", null, NewKey()).ConfigureAwait(true))
            {
                Assert.Equal(HttpStatusCode.OK, reopened.StatusCode);
                var doc = JsonDocument.Parse(await reopened.Content.ReadAsStringAsync().ConfigureAwait(true));
                Assert.Equal("Open", doc.RootElement.GetProperty("status").GetString());
                Assert.Equal(2, doc.RootElement.GetProperty("version").GetInt32());
            }

            using (var context = await client.GetAsync(
                $"/api/v1/reviews/{review}/context").ConfigureAwait(true))
            {
                var doc = JsonDocument.Parse(await context.Content.ReadAsStringAsync().ConfigureAwait(true));
                var allowed = doc.RootElement.GetProperty("actions").GetProperty("allowed").EnumerateArray()
                    .Select(e => e.GetString()).ToList();
                Assert.Contains("resolve", allowed);
            }

            using (var again = await SendMutationAsync(client, review, "reopen", 2, "noop", null, NewKey()).ConfigureAwait(true))
            {
                Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
                var body = JsonDocument.Parse(await again.Content.ReadAsStringAsync().ConfigureAwait(true));
                Assert.Equal("REVIEW_NOT_RESOLVED", body.RootElement.GetProperty("error").GetProperty("code").GetString());
            }
        }
    }

    [SkippableFact]
    public async Task Allowed_Actions_Honesty()
    {
        var container = await StartPostgresAsync().ConfigureAwait(true);
        await using (container.ConfigureAwait(true))
        {
            var connectionString = container.GetConnectionString();
            await MigrateAsync(connectionString).ConfigureAwait(true);
            var tenantId = Guid.NewGuid();
            var ownerId = Guid.NewGuid();
            await SeedTenantAsync(connectionString, tenantId).ConfigureAwait(true);

            using var factory = CreateFactory(connectionString);
            using var owner = factory.CreateClient();
            UseToken(owner, tenantId, ownerId, "TenantAdmin");
            var projectId = await CreateProjectAsync(owner).ConfigureAwait(true);
            var seed = await SeedReviewAsync(connectionString, tenantId, ParseProjectId(projectId)).ConfigureAwait(true);
            var review = ToReviewId(seed.ReviewId);

            using var viewer = factory.CreateClient();
            UseToken(viewer, tenantId, Guid.NewGuid(), "ProjectViewer");
            using (var context = await viewer.GetAsync(
                $"/api/v1/reviews/{review}/context").ConfigureAwait(true))
            {
                Assert.Equal(HttpStatusCode.OK, context.StatusCode);
                var doc = JsonDocument.Parse(await context.Content.ReadAsStringAsync().ConfigureAwait(true));
                Assert.Empty(doc.RootElement.GetProperty("actions").GetProperty("allowed").EnumerateArray().ToList());
                Assert.False(doc.RootElement.GetProperty("permissions").GetProperty("canResolve").GetBoolean());
            }

            using (var forged = await SendMutationAsync(viewer, review, "resolve", 0, "forged", null, NewKey()).ConfigureAwait(true))
            {
                Assert.Equal(HttpStatusCode.Forbidden, forged.StatusCode);
            }

            using (var resolved = await SendMutationAsync(owner, review, "resolve", 0, "done", null, NewKey()).ConfigureAwait(true))
            {
                Assert.Equal(HttpStatusCode.OK, resolved.StatusCode);
            }

            using (var context = await owner.GetAsync(
                $"/api/v1/reviews/{review}/context").ConfigureAwait(true))
            {
                var doc = JsonDocument.Parse(await context.Content.ReadAsStringAsync().ConfigureAwait(true));
                var allowed = doc.RootElement.GetProperty("actions").GetProperty("allowed").EnumerateArray()
                    .Select(e => e.GetString()).ToList();
                Assert.Single(allowed);
                Assert.Equal("reopen", allowed[0]);
            }
        }
    }

    [SkippableFact]
    public async Task Audit_Written()
    {
        var container = await StartPostgresAsync().ConfigureAwait(true);
        await using (container.ConfigureAwait(true))
        {
            var connectionString = container.GetConnectionString();
            await MigrateAsync(connectionString).ConfigureAwait(true);
            var tenantId = Guid.NewGuid();
            var ownerId = Guid.NewGuid();
            await SeedTenantAsync(connectionString, tenantId).ConfigureAwait(true);

            using var factory = CreateFactory(connectionString);
            using var client = factory.CreateClient();
            UseToken(client, tenantId, ownerId, "TenantAdmin");

            var projectId = await CreateProjectAsync(client).ConfigureAwait(true);
            var projectGuid = ParseProjectId(projectId);
            var seed = await SeedReviewAsync(connectionString, tenantId, projectGuid).ConfigureAwait(true);
            var review = ToReviewId(seed.ReviewId);

            var correlationId = Guid.NewGuid().ToString("N");
            var key = NewKey();
            using (var response = await SendMutationAsync(
                client, review, "resolve", 0, "audit <b>check</b>", null, key, correlationId).ConfigureAwait(true))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            }

            using (TenantContext.BeginScope(tenantId))
            {
                using var db = new AppDbContext(CreateOptions(connectionString));
                var audit = await db.Set<AuditEvent>().AsNoTracking()
                    .Where(a => a.Action == "review.resolve" && a.ProjectId == projectGuid)
                    .OrderByDescending(a => a.CreatedAt)
                    .FirstOrDefaultAsync().ConfigureAwait(true);
                Assert.NotNull(audit);
                Assert.Equal(ownerId.ToString("D"), audit!.Actor);
                Assert.Contains("audit check", audit.DetailsJson!, StringComparison.Ordinal);
                Assert.DoesNotContain("<b>", audit.DetailsJson!, StringComparison.Ordinal);
                Assert.Contains(key, audit.DetailsJson!, StringComparison.Ordinal);
                Assert.Contains(correlationId, audit.DetailsJson!, StringComparison.Ordinal);
                Assert.Contains("Approved", audit.DetailsJson!, StringComparison.Ordinal);
            }
        }
    }

    [SkippableFact]
    public async Task Cross_Tenant_404()
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
            UseToken(clientA, tenantA, Guid.NewGuid(), "TenantAdmin");
            var projectId = await CreateProjectAsync(clientA).ConfigureAwait(true);
            var seed = await SeedReviewAsync(connectionString, tenantA, ParseProjectId(projectId)).ConfigureAwait(true);
            var review = ToReviewId(seed.ReviewId);

            using var clientB = factory.CreateClient();
            UseToken(clientB, tenantB, Guid.NewGuid(), "TenantAdmin");

            using (var context = await clientB.GetAsync(
                $"/api/v1/reviews/{review}/context").ConfigureAwait(true))
            {
                Assert.Equal(HttpStatusCode.NotFound, context.StatusCode);
                var body = JsonDocument.Parse(await context.Content.ReadAsStringAsync().ConfigureAwait(true));
                Assert.Equal("NOT_FOUND", body.RootElement.GetProperty("error").GetProperty("code").GetString());
            }

            using (var mutation = await SendMutationAsync(clientB, review, "resolve", 0, "cross", null, NewKey()).ConfigureAwait(true))
            {
                Assert.Equal(HttpStatusCode.NotFound, mutation.StatusCode);
            }
        }
    }

    private sealed record SeedData(
        Guid RunId,
        Guid SegmentId,
        Guid SpeakerId,
        Guid ReviewId,
        Guid TranscriptV1,
        Guid TranslationV1);

    private static string ToReviewId(Guid id)
    {
        return string.Concat("rev_", id.ToString("N"));
    }

    private static Guid ParseProjectId(string publicId)
    {
        var hex = publicId.StartsWith("prj_", StringComparison.Ordinal)
            ? publicId.Substring("prj_".Length)
            : publicId;
        return Guid.ParseExact(hex, "N");
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
            Content = JsonContent.Create(new { name = $"R-{Guid.NewGuid():N}", sourceLanguage = "en", targetLanguage = "es" }),
        };
        request.Headers.Add("Idempotency-Key", NewKey());
        using var response = await client.SendAsync(request).ConfigureAwait(true);
        var payload = await response.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"Create failed: {(int)response.StatusCode} {payload}");
        using var document = JsonDocument.Parse(payload);
        return document.RootElement.GetProperty("id").GetString()!;
    }

    private static async Task<HttpResponseMessage> SendMutationAsync(
        HttpClient client,
        string reviewId,
        string action,
        int expectedVersion,
        string? reason,
        string? editText,
        string key,
        string? correlationId = null)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"/api/v1/reviews/{reviewId}/{action}")
        {
            Content = JsonContent.Create(new { expectedVersion, reason, editText }),
        };
        request.Headers.Add("Idempotency-Key", key);
        if (!string.IsNullOrWhiteSpace(correlationId))
        {
            request.Headers.Add("X-Correlation-Id", correlationId);
        }

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

    private static async Task<SeedData> SeedReviewAsync(
        string connectionString, Guid tenantId, Guid projectId)
    {
        var now = DateTimeOffset.UtcNow;
        var runId = Guid.NewGuid();
        var speakerId = Guid.NewGuid();
        var segmentId = Guid.NewGuid();
        var reviewId = Guid.NewGuid();
        var transcriptV1 = Guid.NewGuid();
        var translationV1 = Guid.NewGuid();

        using (TenantContext.BeginScope(tenantId))
        {
            using var context = new AppDbContext(CreateOptions(connectionString));
            context.Set<ProcessingRun>().Add(new ProcessingRun(
                runId, tenantId, projectId, 0, ProcessingRunStatus.Running, "1.0.0",
                new string('a', 64), new string('b', 64), new string('c', 64),
                now, now, now, null));

            context.Set<Speaker>().Add(new Speaker(
                speakerId, tenantId, projectId, "spk-a", "Speaker A", 0, 2000,
                "diarization", "v1", 0.9, null, now));

            context.Set<SpeechSegment>().Add(new SpeechSegment(
                segmentId, tenantId, projectId, runId, 0, 0, 2000, "Pending", speakerId, now));

            context.Set<TranscriptVersion>().Add(new TranscriptVersion(
                transcriptV1, tenantId, projectId, runId, segmentId,
                "mock", "m1", "en", "hello world", 0.9, null, true, false, now));
            context.Set<TranslationVersion>().Add(new TranslationVersion(
                translationV1, tenantId, projectId, runId, segmentId,
                "hola mundo", [], 0.9, 0.9, 0.9, "mock", "m1", null, null, true, now));
            context.Set<SegmentSelection>().Add(new SegmentSelection(
                Guid.NewGuid(), tenantId, projectId, segmentId,
                transcriptV1, translationV1, null, 1, now, tenantId));

            context.Set<ReviewItem>().Add(new ReviewItem(
                reviewId, tenantId, projectId, runId, ScopeType.Segment, segmentId.ToString("N"), segmentId,
                ReviewStatus.Open, "TRANSLATION_QUALITY", null, now, now, null));

            context.Set<QualityResult>().Add(new QualityResult(
                Guid.NewGuid(), tenantId, projectId, runId, ScopeType.Segment, segmentId.ToString("N"), segmentId,
                QualityStatus.Blocked, "QC_BLOCKED", "high", "blocked for review", null, null, now));

            context.Set<SyncResult>().Add(new SyncResult(
                Guid.NewGuid(), tenantId, projectId, runId, segmentId,
                0.9, SyncStatus.SyncRetryable, 2000, 2300, 0.15, 1.15, now));

            var voiceId = Guid.NewGuid();
            context.Set<VoiceProfile>().Add(new VoiceProfile(
                voiceId, tenantId, "mock", "stock-es-ctx", "1", "es", VoiceType.Stock, false, null, now));
            context.Set<SpeakerVoiceAssignment>().Add(new SpeakerVoiceAssignment(
                Guid.NewGuid(), tenantId, projectId, runId, speakerId, voiceId, "seed", "seed", now));

            context.Set<VoicePreviewJob>().Add(new VoicePreviewJob(
                Guid.NewGuid(), tenantId, projectId, speakerId, "stock-es-ctx", "preview text",
                VoicePreviewStatus.Pending, tenantId, NewKey(), VoicePreviewQuotaCheck.Allowed, null,
                VoicePreviewConsentState.Verified, null, null, null, null, now, null, null));

            await context.SaveChangesAsync().ConfigureAwait(true);
        }

        return new SeedData(runId, segmentId, speakerId, reviewId, transcriptV1, translationV1);
    }

    private static async Task<int> CountDecisionsAsync(
        string connectionString, Guid tenantId, Guid reviewId)
    {
        using (TenantContext.BeginScope(tenantId))
        {
            using var context = new AppDbContext(CreateOptions(connectionString));
            return await context.Set<ReviewDecision>()
                .CountAsync(d => d.ReviewItemId == reviewId).ConfigureAwait(true);
        }
    }
}
