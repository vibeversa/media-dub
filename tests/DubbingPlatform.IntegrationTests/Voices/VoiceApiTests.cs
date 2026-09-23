using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using DubbingPlatform.Api.Middleware;
using DubbingPlatform.Application.MultiTenancy;
using DubbingPlatform.Application.Voices;
using DubbingPlatform.Contracts.Messages;
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

namespace DubbingPlatform.IntegrationTests.Voices;

/// <summary>
/// Task 010: speakers, voice assignment, and voice previews over PG.
/// Skips when Docker is unavailable (CI runs live).
/// Cross-tenant speaker/voice/preview ids return 404 (no leak); project
/// mismatch stays 403.
/// </summary>
public sealed class VoiceApiTests
{
    private const string TestSigningKey = "test-signing-key-0123456789abcdef-test-signing-key-01";

    private const string TestAudience = "dubbing-api";

    [SkippableFact]
    public async Task List_And_Detail_Include_Counts_And_AssignedVoice()
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
            var seed = await SeedVoicesAsync(connectionString, tenantId, projectGuid).ConfigureAwait(true);

            using (var list = await client.GetAsync(
                $"/api/v1/projects/{projectId}/speakers").ConfigureAwait(true))
            {
                Assert.Equal(HttpStatusCode.OK, list.StatusCode);
                var doc = JsonDocument.Parse(await list.Content.ReadAsStringAsync().ConfigureAwait(true));
                var items = doc.RootElement.GetProperty("items");
                Assert.Equal(3, items.GetArrayLength());
                var first = items.EnumerateArray()
                    .First(e => e.GetProperty("speakerKey").GetString() == "spk-a");
                Assert.Equal(2, first.GetProperty("segmentCount").GetInt32());
            }

