using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DubbingPlatform.IntegrationTests.OpenApi;

/// <summary>
/// Task 014: the versioned OpenAPI bundle
/// (<c>src/DubbingPlatform.Api/OpenApi/openapi.v1.json</c>) is the single
/// contract authority. These hermetic tests (no Docker) assert route coverage
/// for every Tasks 006-013 endpoint, the error/pagination envelopes, the
/// frozen 14-type SSE contract, per-mutation idempotency/concurrency docs,
/// secret-free examples, and the committed generated client (with a stamp
/// hash that matches the bundle). Adding an endpoint requires updating the
/// bundle AND the expected list below in the same commit.
/// </summary>
public sealed class OpenApiCoverageTests
{
    private static readonly string[] ExpectedSseTypes =
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

    // Full Tasks 006-013 surface: "METHOD /api/v1/..." (83 operations).
    private static readonly string[] ExpectedRoutes =
    [
        "POST /api/v1/auth/login",
        "POST /api/v1/auth/refresh",
        "POST /api/v1/auth/logout",
        "GET /api/v1/me",
        "GET /api/v1/me/preferences",
        "PUT /api/v1/me/preferences",
        "GET /api/v1/dashboard/summary",
        "POST /api/v1/projects",
        "GET /api/v1/projects",
        "GET /api/v1/projects/{projectId}",
        "PATCH /api/v1/projects/{projectId}",
        "DELETE /api/v1/projects/{projectId}",
        "POST /api/v1/projects/{projectId}/archive",
        "POST /api/v1/projects/{projectId}/unarchive",
        "POST /api/v1/projects/{projectId}/uploads",
        "GET /api/v1/projects/{projectId}/uploads",
        "GET /api/v1/projects/{projectId}/uploads/{uploadId}",
        "POST /api/v1/projects/{projectId}/uploads/{uploadId}/parts",
        "POST /api/v1/projects/{projectId}/uploads/{uploadId}/complete",
        "POST /api/v1/projects/{projectId}/uploads/{uploadId}/abort",
        "POST /api/v1/projects/{projectId}/processing",
        "GET /api/v1/projects/{projectId}/processing",
        "GET /api/v1/projects/{projectId}/processing/active",
        "GET /api/v1/projects/{projectId}/processing/{runId}",
        "POST /api/v1/projects/{projectId}/processing/{runId}/cancel",
        "POST /api/v1/projects/{projectId}/processing/{runId}/retry",
        "POST /api/v1/projects/{projectId}/processing/cancel",
        "POST /api/v1/projects/{projectId}/processing/retry",
        "GET /api/v1/projects/{projectId}/progress",
        "GET /api/v1/projects/{projectId}/progress/stream",
        "GET /api/v1/projects/{projectId}/workspace",
        "GET /api/v1/projects/{projectId}/activity",
        "GET /api/v1/projects/{projectId}/quality",
        "GET /api/v1/projects/{projectId}/segments",
        "GET /api/v1/projects/{projectId}/segments/{segmentId}",
        "POST /api/v1/projects/{projectId}/segments/{segmentId}/retry",
        "POST /api/v1/projects/{projectId}/segments/{segmentId}/transcript-selection",
        "POST /api/v1/projects/{projectId}/segments/{segmentId}/translation-selection",
        "POST /api/v1/projects/{projectId}/segments/{segmentId}/transcript-edits",
        "POST /api/v1/projects/{projectId}/segments/{segmentId}/translation-edits",
        "GET /api/v1/projects/{projectId}/speakers",
        "GET /api/v1/projects/{projectId}/speakers/{speakerId}",
        "GET /api/v1/projects/{projectId}/speakers/{speakerId}/available-voices",
        "PUT /api/v1/projects/{projectId}/speakers/{speakerId}/voice-assignment",
        "POST /api/v1/projects/{projectId}/voice-previews",
        "GET /api/v1/projects/{projectId}/voice-previews",
        "GET /api/v1/projects/{projectId}/voice-previews/{previewId}",
        "GET /api/v1/projects/{projectId}/reviews",
        "GET /api/v1/projects/{projectId}/reviews/{reviewId}",
        "GET /api/v1/reviews/{reviewId}",
        "POST /api/v1/reviews/{reviewId}/approve",
        "POST /api/v1/reviews/{reviewId}/reject",
        "POST /api/v1/reviews/{reviewId}/requeue",
        "GET /api/v1/reviews/{reviewId}/context",
        "POST /api/v1/reviews/{reviewId}/resolve",
        "POST /api/v1/reviews/{reviewId}/dismiss",
        "POST /api/v1/reviews/{reviewId}/reopen",
        "POST /api/v1/reviews/{reviewId}/resolve-with-edit",
        "GET /api/v1/projects/{projectId}/output",
        "GET /api/v1/projects/{projectId}/output/download",
        "POST /api/v1/projects/{projectId}/exports",
        "GET /api/v1/projects/{projectId}/exports",
        "GET /api/v1/projects/{projectId}/exports/{exportId}",
        "GET /api/v1/projects/{projectId}/exports/{exportId}/download",
        "GET /api/v1/notifications",
        "GET /api/v1/notifications/unread-count",
        "POST /api/v1/notifications/{notificationId}/read",
        "POST /api/v1/notifications/read-all",
        "GET /api/v1/admin/status",
        "GET /api/v1/admin/stages/{execId}",
        "GET /api/v1/admin/provider-executions/{id}",
        "GET /api/v1/admin/dlq/summary",
        "GET /api/v1/admin/leases/status",
        "GET /api/v1/admin/reviews/backlog",
        "GET /api/v1/admin/usage",
        "GET /api/v1/admin/quotas",
        "GET /api/v1/admin/provider-health",
        "GET /api/v1/admin/provider-routes",
        "GET /api/v1/admin/diagnostics/queues",
        "GET /api/v1/admin/diagnostics/dlq",
        "GET /api/v1/admin/diagnostics/leases",
        "GET /api/v1/admin/diagnostics/orphans",
        "GET /api/v1/admin/diagnostics/review-backlog",
    ];

