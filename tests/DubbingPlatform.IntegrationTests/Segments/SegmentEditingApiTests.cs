using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using DubbingPlatform.Api.Middleware;
using DubbingPlatform.Application.MultiTenancy;
using DubbingPlatform.Application.Segments;
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

namespace DubbingPlatform.IntegrationTests.Segments;

/// <summary>
/// Task 009: segment list/detail, selection, manual-edit, and retry over PG.
/// Skips when Docker is unavailable (CI runs live).
/// Cross-tenant segment ids return 404 (no leak); project mismatch stays 403.
/// </summary>
public sealed class SegmentEditingApiTests
{
    private const string TestSigningKey = "test-signing-key-0123456789abcdef-test-signing-key-01";

    private const string TestAudience = "dubbing-api";

    [SkippableFact]
    public async Task List_Filter_Matrix_And_Combined_And()
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
            var seed = await SeedSegmentsAsync(connectionString, tenantId, projectGuid, ownerId).ConfigureAwait(true);

            // speakerId filter.
            using (var response = await client.GetAsync(
                $"/api/v1/projects/{projectId}/segments?speakerId={seed.SpeakerA:N}").ConfigureAwait(true))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(true));
                var items = doc.RootElement.GetProperty("items");
                Assert.Equal(2, items.GetArrayLength());
            }

            // reviewStatus filter.
            using (var response = await client.GetAsync(
                $"/api/v1/projects/{projectId}/segments?reviewStatus=Open").ConfigureAwait(true))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(true));
                Assert.Equal(1, doc.RootElement.GetProperty("items").GetArrayLength());
            }

            // qualityFlag filter (code).
            using (var response = await client.GetAsync(
                $"/api/v1/projects/{projectId}/segments?qualityFlag=QC_NOISY").ConfigureAwait(true))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(true));
                Assert.Equal(1, doc.RootElement.GetProperty("items").GetArrayLength());
            }

            // syncIssue=true filter.
            using (var response = await client.GetAsync(
                $"/api/v1/projects/{projectId}/segments?syncIssue=true").ConfigureAwait(true))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(true));
                Assert.Equal(1, doc.RootElement.GetProperty("items").GetArrayLength());
            }

            // text substring (case-insensitive over transcript+translation).
            using (var response = await client.GetAsync(
                $"/api/v1/projects/{projectId}/segments?text=HELLO").ConfigureAwait(true))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(true));
                Assert.Equal(1, doc.RootElement.GetProperty("items").GetArrayLength());
            }

            // time window overlap: [1500,2500) overlaps seg2 only.
            using (var response = await client.GetAsync(
                $"/api/v1/projects/{projectId}/segments?startMs=1500&endMs=2500").ConfigureAwait(true))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(true));
                var items = doc.RootElement.GetProperty("items");
                Assert.Equal(1, items.GetArrayLength());
                Assert.Equal(1000, items[0].GetProperty("startMs").GetInt32());
            }

            // Combined AND: speakerA + text hello → 1 (seg1), speakerA + text nomatch → 0.
            using (var response = await client.GetAsync(
                $"/api/v1/projects/{projectId}/segments?speakerId={seed.SpeakerA:N}&text=hello").ConfigureAwait(true))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(true));
                Assert.Equal(1, doc.RootElement.GetProperty("items").GetArrayLength());
            }

            using (var response = await client.GetAsync(
                $"/api/v1/projects/{projectId}/segments?speakerId={seed.SpeakerA:N}&text=zzz_nomatch").ConfigureAwait(true))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(true));
                Assert.Equal(0, doc.RootElement.GetProperty("items").GetArrayLength());
            }
        }
    }

    [SkippableFact]
    public async Task List_Pagination_Sort_StartMs()
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
            _ = await SeedSegmentsAsync(connectionString, tenantId, projectGuid, ownerId).ConfigureAwait(true);

            using var page1 = await client.GetAsync(
                $"/api/v1/projects/{projectId}/segments?page=1&pageSize=2").ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.OK, page1.StatusCode);
            var doc1 = JsonDocument.Parse(await page1.Content.ReadAsStringAsync().ConfigureAwait(true));
            Assert.Equal(2, doc1.RootElement.GetProperty("items").GetArrayLength());
            Assert.Equal(3, doc1.RootElement.GetProperty("total").GetInt64());
            Assert.True(doc1.RootElement.GetProperty("hasMore").GetBoolean());
            var firstStart = doc1.RootElement.GetProperty("items")[0].GetProperty("startMs").GetInt32();
            var secondStart = doc1.RootElement.GetProperty("items")[1].GetProperty("startMs").GetInt32();
            Assert.True(firstStart < secondStart);

            using var page2 = await client.GetAsync(
                $"/api/v1/projects/{projectId}/segments?page=2&pageSize=2").ConfigureAwait(true);
            var doc2 = JsonDocument.Parse(await page2.Content.ReadAsStringAsync().ConfigureAwait(true));
            Assert.Equal(1, doc2.RootElement.GetProperty("items").GetArrayLength());
            Assert.False(doc2.RootElement.GetProperty("hasMore").GetBoolean());

            // pageSize clamps to 200 (request 500 → 200, still 200 OK shape).
            using var clamped = await client.GetAsync(
                $"/api/v1/projects/{projectId}/segments?page=1&pageSize=500").ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.OK, clamped.StatusCode);
            var clampedDoc = JsonDocument.Parse(await clamped.Content.ReadAsStringAsync().ConfigureAwait(true));
            Assert.Equal(200, clampedDoc.RootElement.GetProperty("pageSize").GetInt32());
        }
    }

    [SkippableFact]
    public async Task Detail_Includes_Versions_And_Selection()
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
            var seed = await SeedSegmentsAsync(connectionString, tenantId, projectGuid, ownerId).ConfigureAwait(true);

            using var detail = await client.GetAsync(
                $"/api/v1/projects/{projectId}/segments/{ToSegmentId(seed.Segment1)}").ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
            var doc = JsonDocument.Parse(await detail.Content.ReadAsStringAsync().ConfigureAwait(true));
            Assert.Equal(0, doc.RootElement.GetProperty("selectionVersion").GetInt32());
            Assert.True(doc.RootElement.GetProperty("transcriptVersions").GetArrayLength() >= 1);
            Assert.True(doc.RootElement.GetProperty("translationVersions").GetArrayLength() >= 1);
            Assert.True(doc.RootElement.TryGetProperty("reviewStatus", out _));
            Assert.True(doc.RootElement.TryGetProperty("qualityCodes", out _));
            Assert.True(doc.RootElement.TryGetProperty("syncStatus", out _));

            // Select then detail reflects pointer + version 1.
            using var select = await SendSelectAsync(
                client, projectId, ToSegmentId(seed.Segment1),
                seed.Transcript1V1.ToString("D"), 0, "prefer v1").ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.OK, select.StatusCode);

            using var detail2 = await client.GetAsync(
                $"/api/v1/projects/{projectId}/segments/{ToSegmentId(seed.Segment1)}").ConfigureAwait(true);
            var doc2 = JsonDocument.Parse(await detail2.Content.ReadAsStringAsync().ConfigureAwait(true));
            Assert.Equal(1, doc2.RootElement.GetProperty("selectionVersion").GetInt32());
            Assert.Equal(
                seed.Transcript1V1.ToString("D"),
                doc2.RootElement.GetProperty("selectedTranscriptVersionId").GetString());
        }
    }

    [SkippableFact]
    public async Task Stale_Write_409_With_Refresh_Payload()
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
            var seed = await SeedSegmentsAsync(connectionString, tenantId, projectGuid, ownerId).ConfigureAwait(true);

            using var first = await SendSelectAsync(
                client, projectId, ToSegmentId(seed.Segment1),
                seed.Transcript1V1.ToString("D"), 0, null).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);

            using var stale = await SendSelectAsync(
                client, projectId, ToSegmentId(seed.Segment1),
                seed.Transcript1V2.ToString("D"), 0, null).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
            var body = JsonDocument.Parse(await stale.Content.ReadAsStringAsync().ConfigureAwait(true));
            var error = body.RootElement.GetProperty("error");
            Assert.Equal("SELECTION_CONFLICT", error.GetProperty("code").GetString());
            var details = error.GetProperty("details");
            Assert.Equal(1, details.GetProperty("currentSelectionVersion").GetInt32());
            var ids = details.GetProperty("currentVersionIds");
            Assert.Equal(
                seed.Transcript1V1.ToString("D"),
                ids.GetProperty("transcriptVersionId").GetString());
        }
    }

    [SkippableFact]
    public async Task Immutable_Versions_Old_Row_Unchanged()
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
            var seed = await SeedSegmentsAsync(connectionString, tenantId, projectGuid, ownerId).ConfigureAwait(true);

            var before = await GetTranscriptTextAsync(connectionString, tenantId, seed.Transcript1V1).ConfigureAwait(true);

            using var edit = await SendEditAsync(
                client, projectId, ToSegmentId(seed.Segment1),
                "corrected hello", 0, "fix typo").ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
            var doc = JsonDocument.Parse(await edit.Content.ReadAsStringAsync().ConfigureAwait(true));
            var newId = doc.RootElement.GetProperty("newVersionId").GetString();
            Assert.NotNull(newId);
            Assert.NotEqual(seed.Transcript1V1.ToString("D"), newId);

            var after = await GetTranscriptTextAsync(connectionString, tenantId, seed.Transcript1V1).ConfigureAwait(true);
            Assert.Equal(before, after);

            var count = await CountTranscriptVersionsAsync(connectionString, tenantId, seed.Segment1).ConfigureAwait(true);
            Assert.Equal(3, count);
        }
    }

    [SkippableFact]
    public async Task Invalidation_Published_And_Audit_Written()
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
            var seed = await SeedSegmentsAsync(connectionString, tenantId, projectGuid, ownerId).ConfigureAwait(true);

            var correlationId = Guid.NewGuid().ToString("N");
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                $"/api/v1/projects/{projectId}/segments/{ToSegmentId(seed.Segment1)}/transcript-selection")
            {
                Content = JsonContent.Create(new
                {
                    versionId = seed.Transcript1V1.ToString("D"),
                    expectedSelectionVersion = 0,
                    reason = "prefer <b>v1</b>",
                }),
            };
            request.Headers.Add("X-Correlation-Id", correlationId);
            using var response = await client.SendAsync(request).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            Assert.Single(RecordingPublisher.Messages);
            var published = RecordingPublisher.Messages[0];
            Assert.Equal(seed.Segment1, published.SegmentId);
            Assert.Equal(1, published.SelectionVersion);
            Assert.False(published.OutputStale);

            var audit = await GetLatestSelectionAuditAsync(connectionString, tenantId, seed.Segment1).ConfigureAwait(true);
            Assert.NotNull(audit);
            Assert.Equal(ownerId.ToString("D"), audit!.Actor);
            Assert.Contains(seed.Segment1.ToString("N"), audit.ResourceId, StringComparison.Ordinal);
            Assert.NotNull(audit.DetailsJson);
            Assert.Contains("transcriptVersionId", audit.DetailsJson!, StringComparison.Ordinal);
            Assert.Contains(correlationId, audit.DetailsJson!, StringComparison.Ordinal);
            Assert.DoesNotContain("<b>", audit.DetailsJson!, StringComparison.Ordinal);
        }
    }

    [SkippableFact]
    public async Task Stale_Output_Flag_When_Final_Output_Exists()
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
            var seed = await SeedSegmentsAsync(connectionString, tenantId, projectGuid, ownerId).ConfigureAwait(true);
            await SeedOutputAsync(connectionString, tenantId, projectGuid, seed.RunId).ConfigureAwait(true);

            using var edit = await SendEditAsync(
                client, projectId, ToSegmentId(seed.Segment1),
                "post-render fix", 0, "late fix").ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
            var doc = JsonDocument.Parse(await edit.Content.ReadAsStringAsync().ConfigureAwait(true));
            Assert.True(doc.RootElement.GetProperty("outputStale").GetBoolean());
            Assert.Equal("OUTPUT_STALE", doc.RootElement.GetProperty("warningCode").GetString());
        }
    }

    [SkippableFact]
    public async Task Version_Errors_Map_To_Codes()
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
            var seed = await SeedSegmentsAsync(connectionString, tenantId, projectGuid, ownerId).ConfigureAwait(true);
            var seg = ToSegmentId(seed.Segment1);

            // Missing version → 404 VERSION_NOT_FOUND.
            using (var missing = await SendSelectAsync(client, projectId, seg, Guid.NewGuid().ToString("D"), 0, null).ConfigureAwait(true))
            {
                Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
                var body = JsonDocument.Parse(await missing.Content.ReadAsStringAsync().ConfigureAwait(true));
                Assert.Equal("VERSION_NOT_FOUND", body.RootElement.GetProperty("error").GetProperty("code").GetString());
            }

            // Cross-segment version → 400 VERSION_SEGMENT_MISMATCH.
            using (var foreign = await SendSelectAsync(client, projectId, seg, seed.OtherTranscript.ToString("D"), 0, null).ConfigureAwait(true))
            {
                Assert.Equal(HttpStatusCode.BadRequest, foreign.StatusCode);
                var body = JsonDocument.Parse(await foreign.Content.ReadAsStringAsync().ConfigureAwait(true));
                Assert.Equal("VERSION_SEGMENT_MISMATCH", body.RootElement.GetProperty("error").GetProperty("code").GetString());
            }

            // Empty text → 400 SEGMENT_TEXT_EMPTY.
            using (var empty = await SendEditAsync(client, projectId, seg, "   ", 0, null).ConfigureAwait(true))
            {
                Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
                var body = JsonDocument.Parse(await empty.Content.ReadAsStringAsync().ConfigureAwait(true));
                Assert.Equal("SEGMENT_TEXT_EMPTY", body.RootElement.GetProperty("error").GetProperty("code").GetString());
            }
        }
    }

    [SkippableFact]
    public async Task Retry_Success_And_Active_Conflict()
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
            var seed = await SeedSegmentsAsync(connectionString, tenantId, projectGuid, ownerId).ConfigureAwait(true);

            // Seed a failed execution for seg1 → retry 202.
            await SeedFailedExecutionAsync(connectionString, tenantId, projectGuid, seed.RunId, seed.Segment1).ConfigureAwait(true);
            using (var retry = await SendRetryAsync(client, projectId, ToSegmentId(seed.Segment1)).ConfigureAwait(true))
            {
                Assert.Equal(HttpStatusCode.Accepted, retry.StatusCode);
            }

            // Seed an active execution for seg2 → retry 409 with existing job.
            var activeId = await SeedActiveExecutionAsync(connectionString, tenantId, projectGuid, seed.RunId, seed.Segment2).ConfigureAwait(true);
            using (var conflict = await SendRetryAsync(client, projectId, ToSegmentId(seed.Segment2)).ConfigureAwait(true))
            {
                Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
                var body = JsonDocument.Parse(await conflict.Content.ReadAsStringAsync().ConfigureAwait(true));
                var error = body.RootElement.GetProperty("error");
                Assert.Equal("SEGMENT_RETRY_ACTIVE", error.GetProperty("code").GetString());
                Assert.Equal(
                    activeId.ToString("D"),
                    error.GetProperty("details").GetProperty("executionId").GetString());
            }

            // Idempotent poll: second 409 returns the same execution.
            using (var again = await SendRetryAsync(client, projectId, ToSegmentId(seed.Segment2)).ConfigureAwait(true))
            {
                Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
                var body = JsonDocument.Parse(await again.Content.ReadAsStringAsync().ConfigureAwait(true));
                Assert.Equal(
                    activeId.ToString("D"),
                    body.RootElement.GetProperty("error").GetProperty("details").GetProperty("executionId").GetString());
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
            var seed = await SeedSegmentsAsync(connectionString, tenantId, projectGuid, ownerId).ConfigureAwait(true);
            var seg = ToSegmentId(seed.Segment1);

            // Viewer can read but cannot mutate.
            using var viewer = factory.CreateClient();
            UseToken(viewer, tenantId, Guid.NewGuid(), "ProjectViewer");
            using (var list = await viewer.GetAsync($"/api/v1/projects/{projectId}/segments").ConfigureAwait(true))
            {
                Assert.Equal(HttpStatusCode.OK, list.StatusCode);
            }

            using (var denied = await SendSelectAsync(viewer, projectId, seg, seed.Transcript1V1.ToString("D"), 0, null).ConfigureAwait(true))
            {
                Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
            }

            // Reviewer can read but cannot mutate (no project.edit).
            using var reviewer = factory.CreateClient();
            UseToken(reviewer, tenantId, Guid.NewGuid(), "Reviewer");
            using (var detail = await reviewer.GetAsync($"/api/v1/projects/{projectId}/segments/{seg}").ConfigureAwait(true))
            {
                Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
            }

            using (var denied = await SendEditAsync(reviewer, projectId, seg, "nope", 0, null).ConfigureAwait(true))
            {
                Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
            }

            // Editor member can mutate.
            var editorId = Guid.NewGuid();
            await SeedMembershipAsync(connectionString, tenantId, projectGuid, editorId).ConfigureAwait(true);
            using var editor = factory.CreateClient();
            UseToken(editor, tenantId, editorId, "ProjectEditor");
            using (var allowed = await SendSelectAsync(editor, projectId, seg, seed.Transcript1V1.ToString("D"), 0, null).ConfigureAwait(true))
            {
                Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
            }

            // Unauthenticated → 401.
            using var anon = factory.CreateClient();
            using (var unauth = await anon.GetAsync($"/api/v1/projects/{projectId}/segments").ConfigureAwait(true))
            {
                Assert.Equal(HttpStatusCode.Unauthorized, unauth.StatusCode);
            }
        }
    }

    [SkippableFact]
    public async Task Cross_Tenant_Segment_404()
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
            var seed = await SeedSegmentsAsync(connectionString, tenantA, projectGuid, ownerA).ConfigureAwait(true);

            using var clientB = factory.CreateClient();
            UseToken(clientB, tenantB, Guid.NewGuid(), "TenantAdmin");

            // Same project id under another tenant → 403 (project ownership).
            using (var project = await clientB.GetAsync(
                $"/api/v1/projects/{projectId}/segments/{ToSegmentId(seed.Segment1)}").ConfigureAwait(true))
            {
                Assert.Equal(HttpStatusCode.Forbidden, project.StatusCode);
            }

            // Real cross-tenant segment: project B reads segment from tenant A → 404.
            using var clientB2 = factory.CreateClient();
            var ownerB = Guid.NewGuid();
            UseToken(clientB2, tenantB, ownerB, "TenantAdmin");
            var projectB = await CreateProjectAsync(clientB2).ConfigureAwait(true);
            using (var cross = await clientB2.GetAsync(
                $"/api/v1/projects/{projectB}/segments/{ToSegmentId(seed.Segment1)}").ConfigureAwait(true))
            {
                Assert.Equal(HttpStatusCode.NotFound, cross.StatusCode);
            }
        }
    }

    private sealed record SeedData(
        Guid RunId,
        Guid Segment1,
        Guid Segment2,
        Guid Segment3,
        Guid SpeakerA,
        Guid SpeakerB,
        Guid Transcript1V1,
        Guid Transcript1V2,
        Guid Translation1V1,
        Guid OtherSegment,
        Guid OtherTranscript);

    private static string ToSegmentId(Guid id)
    {
        return string.Concat("seg_", id.ToString("N"));
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
        string connectionString, bool useRecordingPublisher = false)
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

            if (useRecordingPublisher)
            {
                builder.ConfigureServices(services =>
                {
                    services.AddSingleton<RecordingPublisher>();
                    services.AddScoped<ISegmentSelectionEventPublisher>(
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
            Content = JsonContent.Create(new { name = $"S-{Guid.NewGuid():N}", sourceLanguage = "en", targetLanguage = "es" }),
        };
        request.Headers.Add("Idempotency-Key", NewKey());
        using var response = await client.SendAsync(request).ConfigureAwait(true);
        var payload = await response.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"Create failed: {(int)response.StatusCode} {payload}");
        using var document = JsonDocument.Parse(payload);
        return document.RootElement.GetProperty("id").GetString()!;
    }

    private static async Task<HttpResponseMessage> SendSelectAsync(
        HttpClient client, string projectId, string segmentId, string versionId, int expected, string? reason)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"/api/v1/projects/{projectId}/segments/{segmentId}/transcript-selection")
        {
            Content = JsonContent.Create(new { versionId, expectedSelectionVersion = expected, reason }),
        };
        return await client.SendAsync(request).ConfigureAwait(true);
    }

    private static async Task<HttpResponseMessage> SendEditAsync(
        HttpClient client, string projectId, string segmentId, string text, int expected, string? reason)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"/api/v1/projects/{projectId}/segments/{segmentId}/transcript-edits")
        {
            Content = JsonContent.Create(new { text, expectedSelectionVersion = expected, reason }),
        };
        return await client.SendAsync(request).ConfigureAwait(true);
    }

    private static async Task<HttpResponseMessage> SendRetryAsync(HttpClient client, string projectId, string segmentId)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"/api/v1/projects/{projectId}/segments/{segmentId}/retry")
        {
            Content = JsonContent.Create(new { }),
        };
        request.Headers.Add("Idempotency-Key", NewKey());
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

    private static async Task<SeedData> SeedSegmentsAsync(
        string connectionString, Guid tenantId, Guid projectId, Guid ownerId)
    {
        var now = DateTimeOffset.UtcNow;
        var runId = Guid.NewGuid();
        var seg1 = Guid.NewGuid();
        var seg2 = Guid.NewGuid();
        var seg3 = Guid.NewGuid();
        var speakerA = Guid.NewGuid();
        var speakerB = Guid.NewGuid();
        var t1v1 = Guid.NewGuid();
        var t1v2 = Guid.NewGuid();
        var tr1v1 = Guid.NewGuid();
        var otherSeg = seg3;
        var otherTranscript = Guid.NewGuid();

        using (TenantContext.BeginScope(tenantId))
        {
            using var context = new AppDbContext(CreateOptions(connectionString));
            context.Set<ProcessingRun>().Add(new ProcessingRun(
                runId, tenantId, projectId, 0, ProcessingRunStatus.Running, "1.0.0",
                new string('a', 64), new string('b', 64), new string('c', 64),
                now, now, now, null));
            context.Set<SpeechSegment>().Add(new SpeechSegment(
                seg1, tenantId, projectId, runId, 0, 0, 1000, "Pending", speakerA, now));
            context.Set<SpeechSegment>().Add(new SpeechSegment(
                seg2, tenantId, projectId, runId, 1, 1000, 2000, "Pending", speakerA, now));
            context.Set<SpeechSegment>().Add(new SpeechSegment(
                seg3, tenantId, projectId, runId, 2, 2000, 3000, "Pending", speakerB, now));

            context.Set<TranscriptVersion>().Add(new TranscriptVersion(
                t1v1, tenantId, projectId, runId, seg1, "mock", "mock-v1", "en",
                "hello world", 0.9, null, false, false, now));
            context.Set<TranscriptVersion>().Add(new TranscriptVersion(
                t1v2, tenantId, projectId, runId, seg1, "mock", "mock-v1", "en",
                "hello there", 0.8, null, false, false, now));
            context.Set<TranslationVersion>().Add(new TranslationVersion(
                tr1v1, tenantId, projectId, runId, seg1,
                "hallo Welt", [], 0.9, 0.9, 0.9, "mock", "mock-v1", null, null, false, now));

            var t2 = Guid.NewGuid();
            context.Set<TranscriptVersion>().Add(new TranscriptVersion(
                t2, tenantId, projectId, runId, seg2, "mock", "mock-v1", "en",
                "second segment", 0.9, null, false, false, now));
            context.Set<TranslationVersion>().Add(new TranslationVersion(
                Guid.NewGuid(), tenantId, projectId, runId, seg2,
                "zweite Zeile", [], 0.9, 0.9, 0.9, "mock", "mock-v1", null, null, false, now));

            context.Set<TranscriptVersion>().Add(new TranscriptVersion(
                otherTranscript, tenantId, projectId, runId, seg3, "mock", "mock-v1", "en",
                "third segment", 0.9, null, false, false, now));

            // Review/Open on seg2 only.
            context.Set<ReviewItem>().Add(new ReviewItem(
                Guid.NewGuid(), tenantId, projectId, runId, ScopeType.Segment, seg2.ToString("D"), seg2,
                ReviewStatus.Open, "low-confidence", null, now, now, null));

            // Quality code on seg1 only.
            context.Set<QualityResult>().Add(new QualityResult(
                Guid.NewGuid(), tenantId, projectId, runId, ScopeType.Segment, seg1.ToString("D"), seg1,
                QualityStatus.RetryRequired, "QC_NOISY", "high", "noisy audio", null, null, now));

            // Sync issue on seg2 only.
            context.Set<SyncResult>().Add(new SyncResult(
                Guid.NewGuid(), tenantId, projectId, runId, seg2,
                0.4, SyncStatus.SyncRetryable, 1000, 1400, 0.4, 1.4, now));
            context.Set<SyncResult>().Add(new SyncResult(
                Guid.NewGuid(), tenantId, projectId, runId, seg1,
                0.95, SyncStatus.SyncAcceptable, 1000, 1000, 0.0, 1.0, now));

            await context.SaveChangesAsync().ConfigureAwait(true);
        }

        return new SeedData(
            runId, seg1, seg2, seg3, speakerA, speakerB,
            t1v1, t1v2, tr1v1, otherSeg, otherTranscript);
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

    private static async Task SeedFailedExecutionAsync(
        string connectionString, Guid tenantId, Guid projectId, Guid runId, Guid segmentId)
    {
        var now = DateTimeOffset.UtcNow;
        using (TenantContext.BeginScope(tenantId))
        {
            using var context = new AppDbContext(CreateOptions(connectionString));
            context.Set<StageExecution>().Add(new StageExecution(
                Guid.NewGuid(), tenantId, projectId, runId, StageType.Transcription, ScopeType.Segment,
                segmentId.ToString("D"), segmentId, 0, StageStatus.Failed,
                "test", Guid.NewGuid().ToString("N"), 0, now.AddHours(1), now, now,
                new string('a', 64), new string('b', 64), new string('c', 64), null, "E_FAIL", "failed", now, now));
            context.Set<RunStageSummary>().Add(new RunStageSummary(
                Guid.NewGuid(), tenantId, runId, StageType.Transcription, 3, 2, 1, 0, 0, 0, now, now));
            await context.SaveChangesAsync().ConfigureAwait(true);
        }
    }

    private static async Task<Guid> SeedActiveExecutionAsync(
        string connectionString, Guid tenantId, Guid projectId, Guid runId, Guid segmentId)
    {
        var now = DateTimeOffset.UtcNow;
        var executionId = Guid.NewGuid();
        using (TenantContext.BeginScope(tenantId))
        {
            using var context = new AppDbContext(CreateOptions(connectionString));
            context.Set<StageExecution>().Add(new StageExecution(
                executionId, tenantId, projectId, runId, StageType.Transcription, ScopeType.Segment,
                segmentId.ToString("D"), segmentId, 1, StageStatus.Running,
                "test", Guid.NewGuid().ToString("N"), 0, now.AddHours(1), now, null,
                new string('a', 64), new string('b', 64), new string('c', 64), null, null, null, now, now));
            await context.SaveChangesAsync().ConfigureAwait(true);
        }

        return executionId;
    }

    private static async Task<string> GetTranscriptTextAsync(string connectionString, Guid tenantId, Guid versionId)
    {
        using (TenantContext.BeginScope(tenantId))
        {
            using var context = new AppDbContext(CreateOptions(connectionString));
            var version = await context.Set<TranscriptVersion>().AsNoTracking()
                .FirstAsync(v => v.Id == versionId).ConfigureAwait(true);
            return version.Text;
        }
    }

    private static async Task<int> CountTranscriptVersionsAsync(string connectionString, Guid tenantId, Guid segmentId)
    {
        using (TenantContext.BeginScope(tenantId))
        {
            using var context = new AppDbContext(CreateOptions(connectionString));
            return await context.Set<TranscriptVersion>().CountAsync(v => v.SegmentId == segmentId).ConfigureAwait(true);
        }
    }

    private static async Task<AuditEvent?> GetLatestSelectionAuditAsync(
        string connectionString, Guid tenantId, Guid segmentId)
    {
        using (TenantContext.BeginScope(tenantId))
        {
            using var context = new AppDbContext(CreateOptions(connectionString));
            return await context.Set<AuditEvent>().AsNoTracking()
                .Where(a => a.Action == SegmentSelectionService.AuditAction
                    && a.ResourceId == segmentId.ToString("N"))
                .OrderByDescending(a => a.CreatedAt)
                .FirstOrDefaultAsync().ConfigureAwait(true);
        }
    }

    private sealed class RecordingPublisher : ISegmentSelectionEventPublisher
    {
        private static readonly object Gate = new();

        public static List<SegmentSelectionChanged> Messages { get; } = [];

        public static void Clear()
        {
            lock (Gate)
            {
                Messages.Clear();
            }
        }

        public Task PublishAsync(SegmentSelectionChanged message, CancellationToken cancellationToken = default)
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