            using (var detail = await client.GetAsync(
                $"/api/v1/projects/{projectId}/speakers/{ToSpeakerId(seed.SpeakerA)}").ConfigureAwait(true))
            {
                Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
                var doc = JsonDocument.Parse(await detail.Content.ReadAsStringAsync().ConfigureAwait(true));
                Assert.Equal("spk-a", doc.RootElement.GetProperty("speakerKey").GetString());
                Assert.Equal(2, doc.RootElement.GetProperty("segmentCount").GetInt32());
            }
        }
    }

    [SkippableFact]
    public async Task AvailableVoices_CompatibleOnly_With_Reasons()
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
            var seed = await SeedVoicesAsync(connectionString, tenantId, projectGuid).ConfigureAwait(true);

            using var response = await client.GetAsync(
                $"/api/v1/projects/{projectId}/speakers/{ToSpeakerId(seed.SpeakerA)}/available-voices").ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(true));
            var voices = doc.RootElement.GetProperty("voices").EnumerateArray()
                .Select(e => e.GetProperty("voiceId").GetString()).ToList();
            Assert.Contains("stock-es-1", voices);
            Assert.DoesNotContain("stock-fr-1", voices);
            Assert.DoesNotContain("cloned-noconsent-1", voices);
            var excludedCount = doc.RootElement.GetProperty("excludedCount").GetInt32();
            Assert.True(excludedCount >= 3);
            foreach (var excluded in doc.RootElement.GetProperty("excluded").EnumerateArray())
            {
                Assert.NotEmpty(excluded.GetProperty("reasons").EnumerateArray().ToList());
            }
        }
    }

    [SkippableFact]
    public async Task Incompatible_Assign_422()
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
            var seed = await SeedVoicesAsync(connectionString, tenantId, projectGuid).ConfigureAwait(true);

            using var response = await SendAssignAsync(
                client, projectId, ToSpeakerId(seed.SpeakerA), "stock-fr-1", "wrong lang").ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(true));
            var error = body.RootElement.GetProperty("error");
            Assert.Equal("VOICE_INCOMPATIBLE", error.GetProperty("code").GetString());
        }
    }

    [SkippableFact]
    public async Task Consent_Block_403()
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
            var seed = await SeedVoicesAsync(connectionString, tenantId, projectGuid).ConfigureAwait(true);

            using var response = await SendAssignAsync(
                client, projectId, ToSpeakerId(seed.SpeakerA), "cloned-noconsent-1", null).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(true));
            Assert.Equal("VOICE_CONSENT_REQUIRED", body.RootElement.GetProperty("error").GetProperty("code").GetString());

            // Preview on the same consent-less cloned voice is also blocked.
            using var preview = await SendPreviewAsync(
                client, projectId, ToSpeakerId(seed.SpeakerA), "cloned-noconsent-1", "hello", NewKey()).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.Forbidden, preview.StatusCode);
        }
    }

    [SkippableFact]
    public async Task Stable_SingleVoice_Replaces()
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
            var seed = await SeedVoicesAsync(connectionString, tenantId, projectGuid).ConfigureAwait(true);

            using (var first = await SendAssignAsync(
                client, projectId, ToSpeakerId(seed.SpeakerA), "stock-es-1", "first").ConfigureAwait(true))
            {
                Assert.Equal(HttpStatusCode.OK, first.StatusCode);
            }

            using (var second = await SendAssignAsync(
                client, projectId, ToSpeakerId(seed.SpeakerA), "stock-es-2", "second").ConfigureAwait(true))
            {
                Assert.Equal(HttpStatusCode.OK, second.StatusCode);
                var doc = JsonDocument.Parse(await second.Content.ReadAsStringAsync().ConfigureAwait(true));
                Assert.True(doc.RootElement.GetProperty("changed").GetBoolean());
                Assert.Equal("stock-es-2", doc.RootElement.GetProperty("voiceId").GetString());
            }

            var count = await CountAssignmentsAsync(connectionString, tenantId, projectGuid, seed.SpeakerA).ConfigureAwait(true);
            Assert.Equal(1, count);
            var current = await CurrentVoiceAsync(connectionString, tenantId, projectGuid, seed.SpeakerA).ConfigureAwait(true);
            Assert.Equal("stock-es-2", current);
        }
    }

    [SkippableFact]
    public async Task NoOp_SameVoice_Changed_False()
    {
        var container = await StartPostgresAsync().ConfigureAwait(true);
        await using (container.ConfigureAwait(true))
        {
            var connectionString = container.GetConnectionString();
            await MigrateAsync(connectionString).ConfigureAwait(true);
            var tenantId = Guid.NewGuid();
            var ownerId = Guid.NewGuid();
            await SeedTenantAsync(connectionString, tenantId).ConfigureAwait(true);

            RecordingPublisher.Clear();
            using var factory = CreateFactory(connectionString, useRecordingPublisher: true);
            using var client = factory.CreateClient();
            UseToken(client, tenantId, ownerId, "TenantAdmin");

            var projectId = await CreateProjectAsync(client).ConfigureAwait(true);
            var projectGuid = ParseProjectId(projectId);
            var seed = await SeedVoicesAsync(connectionString, tenantId, projectGuid).ConfigureAwait(true);

            using (var first = await SendAssignAsync(
                client, projectId, ToSpeakerId(seed.SpeakerA), "stock-es-1", null).ConfigureAwait(true))
            {
                Assert.Equal(HttpStatusCode.OK, first.StatusCode);
            }

            Assert.Single(RecordingPublisher.Messages);

            using (var noop = await SendAssignAsync(
                client, projectId, ToSpeakerId(seed.SpeakerA), "stock-es-1", null).ConfigureAwait(true))
            {
                Assert.Equal(HttpStatusCode.OK, noop.StatusCode);
                var doc = JsonDocument.Parse(await noop.Content.ReadAsStringAsync().ConfigureAwait(true));
                Assert.False(doc.RootElement.GetProperty("changed").GetBoolean());
            }

            Assert.Single(RecordingPublisher.Messages);
        }
    }

    [SkippableFact]
    public async Task Change_Invalidation_StaleFlag_Audit()
    {
        var container = await StartPostgresAsync().ConfigureAwait(true);
        await using (container.ConfigureAwait(true))
        {
            var connectionString = container.GetConnectionString();
            await MigrateAsync(connectionString).ConfigureAwait(true);
            var tenantId = Guid.NewGuid();
            var ownerId = Guid.NewGuid();
            await SeedTenantAsync(connectionString, tenantId).ConfigureAwait(true);

            RecordingPublisher.Clear();
            using var factory = CreateFactory(connectionString, useRecordingPublisher: true);
            using var client = factory.CreateClient();
            UseToken(client, tenantId, ownerId, "TenantAdmin");

            var projectId = await CreateProjectAsync(client).ConfigureAwait(true);
            var projectGuid = ParseProjectId(projectId);
            var seed = await SeedVoicesAsync(connectionString, tenantId, projectGuid).ConfigureAwait(true);
            await SeedOutputAsync(connectionString, tenantId, projectGuid, seed.RunId).ConfigureAwait(true);

            var correlationId = Guid.NewGuid().ToString("N");
            using (var request = new HttpRequestMessage(
                HttpMethod.Put,
                $"/api/v1/projects/{projectId}/speakers/{ToSpeakerId(seed.SpeakerA)}/voice-assignment")
            {
                Content = JsonContent.Create(new { voiceId = "stock-es-1", reason = "prefer <b>warm</b>" }),
            })
            {
                request.Headers.Add("X-Correlation-Id", correlationId);
                using var response = await client.SendAsync(request).ConfigureAwait(true);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(true));
                Assert.True(doc.RootElement.GetProperty("outputStale").GetBoolean());
                Assert.Equal("OUTPUT_STALE", doc.RootElement.GetProperty("warningCode").GetString());
            }

            Assert.Single(RecordingPublisher.Messages);
            var published = RecordingPublisher.Messages[0];
            Assert.Equal(seed.SpeakerA, published.SpeakerId);
            Assert.True(published.OutputStale);

            var audit = await GetLatestVoiceAuditAsync(connectionString, tenantId, projectGuid).ConfigureAwait(true);
            Assert.NotNull(audit);
            Assert.Equal(ownerId.ToString("D"), audit!.Actor);
            Assert.Contains(correlationId, audit.DetailsJson!, StringComparison.Ordinal);
            Assert.Contains("stock-es-1", audit.DetailsJson!, StringComparison.Ordinal);
            Assert.DoesNotContain("<b>", audit.DetailsJson!, StringComparison.Ordinal);
        }
    }

    [SkippableFact]
    public async Task Preview_Create_Get_Idempotency()
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
            var seed = await SeedVoicesAsync(connectionString, tenantId, projectGuid).ConfigureAwait(true);

            var key = NewKey();
            string previewId;
            using (var created = await SendPreviewAsync(
                client, projectId, ToSpeakerId(seed.SpeakerA), "stock-es-1", "hello preview", key).ConfigureAwait(true))
            {
                Assert.Equal(HttpStatusCode.Accepted, created.StatusCode);
                var doc = JsonDocument.Parse(await created.Content.ReadAsStringAsync().ConfigureAwait(true));
                previewId = doc.RootElement.GetProperty("previewId").GetString()!;
                Assert.StartsWith("vpv_", previewId, StringComparison.Ordinal);
                Assert.False(doc.RootElement.GetProperty("isDuplicate").GetBoolean());
            }

            using (var replay = await SendPreviewAsync(
                client, projectId, ToSpeakerId(seed.SpeakerA), "stock-es-1", "hello preview", key).ConfigureAwait(true))
            {
                Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
                var doc = JsonDocument.Parse(await replay.Content.ReadAsStringAsync().ConfigureAwait(true));
                Assert.Equal(previewId, doc.RootElement.GetProperty("previewId").GetString());
                Assert.True(doc.RootElement.GetProperty("isDuplicate").GetBoolean());
            }

            using (var list = await client.GetAsync(
                $"/api/v1/projects/{projectId}/voice-previews").ConfigureAwait(true))
            {
                Assert.Equal(HttpStatusCode.OK, list.StatusCode);
                var doc = JsonDocument.Parse(await list.Content.ReadAsStringAsync().ConfigureAwait(true));
                Assert.True(doc.RootElement.GetProperty("total").GetInt64() >= 1);
            }

            using (var detail = await client.GetAsync(
                $"/api/v1/projects/{projectId}/voice-previews/{previewId}").ConfigureAwait(true))
            {
                Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
                var payload = await detail.Content.ReadAsStringAsync().ConfigureAwait(true);
                var doc = JsonDocument.Parse(payload);
                Assert.Equal("Completed", doc.RootElement.GetProperty("status").GetString());
                Assert.True(doc.RootElement.TryGetProperty("downloadUrl", out _));
                Assert.DoesNotContain("storageKey", payload, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("internalPath", payload, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [SkippableFact]
    public async Task Preview_Quota_And_Text_Errors()
    {
        var container = await StartPostgresAsync().ConfigureAwait(true);
        await using (container.ConfigureAwait(true))
        {
            var connectionString = container.GetConnectionString();
            await MigrateAsync(connectionString).ConfigureAwait(true);
            var tenantId = Guid.NewGuid();
            var ownerId = Guid.NewGuid();
            await SeedTenantAsync(connectionString, tenantId).ConfigureAwait(true);

            var overrides = new Dictionary<string, string?>
            {
                ["Preview:MaxPreviewsPerMinutePerTenant"] = "1",
                ["Preview:MaxPreviewsPerDayPerTenant"] = "1000",
            };
            using var factory = CreateFactory(connectionString, extraConfig: overrides);
            using var client = factory.CreateClient();
            UseToken(client, tenantId, ownerId, "TenantAdmin");

            var projectId = await CreateProjectAsync(client).ConfigureAwait(true);
            var projectGuid = ParseProjectId(projectId);
            var seed = await SeedVoicesAsync(connectionString, tenantId, projectGuid).ConfigureAwait(true);

            using (var first = await SendPreviewAsync(
                client, projectId, ToSpeakerId(seed.SpeakerA), "stock-es-1", "first", NewKey()).ConfigureAwait(true))
            {
                Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
            }

            using (var quota = await SendPreviewAsync(
                client, projectId, ToSpeakerId(seed.SpeakerA), "stock-es-1", "second", NewKey()).ConfigureAwait(true))
            {
                Assert.Equal(HttpStatusCode.TooManyRequests, quota.StatusCode);
                var body = JsonDocument.Parse(await quota.Content.ReadAsStringAsync().ConfigureAwait(true));
                Assert.Equal("PREVIEW_QUOTA_EXCEEDED", body.RootElement.GetProperty("error").GetProperty("code").GetString());
            }
        }

        var container2 = await StartPostgresAsync().ConfigureAwait(true);
        await using (container2.ConfigureAwait(true))
        {
            var connectionString = container2.GetConnectionString();
            await MigrateAsync(connectionString).ConfigureAwait(true);
            var tenantId = Guid.NewGuid();
            var ownerId = Guid.NewGuid();
            await SeedTenantAsync(connectionString, tenantId).ConfigureAwait(true);

            using var factory = CreateFactory(connectionString);
            using var client = factory.CreateClient();
            UseToken(client, tenantId, ownerId, "TenantAdmin");

            var projectId = await CreateProjectAsync(client).ConfigureAwait(true);
            var projectGuid = ParseProjectId(projectId);
            var seed = await SeedVoicesAsync(connectionString, tenantId, projectGuid).ConfigureAwait(true);
            var speaker = ToSpeakerId(seed.SpeakerA);

            using (var empty = await SendPreviewAsync(client, projectId, speaker, "stock-es-1", "   ", NewKey()).ConfigureAwait(true))
            {
                Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
                var body = JsonDocument.Parse(await empty.Content.ReadAsStringAsync().ConfigureAwait(true));
                Assert.Equal("PREVIEW_TEXT_INVALID", body.RootElement.GetProperty("error").GetProperty("code").GetString());
            }

            using (var overlong = await SendPreviewAsync(client, projectId, speaker, "stock-es-1", new string('x', 501), NewKey()).ConfigureAwait(true))
            {
                Assert.Equal(HttpStatusCode.BadRequest, overlong.StatusCode);
                var body = JsonDocument.Parse(await overlong.Content.ReadAsStringAsync().ConfigureAwait(true));
                Assert.Equal("PREVIEW_TEXT_INVALID", body.RootElement.GetProperty("error").GetProperty("code").GetString());
            }

            using (var unknown = await SendAssignAsync(client, projectId, speaker, "no-such-voice", null).ConfigureAwait(true))
            {
                Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
                var body = JsonDocument.Parse(await unknown.Content.ReadAsStringAsync().ConfigureAwait(true));
                Assert.Equal("VOICE_NOT_FOUND", body.RootElement.GetProperty("error").GetProperty("code").GetString());
            }
        }
    }

    [SkippableFact]
    public async Task Authz_Matrix()
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
            var projectGuid = ParseProjectId(projectId);
            var seed = await SeedVoicesAsync(connectionString, tenantId, projectGuid).ConfigureAwait(true);
            var speaker = ToSpeakerId(seed.SpeakerA);

            using var viewer = factory.CreateClient();
            UseToken(viewer, tenantId, Guid.NewGuid(), "ProjectViewer");
            using (var list = await viewer.GetAsync($"/api/v1/projects/{projectId}/speakers").ConfigureAwait(true))
            {
                Assert.Equal(HttpStatusCode.OK, list.StatusCode);
            }

            using (var denied = await SendAssignAsync(viewer, projectId, speaker, "stock-es-1", null).ConfigureAwait(true))
            {
                Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
            }

            using (var denied = await SendPreviewAsync(viewer, projectId, speaker, "stock-es-1", "hi", NewKey()).ConfigureAwait(true))
            {
                Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
            }

            var editorId = Guid.NewGuid();
            await SeedMembershipAsync(connectionString, tenantId, projectGuid, editorId).ConfigureAwait(true);
            using var editor = factory.CreateClient();
            UseToken(editor, tenantId, editorId, "ProjectEditor");
            using (var allowed = await SendAssignAsync(editor, projectId, speaker, "stock-es-1", null).ConfigureAwait(true))
            {
                Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
            }

            using var anon = factory.CreateClient();
            using (var unauth = await anon.GetAsync($"/api/v1/projects/{projectId}/speakers").ConfigureAwait(true))
            {
                Assert.Equal(HttpStatusCode.Unauthorized, unauth.StatusCode);
            }
        }
    }

    [SkippableFact]
    public async Task Cross_Tenant_Ids_404()
    {
        var container = await StartPostgresAsync().ConfigureAwait(true);
        await using (container.ConfigureAwait(true))
        {
            var connectionString = container.GetConnectionString();
            await MigrateAsync(connectionString).ConfigureAwait(true);
            var tenantA = Guid.NewGuid();
            var tenantB = Guid.NewGuid();
            var ownerA = Guid.NewGuid();
            await SeedTenantAsync(connectionString, tenantA).ConfigureAwait(true);
            await SeedTenantAsync(connectionString, tenantB).ConfigureAwait(true);

            using var factory = CreateFactory(connectionString);
            using var clientA = factory.CreateClient();
            UseToken(clientA, tenantA, ownerA, "TenantAdmin");
            var projectId = await CreateProjectAsync(clientA).ConfigureAwait(true);
            var projectGuid = ParseProjectId(projectId);
            var seed = await SeedVoicesAsync(connectionString, tenantA, projectGuid).ConfigureAwait(true);

            using var clientB = factory.CreateClient();
            UseToken(clientB, tenantB, Guid.NewGuid(), "TenantAdmin");

            using (var project = await clientB.GetAsync(
                $"/api/v1/projects/{projectId}/speakers/{ToSpeakerId(seed.SpeakerA)}").ConfigureAwait(true))
            {
                Assert.Equal(HttpStatusCode.Forbidden, project.StatusCode);
            }

            var ownerB = Guid.NewGuid();
            using var clientB2 = factory.CreateClient();
            UseToken(clientB2, tenantB, ownerB, "TenantAdmin");
            var projectB = await CreateProjectAsync(clientB2).ConfigureAwait(true);
            using (var cross = await clientB2.GetAsync(
                $"/api/v1/projects/{projectB}/speakers/{ToSpeakerId(seed.SpeakerA)}").ConfigureAwait(true))
            {
                Assert.Equal(HttpStatusCode.NotFound, cross.StatusCode);
            }
        }
    }

    private sealed record SeedData(
        Guid RunId,
        Guid SpeakerA,
        Guid SpeakerB,
        Guid SpeakerUnused);

    private static string ToSpeakerId(Guid id)
    {
        return string.Concat("spk_", id.ToString("N"));
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

    private static WebApplicationFactory<CorrelationIdMiddleware> CreateFactory(
        string connectionString,
        bool useRecordingPublisher = false,
        IReadOnlyDictionary<string, string?>? extraConfig = null)
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
                if (extraConfig is not null)
                {
                    foreach (var entry in extraConfig)
                    {
                        values[entry.Key] = entry.Value;
                    }
                }

                config.AddInMemoryCollection(values);
            });

            if (useRecordingPublisher)
            {
                builder.ConfigureServices(services =>
                {
                    services.AddSingleton<RecordingPublisher>();
                    services.AddScoped<ISpeakerVoiceEventPublisher>(
                        sp => sp.GetRequiredService<RecordingPublisher>());
                });
            }
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
            Content = JsonContent.Create(new { name = $"V-{Guid.NewGuid():N}", sourceLanguage = "en", targetLanguage = "es" }),
        };
        request.Headers.Add("Idempotency-Key", NewKey());
        using var response = await client.SendAsync(request).ConfigureAwait(true);
        var payload = await response.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"Create failed: {(int)response.StatusCode} {payload}");
        using var document = JsonDocument.Parse(payload);
        return document.RootElement.GetProperty("id").GetString()!;
    }

    private static async Task<HttpResponseMessage> SendAssignAsync(
        HttpClient client, string projectId, string speakerId, string voiceId, string? reason)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Put, $"/api/v1/projects/{projectId}/speakers/{speakerId}/voice-assignment")
        {
            Content = JsonContent.Create(new { voiceId, reason }),
        };
        return await client.SendAsync(request).ConfigureAwait(true);
    }

    private static async Task<HttpResponseMessage> SendPreviewAsync(
        HttpClient client, string projectId, string speakerId, string voiceId, string text, string key)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"/api/v1/projects/{projectId}/voice-previews")
        {
            Content = JsonContent.Create(new { speakerId, voiceId, text }),
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

    private static async Task SeedMembershipAsync(string connectionString, Guid tenantId, Guid projectId, Guid userId)
    {
        var now = DateTimeOffset.UtcNow;
        using (TenantContext.BeginScope(tenantId))
        {
            using var context = new AppDbContext(CreateOptions(connectionString));
            context.Set<TenantUser>().Add(new TenantUser(
                userId, tenantId, string.Concat("sub-", userId.ToString("N")),
                string.Concat("editor-", userId.ToString("N"), "@example.com"), "Editor",
                TenantUserStatus.Active, now, now));
            context.Set<ProjectMembership>().Add(new ProjectMembership(
                Guid.NewGuid(), tenantId, projectId, userId, ProjectRole.ProjectEditor, userId, now));
            await context.SaveChangesAsync().ConfigureAwait(true);
        }
    }

    private static async Task<SeedData> SeedVoicesAsync(
        string connectionString, Guid tenantId, Guid projectId)
    {
        var now = DateTimeOffset.UtcNow;
        var runId = Guid.NewGuid();
        var speakerA = Guid.NewGuid();
        var speakerB = Guid.NewGuid();
        var speakerUnused = Guid.NewGuid();

        using (TenantContext.BeginScope(tenantId))
        {
            using var context = new AppDbContext(CreateOptions(connectionString));
            context.Set<ProcessingRun>().Add(new ProcessingRun(
                runId, tenantId, projectId, 0, ProcessingRunStatus.Running, "1.0.0",
                new string('a', 64), new string('b', 64), new string('c', 64),
                now, now, now, null));

            context.Set<Speaker>().Add(new Speaker(
                speakerA, tenantId, projectId, "spk-a", "Speaker A", 0, 2000,
                "diarization", "v1", 0.9, null, now));
            context.Set<Speaker>().Add(new Speaker(
                speakerB, tenantId, projectId, "spk-b", "Speaker B", 0, 1000,
                "diarization", "v1", 0.8, null, now));
            context.Set<Speaker>().Add(new Speaker(
                speakerUnused, tenantId, projectId, "spk-u", "Unused", 0, 0,
                "diarization", "v1", 0.7, null, now));

            context.Set<SpeechSegment>().Add(new SpeechSegment(
                Guid.NewGuid(), tenantId, projectId, runId, 0, 0, 1000, "Pending", speakerA, now));
            context.Set<SpeechSegment>().Add(new SpeechSegment(
                Guid.NewGuid(), tenantId, projectId, runId, 1, 1000, 2000, "Pending", speakerA, now));
            context.Set<SpeechSegment>().Add(new SpeechSegment(
                Guid.NewGuid(), tenantId, projectId, runId, 2, 2000, 3000, "Pending", speakerB, now));

            context.Set<VoiceProfile>().Add(new VoiceProfile(
                Guid.NewGuid(), tenantId, "mock", "stock-es-1", "1", "es", VoiceType.Stock, false, null, now));
            context.Set<VoiceProfile>().Add(new VoiceProfile(
                Guid.NewGuid(), tenantId, "mock", "stock-es-2", "1", "es", VoiceType.Stock, false, null, now));
            context.Set<VoiceProfile>().Add(new VoiceProfile(
                Guid.NewGuid(), tenantId, "mock", "stock-fr-1", "1", "fr", VoiceType.Stock, false, null, now));
            context.Set<VoiceProfile>().Add(new VoiceProfile(
                Guid.NewGuid(), tenantId, "mock", "lowrate-es-1", "1", "es", VoiceType.Stock, false,
                """{"sampleRateHz":8000,"channels":1}""", now));
            var clonedConsentedId = Guid.NewGuid();
            context.Set<VoiceProfile>().Add(new VoiceProfile(
                clonedConsentedId, tenantId, "mock", "cloned-consented-1", "1", "es", VoiceType.Cloned, true, null, now));
            context.Set<VoiceProfile>().Add(new VoiceProfile(
                Guid.NewGuid(), tenantId, "mock", "cloned-noconsent-1", "1", "es", VoiceType.Cloned, true, null, now));

            context.Set<ConsentRecord>().Add(new ConsentRecord(
                Guid.NewGuid(), tenantId, "subject-1", "evidence-1", "*",
                "EU", ConsentStatus.Granted, clonedConsentedId, now, null));

            await context.SaveChangesAsync().ConfigureAwait(true);
        }

        return new SeedData(runId, speakerA, speakerB, speakerUnused);
    }

    private static async Task SeedOutputAsync(string connectionString, Guid tenantId, Guid projectId, Guid runId)
    {
        var now = DateTimeOffset.UtcNow;
        using (TenantContext.BeginScope(tenantId))
        {
            using var context = new AppDbContext(CreateOptions(connectionString));
            context.Set<OutputAsset>().Add(new OutputAsset(
                Guid.NewGuid(), tenantId, projectId, runId, Guid.NewGuid(), "Video", 3000, "mp4", now));
            await context.SaveChangesAsync().ConfigureAwait(true);
        }
    }

    private static async Task<int> CountAssignmentsAsync(
        string connectionString, Guid tenantId, Guid projectId, Guid speakerId)
    {
        using (TenantContext.BeginScope(tenantId))
        {
            using var context = new AppDbContext(CreateOptions(connectionString));
            return await context.Set<SpeakerVoiceAssignment>()
                .CountAsync(a => a.ProjectId == projectId && a.SpeakerId == speakerId).ConfigureAwait(true);
        }
    }

    private static async Task<string?> CurrentVoiceAsync(
        string connectionString, Guid tenantId, Guid projectId, Guid speakerId)
    {
        using (TenantContext.BeginScope(tenantId))
        {
            using var context = new AppDbContext(CreateOptions(connectionString));
            var assignment = await context.Set<SpeakerVoiceAssignment>()
                .AsNoTracking()
                .Where(a => a.ProjectId == projectId && a.SpeakerId == speakerId)
                .OrderByDescending(a => a.CreatedAt)
                .FirstOrDefaultAsync().ConfigureAwait(true);
            if (assignment is null)
            {
                return null;
            }

            var voice = await context.Set<VoiceProfile>()
                .AsNoTracking()
                .FirstOrDefaultAsync(v => v.Id == assignment.VoiceProfileId).ConfigureAwait(true);
            return voice?.VoiceId;
        }
    }

    private static async Task<AuditEvent?> GetLatestVoiceAuditAsync(
        string connectionString, Guid tenantId, Guid projectId)
    {
        using (TenantContext.BeginScope(tenantId))
        {
            using var context = new AppDbContext(CreateOptions(connectionString));
            return await context.Set<AuditEvent>().AsNoTracking()
                .Where(a => a.Action == SpeakerVoiceService.AuditAction && a.ProjectId == projectId)
                .OrderByDescending(a => a.CreatedAt)
                .FirstOrDefaultAsync().ConfigureAwait(true);
        }
    }

    private sealed class RecordingPublisher : ISpeakerVoiceEventPublisher
    {
        private static readonly object Gate = new();

        public static List<SpeakerVoiceChanged> Messages { get; } = [];

        public static void Clear()
        {
            lock (Gate)
            {
                Messages.Clear();
            }
        }

        public Task PublishAsync(SpeakerVoiceChanged message, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(message);
            lock (Gate)
            {
                Messages.Add(message);
            }

            return Task.CompletedTask;
        }
    }
}