    // Naturally-idempotent POSTs that intentionally omit Idempotency-Key docs.
    private static readonly string[] IdempotencyExempt =
    [
        "POST /notifications/{notificationId}/read",
        "POST /notifications/read-all",
    ];

    private static readonly string[] ExpectedGeneratedTypes =
    [
        "ErrorCode",
        "ErrorResponse",
        "PaginatedResult",
        "SseEnvelope",
        "SseEventType",
        "Project",
        "ProjectListResponse",
        "DashboardSummary",
        "ProcessingRun",
        "ProgressResponse",
        "Workspace",
        "SegmentDetailResponse",
        "SegmentMutationResponse",
        "SpeakerDetailResponse",
        "AvailableVoicesResponse",
        "VoiceAssignmentResponse",
        "VoicePreviewResponse",
        "ReviewContextResponse",
        "ReviewMutationRequest",
        "ReviewMutationResponse",
        "OutputResponse",
        "Export",
        "Notification",
        "NotificationListResponse",
        "AdminUsageResponse",
        "AdminQuotasResponse",
        "ProviderHealth",
        "QueueDepth",
        "DlqSummary",
        "OrphanPage",
        "ReviewBacklog",
    ];

    [Fact]
    public void Bundle_Parses_And_Is_Versioned_V1()
    {
        var document = LoadBundle();
        Assert.Equal("3.0.3", document.RootElement.GetProperty("openapi").GetString());
        Assert.Equal("v1", document.RootElement.GetProperty("info").GetProperty("version").GetString());

        var servers = document.RootElement.GetProperty("servers").EnumerateArray().ToList();
        Assert.NotEmpty(servers);
        foreach (var server in servers)
        {
            var url = server.GetProperty("url").GetString();
            Assert.StartsWith("/", url);
            Assert.DoesNotContain("http", url, StringComparison.OrdinalIgnoreCase);
        }

        Assert.True(document.RootElement.TryGetProperty("paths", out _));
        Assert.True(document.RootElement.TryGetProperty("components", out _));
    }

    [Fact]
    public void All_Expected_Routes_Present_With_Unique_OperationIds()
    {
        using var document = LoadBundle();
        var paths = document.RootElement.GetProperty("paths");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var operationIds = new HashSet<string>(StringComparer.Ordinal);
        var total = 0;

        foreach (var pathEntry in paths.EnumerateObject())
        {
            foreach (var opEntry in pathEntry.Value.EnumerateObject())
            {
                var key = opEntry.Name.ToUpperInvariant() + " /api/v1" + pathEntry.Name;
                seen.Add(key);
                total++;
                var operationId = opEntry.Value.GetProperty("operationId").GetString();
                Assert.False(string.IsNullOrWhiteSpace(operationId));
                Assert.True(operationIds.Add(operationId!), $"Duplicate operationId '{operationId}'.");
            }
        }

        Assert.Equal(ExpectedRoutes.Length, total);
        foreach (var expected in ExpectedRoutes)
        {
            Assert.True(seen.Contains(expected), $"Bundle is missing route '{expected}'.");
        }
    }

