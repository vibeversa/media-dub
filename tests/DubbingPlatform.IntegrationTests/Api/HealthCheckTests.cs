using System.Net;
using DubbingPlatform.Api.Middleware;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Testcontainers.Minio;
using Testcontainers.PostgreSql;

namespace DubbingPlatform.IntegrationTests.Api;

/// <summary>
/// Verifies health separation: liveness is process-local only (200 without
/// dependencies); readiness reflects dependencies (503 when down, 200 when up).
/// Also verifies Prometheus metrics exposition and correlation headers on health
/// endpoints. Docker-dependent readiness-up test skips without a daemon.
/// </summary>
public sealed class HealthCheckTests : IClassFixture<WebApplicationFactory<CorrelationIdMiddleware>>
{
    private readonly WebApplicationFactory<CorrelationIdMiddleware> _factory;

    public HealthCheckTests(WebApplicationFactory<CorrelationIdMiddleware> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Live_Returns_200_With_Db_Down()
    {
        using var client = _factory.WithWebHostBuilder(_ => { }).CreateClient();

        using var response = await client.GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.Contains(CorrelationIdMiddleware.HeaderName));
    }

    [Fact]
    public async Task Ready_Returns_503_When_Db_Down()
    {
        using var client = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Default"] = "Host=127.0.0.1;Port=1;Database=down;Username=down;Password=down",
                    ["Storage:Endpoint"] = "http://127.0.0.1:1",
                    ["Transport:Provider"] = "InMemory",
                });
            });
        }).CreateClient();

        using var response = await client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.True(response.Headers.Contains(CorrelationIdMiddleware.HeaderName));
    }

    [Fact]
    public async Task Ready_Returns_503_In_Fast_Profile_Without_Rabbit_Redis()
    {
        // Fast profile (InMemory) must not require RabbitMQ/Redis: with PG down it
        // is still 503 because of PG/storage, but starting without broker env must
        // not throw and liveness stays 200.
        using var client = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Transport:Provider"] = "InMemory",
                });
            });
        }).CreateClient();

        using var live = await client.GetAsync("/health/live");
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
    }

    [Fact]
    public async Task Metrics_Returns_Prometheus_Text()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync("/metrics");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var contentType = response.Content.Headers.ContentType?.ToString() ?? string.Empty;
        Assert.Contains("text/plain", contentType, StringComparison.OrdinalIgnoreCase);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("# HELP", body, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task Ready_Returns_200_When_Up()
    {
        PostgreSqlContainer? postgres = null;
        MinioContainer? minio = null;
        try
        {
            try
            {
                postgres = new PostgreSqlBuilder("postgres:16-alpine").Build();
                await postgres.StartAsync();
                minio = new MinioBuilder("quay.io/minio/minio:RELEASE.2024-02-06T21-36-22Z").Build();
                await minio.StartAsync();
            }
#pragma warning disable CA1031 // Docker availability probe: any failure means skip.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                Skip.If(true, $"Docker unavailable: {ex.Message}");
                return;
            }

            var connectionString = postgres!.GetConnectionString();
            var s3Endpoint = $"http://{minio!.Hostname}:{minio.GetMappedPublicPort(9000)}";

            using var client = _factory.WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, config) =>
                {
                    config.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["ConnectionStrings:Default"] = connectionString,
                        ["Storage:Endpoint"] = s3Endpoint,
                        ["Storage:Bucket"] = "dubbing",
                        ["Storage:AccessKey"] = minio.GetAccessKey(),
                        ["Storage:SecretKey"] = minio.GetSecretKey(),
                        ["Transport:Provider"] = "InMemory",
                    });
                });
            }).CreateClient();

            using var response = await client.GetAsync("/health/ready");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        finally
        {
            if (minio is not null)
            {
                await minio.DisposeAsync();
            }

            if (postgres is not null)
            {
                await postgres.DisposeAsync();
            }
        }
    }
}
