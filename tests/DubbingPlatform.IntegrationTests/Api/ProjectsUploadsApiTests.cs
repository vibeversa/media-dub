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
using Testcontainers.Minio;
using Testcontainers.PostgreSql;

namespace DubbingPlatform.IntegrationTests.Api;

/// <summary>
/// Task 018: project CRUD plus multipart upload sessions over PG + MinIO.
/// Skips when Docker is unavailable (CI runs live).
/// </summary>
public sealed class ProjectsUploadsApiTests
{
    private const string TestSigningKey = "test-signing-key-0123456789abcdef-test-signing-key-01";

    private const string TestAudience = "dubbing-api";

    private const string TestBucket = "dubbing-test";

    private const long PartSizeBytes = 8L * 1024L * 1024L;

    private static WebApplicationFactory<CorrelationIdMiddleware> CreateFactory(
        string connectionString,
        string? storageEndpoint = null,
        string? accessKey = null,
        string? secretKey = null)
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
                    ["Storage:Bucket"] = TestBucket,
                    ["Storage:UseSsl"] = "false",
                };
                if (storageEndpoint is not null)
                {
                    values["Storage:Endpoint"] = storageEndpoint;
                }

                if (accessKey is not null)
                {
                    values["Storage:AccessKey"] = accessKey;
                }

                if (secretKey is not null)
                {
                    values["Storage:SecretKey"] = secretKey;
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

    private static async Task<MinioContainer> StartMinioAsync()
    {
        try
        {
            var container = new MinioBuilder("quay.io/minio/minio:RELEASE.2024-02-06T21-36-22Z")
                .WithUsername("minioadmin")
                .WithPassword("minioadmin")
                .Build();
            await container.StartAsync().ConfigureAwait(true);
            return container;
        }
#pragma warning disable CA1031 // Docker availability probe: any failure means skip.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Skip.If(true, $"Docker/MinIO unavailable: {ex.Message}");
            throw new InvalidOperationException("Unreachable: Skip.If always throws.", ex);
        }
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

    private static async Task EnsureBucketAsync(MinioContainer minio)
    {
        var endpoint = string.Concat("http://", minio.Hostname, ":", minio.GetMappedPublicPort(9000).ToString(System.Globalization.CultureInfo.InvariantCulture));
        var config = new Amazon.S3.AmazonS3Config
        {
            ServiceURL = endpoint,
            ForcePathStyle = true,
        };
        using var client = new Amazon.S3.AmazonS3Client(minio.GetAccessKey(), minio.GetSecretKey(), config);
        var listed = await client.ListBucketsAsync().ConfigureAwait(true);
        if (!listed.Buckets.Any(b => string.Equals(b.BucketName, TestBucket, StringComparison.Ordinal)))
        {
            await client.PutBucketAsync(TestBucket, CancellationToken.None).ConfigureAwait(true);
        }
    }

    private static string MinioEndpoint(MinioContainer minio)
    {
        return string.Concat(minio.Hostname, ":", minio.GetMappedPublicPort(9000).ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private static async Task<string> CreateProjectAsync(HttpClient client, object body, string? idempotencyKey = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/projects")
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Add("Idempotency-Key", idempotencyKey ?? Guid.NewGuid().ToString("N"));
        using var response = await client.SendAsync(request).ConfigureAwait(true);
        var payload = await response.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var document = JsonDocument.Parse(payload);
        return document.RootElement.GetProperty("id").GetString()!;
    }

    private static async Task<string> CreateUploadAsync(HttpClient client, string projectId, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/projects/{projectId}/uploads")
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        using var response = await client.SendAsync(request).ConfigureAwait(true);
        var payload = await response.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var document = JsonDocument.Parse(payload);
        return document.RootElement.GetProperty("uploadId").GetString()!;
    }

    [SkippableFact]
    public async Task Create_Project_201()
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

            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/projects")
            {
                Content = JsonContent.Create(new { sourceLanguage = "en", targetLanguage = "es", settings = new { } }),
            };
            request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
            using var response = await client.SendAsync(request).ConfigureAwait(true);

            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            Assert.True(response.Headers.Contains(CorrelationIdMiddleware.HeaderName));
            var payload = await response.Content.ReadAsStringAsync().ConfigureAwait(true);
            using var document = JsonDocument.Parse(payload);
            var id = document.RootElement.GetProperty("id").GetString()!;
            Assert.StartsWith("prj_", id, StringComparison.Ordinal);
            Assert.Equal("Created", document.RootElement.GetProperty("status").GetString());
        }
    }

    [SkippableFact]
    public async Task Create_Invalid_Language_400()
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

            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/projects")
            {
                Content = JsonContent.Create(new { sourceLanguage = "en", targetLanguage = "en" }),
            };
            request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
            using var response = await client.SendAsync(request).ConfigureAwait(true);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var payload = await response.Content.ReadAsStringAsync().ConfigureAwait(true);
            using var document = JsonDocument.Parse(payload);
            Assert.Equal("VALIDATION_FAILED", document.RootElement.GetProperty("error").GetProperty("code").GetString());
        }
    }

    [SkippableFact]
    public async Task Upload_Create_Part_Complete_Flow()
    {
        var pg = await StartPostgresAsync().ConfigureAwait(true);
        await using (pg.ConfigureAwait(true))
        {
            var minio = await StartMinioAsync().ConfigureAwait(true);
            await using (minio.ConfigureAwait(true))
            {
                var connectionString = pg.GetConnectionString();
                await MigrateAsync(connectionString).ConfigureAwait(true);
                await EnsureBucketAsync(minio).ConfigureAwait(true);
                var tenantId = Guid.NewGuid();
                await SeedTenantAsync(connectionString, tenantId).ConfigureAwait(true);

                using var factory = CreateFactory(connectionString, MinioEndpoint(minio), minio.GetAccessKey(), minio.GetSecretKey());
                using var client = factory.CreateClient();
                UseToken(client, tenantId, "TenantAdmin");

                var projectId = await CreateProjectAsync(
                    client, new { sourceLanguage = "en", targetLanguage = "es" }).ConfigureAwait(true);

                var uploadId = await CreateUploadAsync(
                    client, projectId,
                    new { fileName = "test.mp4", contentType = "video/mp4", declaredSize = 1048576 }).ConfigureAwait(true);
                Assert.StartsWith("upl_", uploadId, StringComparison.Ordinal);

                using var partsRequest = new HttpRequestMessage(
                    HttpMethod.Post, $"/api/v1/projects/{projectId}/uploads/{uploadId}/parts")
                {
                    Content = JsonContent.Create(new { partNumbers = new[] { 1 } }),
                };
                partsRequest.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
                using var partsResponse = await client.SendAsync(partsRequest).ConfigureAwait(true);
                Assert.Equal(HttpStatusCode.OK, partsResponse.StatusCode);
                var partsPayload = await partsResponse.Content.ReadAsStringAsync().ConfigureAwait(true);
                using var partsDocument = JsonDocument.Parse(partsPayload);
                var url = partsDocument.RootElement.GetProperty("urls").GetProperty("1").GetString()!;
                Assert.StartsWith("http", url, StringComparison.OrdinalIgnoreCase);

                var bytes = new byte[262144];
                new Random(42).NextBytes(bytes);
                using var putContent = new ByteArrayContent(bytes);
                putContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("video/mp4");
                using var putResponse = await client.PutAsync(url, putContent).ConfigureAwait(true);
                Assert.True(putResponse.IsSuccessStatusCode, $"PUT part failed: {(int)putResponse.StatusCode} {await putResponse.Content.ReadAsStringAsync().ConfigureAwait(true)}");

                using var completeRequest = new HttpRequestMessage(
                    HttpMethod.Post, $"/api/v1/projects/{projectId}/uploads/{uploadId}/complete");
                completeRequest.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
                using var completeResponse = await client.SendAsync(completeRequest).ConfigureAwait(true);
                var completePayload = await completeResponse.Content.ReadAsStringAsync().ConfigureAwait(true);
                Assert.Equal(HttpStatusCode.OK, completeResponse.StatusCode);
                using var completeDocument = JsonDocument.Parse(completePayload);
                Assert.Equal("Completed", completeDocument.RootElement.GetProperty("status").GetString());

                using var statusResponse = await client.GetAsync(
                    $"/api/v1/projects/{projectId}/uploads/{uploadId}").ConfigureAwait(true);
                Assert.Equal(HttpStatusCode.OK, statusResponse.StatusCode);
                var statusPayload = await statusResponse.Content.ReadAsStringAsync().ConfigureAwait(true);
                using var statusDocument = JsonDocument.Parse(statusPayload);
                Assert.Equal("Completed", statusDocument.RootElement.GetProperty("status").GetString());
            }
        }
    }

    [SkippableFact]
    public async Task Incomplete_Complete_400()
    {
        var pg = await StartPostgresAsync().ConfigureAwait(true);
        await using (pg.ConfigureAwait(true))
        {
            var minio = await StartMinioAsync().ConfigureAwait(true);
            await using (minio.ConfigureAwait(true))
            {
                var connectionString = pg.GetConnectionString();
                await MigrateAsync(connectionString).ConfigureAwait(true);
                await EnsureBucketAsync(minio).ConfigureAwait(true);
                var tenantId = Guid.NewGuid();
                await SeedTenantAsync(connectionString, tenantId).ConfigureAwait(true);

                using var factory = CreateFactory(connectionString, MinioEndpoint(minio), minio.GetAccessKey(), minio.GetSecretKey());
                using var client = factory.CreateClient();
                UseToken(client, tenantId, "TenantAdmin");

                var projectId = await CreateProjectAsync(
                    client, new { sourceLanguage = "en", targetLanguage = "es" }).ConfigureAwait(true);
                var uploadId = await CreateUploadAsync(
                    client, projectId,
                    new { fileName = "clip.mp4", contentType = "video/mp4", declaredSize = 1048576 }).ConfigureAwait(true);

                using var completeRequest = new HttpRequestMessage(
                    HttpMethod.Post, $"/api/v1/projects/{projectId}/uploads/{uploadId}/complete");
                completeRequest.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
                using var completeResponse = await client.SendAsync(completeRequest).ConfigureAwait(true);

                Assert.Equal(HttpStatusCode.BadRequest, completeResponse.StatusCode);
                var payload = await completeResponse.Content.ReadAsStringAsync().ConfigureAwait(true);
                using var document = JsonDocument.Parse(payload);
                Assert.Equal("UPLOAD_INCOMPLETE", document.RootElement.GetProperty("error").GetProperty("code").GetString());
            }
        }
    }

    [SkippableFact]
    public async Task Cross_Tenant_403()
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
            var projectId = await CreateProjectAsync(
                clientA, new { sourceLanguage = "en", targetLanguage = "es" }).ConfigureAwait(true);

            using var clientB = factory.CreateClient();
            UseToken(clientB, tenantB, "ProjectViewer");
            using var response = await clientB.GetAsync($"/api/v1/projects/{projectId}").ConfigureAwait(true);

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            var payload = await response.Content.ReadAsStringAsync().ConfigureAwait(true);
            using var document = JsonDocument.Parse(payload);
            Assert.Equal("FORBIDDEN", document.RootElement.GetProperty("error").GetProperty("code").GetString());
        }
    }

    [SkippableFact]
    public async Task Idempotent_Create_Same_Response()
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

            async Task<string> PostOnceAsync()
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/projects")
                {
                    Content = JsonContent.Create(body),
                };
                request.Headers.Add("Idempotency-Key", key);
                using var response = await client.SendAsync(request).ConfigureAwait(true);
                var payload = await response.Content.ReadAsStringAsync().ConfigureAwait(true);
                Assert.Equal(HttpStatusCode.Created, response.StatusCode);
                using var document = JsonDocument.Parse(payload);
                return document.RootElement.GetProperty("id").GetString()!;
            }

            var first = await PostOnceAsync().ConfigureAwait(true);
            var second = await PostOnceAsync().ConfigureAwait(true);
            Assert.Equal(first, second);
        }
    }
}
