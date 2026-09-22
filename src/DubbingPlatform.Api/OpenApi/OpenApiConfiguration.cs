using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace DubbingPlatform.Api.OpenApi;

/// <summary>
/// Enriches the <c>v1</c> OpenAPI document with the platform conventions:
/// JWT bearer security scheme, global <c>Idempotency-Key</c> header on mutations,
/// and document metadata. Error-envelope (<c>ErrorResponse</c>) and pagination
/// (<c>PaginatedResult</c>) schemas are emitted from controller
/// <c>ProducesResponseType</c> references; this transformer only adds the
/// cross-cutting security and header contracts. Auth
/// (<c>/api/v1/auth/*</c>) and identity (<c>/api/v1/me*</c>) routes are
/// excluded from <c>Idempotency-Key</c>: login/refresh mint fresh secrets per
/// call by design (replay must not return the same secret), and preference
/// PUT is naturally idempotent with last-writer-wins per key.
/// </summary>
public static class OpenApiConfiguration
{
    /// <summary>
    /// Registers the <c>v1</c> document with the enricher.
    /// </summary>
    public static void AddDubbingOpenApi(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOpenApi("v1", options =>
        {
            options.AddDocumentTransformer((document, _, _) =>
            {
                document.Info.Title = "Dubbing Platform API";
                document.Info.Version = "v1";
                document.Info.Description = "Tenant-scoped dubbing pipeline. Auth: JWT bearer (tid/sub/roles) or ?access_token= on SSE /stream (same policy, never logged). Mutations accept Idempotency-Key. Errors use { error: { code, message, correlationId, details } } with codes including LANGUAGE_IMMUTABLE, SETTINGS_LOCKED_ACTIVE_RUN, PROJECT_NOT_FOUND, PROJECT_HAS_ACTIVE_RUN, SETTINGS_VERSION_CONFLICT, IDEMPOTENCY_KEY_REQUIRED, IDEMPOTENCY_KEY_REUSED, PROJECT_ARCHIVED, RUN_ALREADY_TERMINAL, RUN_ALREADY_ACTIVE, CONFIG_CHANGED_SINCE_RUN. Project lists use { items, page, pageSize, total, sort, sortDir, hasMore, clamped }; dashboard summary is GET /api/v1/dashboard/summary with seven sections; processing start returns 202 (replay 200 + Idempotent-Replayed:true) and run lists use { items, page, pageSize, total, hasMore }; workspace aggregate is GET /api/v1/projects/{projectId}/workspace (single call, ten sections); progress exposes percentApproximate (display-only) and SSE is GET .../progress/stream (text/event-stream, Last-Event-ID accepted, event freeze in Task 013).";

                document.Components ??= new OpenApiComponents();
                if (document.Components.SecuritySchemes is null)
                {
                    document.Components.SecuritySchemes = new Dictionary<string, IOpenApiSecurityScheme>(StringComparer.Ordinal);
                }

                if (!document.Components.SecuritySchemes.ContainsKey("Bearer"))
                {
                    document.Components.SecuritySchemes["Bearer"] = new OpenApiSecurityScheme
                    {
                        Type = SecuritySchemeType.Http,
                        Scheme = "bearer",
                        BearerFormat = "JWT",
                        Description = "JWT bearer with tid (tenant), sub (user), roles[] claims.",
                    };
                }

                if (document.Security is null)
                {
                    document.Security = new List<OpenApiSecurityRequirement>();
                }

                if (document.Security.Count == 0)
                {
                    var requirement = new OpenApiSecurityRequirement();
                    var reference = new OpenApiSecuritySchemeReference("Bearer", document);
                    requirement.Add(reference, []);
                    document.Security.Add(requirement);
                }

                if (document.Paths is not null)
                {
                    foreach (var pathEntry in document.Paths)
                    {
                        var path = pathEntry.Value;
                        if (path?.Operations is null)
                        {
                            continue;
                        }

                        if (IsIdempotencyExempt(pathEntry.Key))
                        {
                            continue;
                        }

                        foreach (var entry in path.Operations)
                        {
                            var method = entry.Key.ToString();
                            if (!string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase)
                                && !string.Equals(method, "PUT", StringComparison.OrdinalIgnoreCase)
                                && !string.Equals(method, "PATCH", StringComparison.OrdinalIgnoreCase)
                                && !string.Equals(method, "DELETE", StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }

                            var operation = entry.Value;
                            if (operation is null)
                            {
                                continue;
                            }

                            if (operation.Parameters is null)
                            {
                                operation.Parameters = new List<IOpenApiParameter>();
                            }

                            var hasKey = false;
                            foreach (var parameter in operation.Parameters)
                            {
                                if (parameter is not null
                                    && string.Equals(parameter.Name, "Idempotency-Key", StringComparison.OrdinalIgnoreCase))
                                {
                                    hasKey = true;
                                    break;
                                }
                            }

                            if (!hasKey)
                            {
                                operation.Parameters.Add(new OpenApiParameter
                                {
                                    Name = "Idempotency-Key",
                                    In = ParameterLocation.Header,
                                    Required = false,
                                    Description = "Idempotency key for safe retries. Same key+body replays; same key+different body returns 409 CONFLICT.",
                                    Schema = new OpenApiSchema
                                    {
                                        Type = JsonSchemaType.String,
                                    },
                                });
                            }
                        }
                    }
                }

                EnsureSchema(document, "ErrorResponse");
                EnsureSchema(document, "PaginatedResult");
                EnsureSchema(document, "ProjectListResponse");
                EnsureSchema(document, "DashboardSummaryResponse");
                EnsureSchema(document, "ProcessingRunListResponse");
                EnsureSchema(document, "WorkspaceDto");
                EnsureSchema(document, "ProgressResponse");

                return Task.CompletedTask;
            });
        });
    }

    /// <summary>
    /// Auth and identity routes never take Idempotency-Key (see class doc).
    /// </summary>
    private static bool IsIdempotencyExempt(string path)
    {
        return path.StartsWith("/api/v1/auth", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/api/v1/me", StringComparison.OrdinalIgnoreCase);
    }

    private static void EnsureSchema(OpenApiDocument document, string name)
    {
        if (document.Components is null)
        {
            document.Components = new OpenApiComponents();
        }

        if (document.Components.Schemas is null)
        {
            document.Components.Schemas = new Dictionary<string, IOpenApiSchema>(StringComparer.Ordinal);
        }

        if (!document.Components.Schemas.ContainsKey(name))
        {
            var description = name switch
            {
                "ErrorResponse" => "Structured error envelope: { error: { code, message, correlationId, details } }.",
                "ProjectListResponse" => "Project list envelope: { items, page, pageSize, total, sort, sortDir, hasMore, clamped }.",
                "DashboardSummaryResponse" => "Dashboard summary: { projectCounts, recentOutputs[<=5], storage, cost, quota, warnings[], backlog }.",
                "ProcessingRunListResponse" => "Processing run list: { items[{ runId, status, retryOfRunId? }], page, pageSize, total, hasMore } (202 start, 200 replay + Idempotent-Replayed:true).",
                "WorkspaceDto" => "Workspace aggregate: { project, media, run, phase, stage, progress{percentApproximate}, review, warnings[], output, cost, activity[<=10], permissions }.",
                "ProgressResponse" => "Progress: { percentApproximate (display-only), currentStage, ... } plus SSE text/event-stream.",
                _ => "Pagination envelope: { items, page, pageSize, total, hasMore }.",
            };
            document.Components.Schemas[name] = new OpenApiSchema
            {
                Type = JsonSchemaType.Object,
                Description = description,
            };
        }
    }
}
