using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace DubbingPlatform.Api.OpenApi;

/// <summary>
/// Enriches the <c>v1</c> OpenAPI document with the platform conventions:
/// JWT bearer security scheme, global <c>Idempotency-Key</c> header on mutations,
/// and document metadata. Error-envelope (<c>ErrorResponse</c>) and pagination
/// (<c>PaginatedResult</c>) schemas are emitted from controller
/// <c>ProducesResponseType</c> references; this transformer only adds the
/// cross-cutting security and header contracts.
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
                document.Info.Description = "Tenant-scoped dubbing pipeline. Auth: JWT bearer (tid/sub/roles). Mutations accept Idempotency-Key. Errors use { error: { code, message, correlationId, details } }. Lists use { items, page, pageSize, total, hasMore }.";

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
                    foreach (var path in document.Paths.Values)
                    {
                        if (path?.Operations is null)
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

                return Task.CompletedTask;
            });
        });
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
            document.Components.Schemas[name] = new OpenApiSchema
            {
                Type = JsonSchemaType.Object,
                Description = string.Equals(name, "ErrorResponse", StringComparison.Ordinal)
                    ? "Structured error envelope: { error: { code, message, correlationId, details } }."
                    : "Pagination envelope: { items, page, pageSize, total, hasMore }.",
            };
        }
    }
}