    [Fact]
    public void Error_And_Pagination_Envelopes_Present()
    {
        using var document = LoadBundle();
        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");

        var error = schemas.GetProperty("ErrorResponse");
        var errorProps = error.GetProperty("properties").GetProperty("error");
        foreach (var field in new[] { "code", "message", "correlationId", "details" })
        {
            Assert.True(errorProps.GetProperty("properties").TryGetProperty(field, out _), $"ErrorResponse.error is missing '{field}'.");
        }

        var codes = schemas.GetProperty("ErrorCode").GetProperty("enum").EnumerateArray()
            .Select(e => e.GetString()).ToList();
        Assert.Equal(65, codes.Count);
        foreach (var code in new[] { "VALIDATION_FAILED", "SELECTION_CONFLICT", "REVIEW_VERSION_CONFLICT", "EXPORT_INCOMPLETE", "URL_EXPIRED", "INTERNAL_ERROR", "TOKEN_REUSED" })
        {
            Assert.Contains(code, codes);
        }

        var page = schemas.GetProperty("PaginatedResult");
        foreach (var field in new[] { "items", "page", "pageSize", "total", "hasMore" })
        {
            Assert.True(page.GetProperty("properties").TryGetProperty(field, out _), $"PaginatedResult is missing '{field}'.");
        }

        var projectList = schemas.GetProperty("ProjectListResponse").GetProperty("properties");
        foreach (var field in new[] { "sort", "sortDir", "clamped" })
        {
            Assert.True(projectList.TryGetProperty(field, out _), $"ProjectListResponse is missing '{field}'.");
        }
    }

    [Fact]
    public void Sse_Contract_Present_With_Stream_Docs()
    {
        using var document = LoadBundle();
        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");

        var types = schemas.GetProperty("SseEventType").GetProperty("enum").EnumerateArray()
            .Select(e => e.GetString()).ToList();
        Assert.Equal(ExpectedSseTypes.Length, types.Count);
        foreach (var type in ExpectedSseTypes)
        {
            Assert.Contains(type, types);
        }

        var envelope = schemas.GetProperty("SseEnvelope").GetProperty("properties");
        foreach (var field in new[] { "eventId", "schemaVersion", "eventType", "tenantId", "occurredAt", "payload", "correlationId" })
        {
            Assert.True(envelope.TryGetProperty(field, out _), $"SseEnvelope is missing '{field}'.");
        }

        var stream = document.RootElement.GetProperty("paths")
            .GetProperty("/projects/{projectId}/progress/stream")
            .GetProperty("get");
        var response = stream.GetProperty("responses").GetProperty("200")
            .GetProperty("content");
        Assert.True(response.TryGetProperty("text/event-stream", out _));

        var parameterNames = ResolveParameterNames(document, stream);
        Assert.Contains("Last-Event-ID", parameterNames);
        Assert.Contains("access_token", parameterNames);
    }

    [Fact]
    public void Mutations_Document_Idempotency_And_Concurrency()
    {
        using var document = LoadBundle();
        var paths = document.RootElement.GetProperty("paths");
        var exempt = new HashSet<string>(IdempotencyExempt, StringComparer.Ordinal);

        foreach (var pathEntry in paths.EnumerateObject())
        {
            foreach (var opEntry in pathEntry.Value.EnumerateObject())
            {
                var method = opEntry.Name.ToUpperInvariant();
                if (method != "POST" && method != "PUT" && method != "PATCH" && method != "DELETE")
                {
                    continue;
                }

                var relative = pathEntry.Name;
                if (relative.StartsWith("/auth/", StringComparison.Ordinal) || relative.StartsWith("/me", StringComparison.Ordinal))
                {
                    continue;
                }

                if (exempt.Contains(method + " " + relative))
                {
                    continue;
                }

                var names = ResolveParameterNames(document, opEntry.Value);
                Assert.Contains("Idempotency-Key", names);
            }
        }

        var patch = paths.GetProperty("/projects/{projectId}").GetProperty("patch");
        Assert.Contains("If-Match", ResolveParameterNames(document, patch));

        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");
        foreach (var schemaName in new[] { "SegmentSelectionRequest", "SegmentEditRequest", "ReviewMutationRequest" })
        {
            var schema = schemas.GetProperty(schemaName);
            var required = schema.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToList();
            Assert.Contains("expectedVersion", required);
        }
    }

