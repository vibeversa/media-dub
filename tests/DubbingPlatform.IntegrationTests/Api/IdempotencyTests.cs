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
using DubbingPlatform.Infrastructure.Persistence;
using DubbingPlatform.Infrastructure.Persistence.Interceptors;
using EFCore.NamingConventions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using Testcontainers.PostgreSql;

namespace DubbingPlatform.IntegrationTests.Api;

/// <summary>
/// API foundation tests (task 017): race-safe idempotency over
/// <c>POST /api/v1/projects</c> (7-day retention), JWT 401, cross-tenant 403,
/// and OpenAPI completeness. PG-backed tests skip without Docker; auth-shape
/// and OpenAPI tests run hermetically.
/// </summary>
public sealed class IdempotencyTests
{
    private const string TestSigningKey = "test-signing-key-0123456789abcdef-test-signing-key-01";

    private const string TestAudience = "dubbing-api";

    private static WebApplicationFactory<CorrelationIdMiddleware> CreateFactory(string? connectionString)
    {
        var factory = new WebApplicationFactory<CorrelationIdMiddleware>();
        if (connectionString is null)
        {
            return factory;
        }

        return factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Default"] = connectionString,
                    ["Auth:SigningKey"] = TestSigningKey,
                    ["Auth:Audience"] = TestAudience,
                    ["Auth:RequireHttps"] = "false",
                    ["Transport:Provider"] = "InMemory",
                });
            });
        });
    }

    private static WebApplicationFactory<CorrelationIdMiddleware> CreateHermeticFactory()
    {
        var factory = new WebApplicationFactory<CorrelationIdMiddleware>();
        return factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Auth:SigningKey"] = TestSigningKey,
                    ["Auth:Audience"] = TestAudience,
                    ["Auth:RequireHttps"] = "false",
                    ["Transport:Provider"] = "InMemory",
                });
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

    private static DbContextOptions<AppDbContext> CreateOptions(string connectionString)
    {
        return new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention()
            .AddInterceptors(new TenantSessionInterceptor())
            .Options;
    }

    private static async Task<PostgreSqlContainer> StartPostgresAsync()
    {
        PostgreSqlContainer container;
        try
        {
            container = new PostgreSqlBuilder("postgres:16-alpine").Build();
            await container.StartAsync().ConfigureAwait(true);
        }
#pragma warning disable CA1031 // Docker availability probe: any failure means skip.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Skip.If(true, $"Docker/PostgreSQL unavailable: {ex.Message}");
            throw new InvalidOperationException("Unreachable: Skip.If always throws.", ex);
        }

        return container;
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

    private static async Task<long> CountProjectsAsync(string connectionString, Guid tenantId)
    {
        using (TenantContext.BeginScope(tenantId))
        {
            var options = CreateOptions(connectionString);
            using var context = new AppDbContext(options);
            return await context.DubbingProjects.LongCountAsync().ConfigureAwait(true);
        }
    }

    private static async Task<string> CreateProjectAsync(HttpClient client, object body, string idempotencyKey)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/projects")
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        using var response = await client.SendAsync(request).ConfigureAwait(true);
        var payload = await response.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var document = JsonDocument.Parse(payload);
        return document.RootElement.GetProperty("id").GetString()!;
    }

    [SkippableFact]
    public async Task Duplicate_Returns_Same_Response()
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
            var key = Guid.NewGuid().ToString("N");
            var body = new { sourceLanguage = "en", targetLanguage = "es" };

            var firstId = await CreateProjectAsync(client, body, key).ConfigureAwait(true);
            var secondId = await CreateProjectAsync(client, body, key).ConfigureAwait(true);

            Assert.Equal(firstId, secondId);
            Assert.Equal(1, await CountProjectsAsync(connectionString, tenantId).ConfigureAwait(true));
        }
    }

    [SkippableFact]
    public async Task Mismatch_Returns_409()
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
            var key = Guid.NewGuid().ToString("N");

            _ = await CreateProjectAsync(client, new { sourceLanguage = "en", targetLanguage = "es" }, key).ConfigureAwait(true);

            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/projects")
            {
                Content = JsonContent.Create(new { sourceLanguage = "en", targetLanguage = "fr" }),
            };
            request.Headers.Add("Idempotency-Key", key);
            using var response = await client.SendAsync(request).ConfigureAwait(true);

            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            var payload = await response.Content.ReadAsStringAsync().ConfigureAwait(true);
            using var document = JsonDocument.Parse(payload);
            Assert.Equal("CONFLICT", document.RootElement.GetProperty("error").GetProperty("code").GetString());
        }
    }

    [SkippableFact]
    public async Task Concurrent_Duplicates_Single_Row()
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
            var token = CreateToken(tenantId, "TenantAdmin");
            var key = Guid.NewGuid().ToString("N");

            var tasks = Enumerable.Range(0, 10).Select(async _ =>
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/projects")
                {
                    Content = JsonContent.Create(new { sourceLanguage = "en", targetLanguage = "es" }),
                };
                request.Headers.Add("Idempotency-Key", key);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                using var innerClient = factory.CreateClient();
                return await innerClient.SendAsync(request).ConfigureAwait(true);
            }).ToList();

            var responses = await Task.WhenAll(tasks).ConfigureAwait(true);
            var bodies = new List<string>();
            try
            {
                var succeeded = new List<string>();
                foreach (var response in responses)
                {
                    var body = await response.Content.ReadAsStringAsync().ConfigureAwait(true);
                    bodies.Add(body);
                    if (response.StatusCode == HttpStatusCode.Created)
                    {
                        using var document = JsonDocument.Parse(body);
                        succeeded.Add(document.RootElement.GetProperty("id").GetString()!);
                    }
                    else
                    {
                        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
                    }
                }

                Assert.NotEmpty(succeeded);
                Assert.All(succeeded, id => Assert.Equal(succeeded[0], id));
                Assert.Equal(1, await CountProjectsAsync(connectionString, tenantId).ConfigureAwait(true));
            }
            finally
            {
                foreach (var response in responses)
                {
                    response.Dispose();
                }
            }

            _ = client;
        }
    }

    [Fact]
    public async Task Unauthorized_401()
    {
        using var factory = CreateHermeticFactory();
        using var client = factory.CreateClient();

        using var response = await client.PostAsync(
            "/api/v1/projects",
            JsonContent.Create(new { sourceLanguage = "en", targetLanguage = "es" })).ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.True(response.Headers.Contains(CorrelationIdMiddleware.HeaderName));
        var payload = await response.Content.ReadAsStringAsync().ConfigureAwait(true);
        using var document = JsonDocument.Parse(payload);
        Assert.Equal("UNAUTHORIZED", document.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [SkippableFact]
    public async Task Forbidden_Tenant_403()
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
            var projectId = await CreateProjectAsync(clientA, new { sourceLanguage = "en", targetLanguage = "es" }, Guid.NewGuid().ToString("N")).ConfigureAwait(true);

            using var clientB = factory.CreateClient();
            UseToken(clientB, tenantB, "ProjectViewer");
            using var response = await clientB.GetAsync($"/api/v1/projects/{projectId}").ConfigureAwait(true);

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.True(response.Headers.Contains(CorrelationIdMiddleware.HeaderName));
            var payload = await response.Content.ReadAsStringAsync().ConfigureAwait(true);
            using var document = JsonDocument.Parse(payload);
            Assert.Equal("FORBIDDEN", document.RootElement.GetProperty("error").GetProperty("code").GetString());
        }
    }

    [Fact]
    public async Task OpenApi_Complete()
    {
        using var factory = CreateHermeticFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/openapi/v1.json").ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.Contains("/api/v1/projects", payload, StringComparison.Ordinal);
        Assert.Contains("Bearer", payload, StringComparison.Ordinal);
        Assert.Contains("Idempotency-Key", payload, StringComparison.Ordinal);
        Assert.Contains("ErrorResponse", payload, StringComparison.Ordinal);
        Assert.Contains("PaginatedResult", payload, StringComparison.Ordinal);
        Assert.Contains("hasMore", payload, StringComparison.OrdinalIgnoreCase);
    }
}