    [Fact]
    public void No_Real_Secrets_In_Bundle()
    {
        var raw = File.ReadAllText(BundlePath());
        Assert.DoesNotContain("Bearer eyJ", raw, StringComparison.Ordinal);
        Assert.Contains("\"***\"", raw, StringComparison.Ordinal);
    }

    [Fact]
    public void No_Duplicate_Path_Keys_In_Bundle()
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(BundlePath()))
        {
            if (line.StartsWith("    \"/", StringComparison.Ordinal) && line.EndsWith("{", StringComparison.Ordinal))
            {
                var key = line.Trim().TrimEnd('{').Trim();
                counts[key] = counts.TryGetValue(key, out var current) ? current + 1 : 1;
            }
        }

        foreach (var entry in counts)
        {
            Assert.True(entry.Value == 1, $"Duplicate bundle path key {entry.Key}.");
        }
    }

    [Fact]
    public void Generated_Client_Committed_With_Matching_Stamp()
    {
        var root = RepoRoot();
        var generated = Path.Combine(root, "frontend", "src", "api", "generated");
        foreach (var file in new[] { "index.ts", "schemas.ts", "client.ts", "OPENAPI_VERSION" })
        {
            Assert.True(File.Exists(Path.Combine(generated, file)), $"Missing generated file '{file}'.");
        }

        foreach (var file in new[] { "index.ts", "schemas.ts", "client.ts" })
        {
            var text = File.ReadAllText(Path.Combine(generated, file));
            Assert.StartsWith("/* auto-generated", text, StringComparison.Ordinal);
        }

        var schemas = File.ReadAllText(Path.Combine(generated, "schemas.ts"));
        foreach (var type in ExpectedGeneratedTypes)
        {
            Assert.Contains(type, schemas, StringComparison.Ordinal);
        }

        var client = File.ReadAllText(Path.Combine(generated, "client.ts"));
        foreach (var member in new[] { "class ApiClient", "class ApiError", "openStream", "parseFrame", "listProjects", "streamProgress", "resolveReview", "createExport", "getAdminUsage", "markAllNotificationsRead" })
        {
            Assert.Contains(member, client, StringComparison.Ordinal);
        }

        var index = File.ReadAllText(Path.Combine(generated, "index.ts"));
        Assert.Contains("schemas.js", index, StringComparison.Ordinal);
        Assert.Contains("client.js", index, StringComparison.Ordinal);

        var bundleBytes = File.ReadAllBytes(BundlePath());
        using var sha = SHA256.Create();
        var hash = BitConverter.ToString(sha.ComputeHash(bundleBytes)).Replace("-", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
        var stamp = File.ReadAllText(Path.Combine(generated, "OPENAPI_VERSION"));
        Assert.Contains("bundle=sha256:" + hash, stamp, StringComparison.Ordinal);
    }

    private static List<string> ResolveParameterNames(JsonDocument document, JsonElement operation)
    {
        var names = new List<string>();
        if (!operation.TryGetProperty("parameters", out var parameters))
        {
            return names;
        }

        var defined = document.RootElement.GetProperty("components").TryGetProperty("parameters", out var defs)
            ? defs
            : (JsonElement?)null;
        foreach (var parameter in parameters.EnumerateArray())
        {
            if (parameter.TryGetProperty("$ref", out var reference))
            {
                var name = reference.GetString()!.Split('/')[^1];
                if (defined.HasValue && defined.Value.TryGetProperty(name, out var resolved)
                    && resolved.TryGetProperty("name", out var resolvedName))
                {
                    names.Add(resolvedName.GetString()!);
                }
                else
                {
                    names.Add(name);
                }
            }
            else if (parameter.TryGetProperty("name", out var direct))
            {
                names.Add(direct.GetString()!);
            }
        }

        return names;
    }

    private static JsonDocument LoadBundle()
    {
        return JsonDocument.Parse(File.ReadAllText(BundlePath()));
    }

    private static string BundlePath()
    {
        return Path.Combine(RepoRoot(), "src", "DubbingPlatform.Api", "OpenApi", "openapi.v1.json");
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "DubbingPlatform.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Repository root (DubbingPlatform.sln) was not found.");
    }
}
