using System.Data.Common;
using System.Net.Sockets;
using DubbingPlatform.Application.MultiTenancy;
using DubbingPlatform.Application.Options;
using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Infrastructure.Persistence;
using DubbingPlatform.Infrastructure.Persistence.Interceptors;
using DubbingPlatform.Infrastructure.Processes;
using EFCore.NamingConventions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Testcontainers.Minio;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;
using Testcontainers.Redis;
using WireMock.Server;
using Xunit.Abstractions;

namespace DubbingPlatform.IntegrationTests.Fixtures;

/// <summary>
/// Task 39: uniform wiring for all test tiers. Backing services
/// (PostgreSQL 16, RabbitMQ 3.13, Redis 7, MinIO) run in Testcontainers with
/// the same images as <c>docker-compose.yml</c>; provider adapters are stubbed
/// with WireMock; deterministic CI uses the Mock providers by default.
/// Every container starter skips with an explicit message when Docker is
/// unavailable (fast local runs); CI runs the same tests live.
/// </summary>
public abstract class TestFixtureBase
{
    protected static async Task<PostgreSqlContainer> StartPostgresAsync(ITestOutputHelper? output = null)
    {
        try
        {
            var container = new PostgreSqlBuilder("postgres:16-alpine").Build();
            await container.StartAsync().ConfigureAwait(true);
            return container;
        }
#pragma warning disable CA1031 // Docker availability probe: any failure means skip.
        catch (Exception ex) when (IsInfrastructureUnavailable(ex))
#pragma warning restore CA1031
        {
            output?.WriteLine($"Docker/PostgreSQL unavailable, skipping: {ex.Message}");
            Skip.If(true, $"Docker/PostgreSQL unavailable: {ex.Message}");
            throw new InvalidOperationException("Unreachable: Skip.If always throws.", ex);
        }
    }

    protected static async Task<RabbitMqContainer> StartRabbitMqAsync(ITestOutputHelper? output = null)
    {
        try
        {
            var container = new RabbitMqBuilder("rabbitmq:3.13-management").Build();
            await container.StartAsync().ConfigureAwait(true);
            return container;
        }
#pragma warning disable CA1031 // Docker availability probe: any failure means skip.
        catch (Exception ex) when (IsInfrastructureUnavailable(ex))
#pragma warning restore CA1031
        {
            output?.WriteLine($"Docker/RabbitMQ unavailable, skipping: {ex.Message}");
            Skip.If(true, $"Docker/RabbitMQ unavailable: {ex.Message}");
            throw new InvalidOperationException("Unreachable: Skip.If always throws.", ex);
        }
    }

    protected static async Task<RedisContainer> StartRedisAsync(ITestOutputHelper? output = null)
    {
        try
        {
            var container = new RedisBuilder("redis:7").Build();
            await container.StartAsync().ConfigureAwait(true);
            return container;
        }
#pragma warning disable CA1031 // Docker availability probe: any failure means skip.
        catch (Exception ex) when (IsInfrastructureUnavailable(ex))
#pragma warning restore CA1031
        {
            output?.WriteLine($"Docker/Redis unavailable, skipping: {ex.Message}");
            Skip.If(true, $"Docker/Redis unavailable: {ex.Message}");
            throw new InvalidOperationException("Unreachable: Skip.If always throws.", ex);
        }
    }

    protected static async Task<MinioContainer> StartMinioAsync(ITestOutputHelper? output = null)
    {
        try
        {
            var container = new MinioBuilder("quay.io/minio/minio:RELEASE.2024-02-06T21-36-22Z").Build();
            await container.StartAsync().ConfigureAwait(true);
            return container;
        }
#pragma warning disable CA1031 // Docker availability probe: any failure means skip.
        catch (Exception ex) when (IsInfrastructureUnavailable(ex))
#pragma warning restore CA1031
        {
            output?.WriteLine($"Docker/MinIO unavailable, skipping: {ex.Message}");
            Skip.If(true, $"Docker/MinIO unavailable: {ex.Message}");
            throw new InvalidOperationException("Unreachable: Skip.If always throws.", ex);
        }
    }

    /// <summary>
    /// Starts a WireMock stub server for provider-adapter contracts on a
    /// dynamic loopback port. Never requires Docker; dispose after each test.
    /// </summary>
    protected static WireMockServer StartWireMock()
    {
        return WireMockServer.Start();
    }

    /// <summary>
    /// Default deterministic mock behavior for CI. Every capability resolves
    /// to <paramref name="scenario"/> unless overridden per capability.
    /// </summary>
    protected static Microsoft.Extensions.Options.IOptions<MockBehaviorOptions> MockOptions(string scenario)
    {
        return Microsoft.Extensions.Options.Options.Create(new MockBehaviorOptions { Scenario = scenario });
    }

    protected static Microsoft.Extensions.Options.IOptions<MockBehaviorOptions> MockSuccessOptions()
    {
        return MockOptions(MockBehaviorOptions.Success);
    }

    /// <summary>
    /// Resolves the repo-level <c>fixtures/</c> directory by walking up from
    /// the test binary directory. Never depends on the current working dir.
    /// Matches the name ordinally so the <c>Fixtures/</c> test-source folders
    /// (capital F) never shadow the lowercase media folder on Windows.
    /// </summary>
    protected static string FixtureDirectory()
    {
        string? fallback = null;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string[] subdirs;
            try
            {
                subdirs = Directory.GetDirectories(dir.FullName);
            }
#pragma warning disable CA1031 // Directory probe: unreadable levels are skipped.
            catch (Exception)
#pragma warning restore CA1031
            {
                continue;
            }

            foreach (var sub in subdirs)
            {
                var name = Path.GetFileName(sub);
                if (string.Equals(name, "fixtures", StringComparison.Ordinal))
                {
                    return sub;
                }

                if (fallback is null && string.Equals(name, "fixtures", StringComparison.OrdinalIgnoreCase))
                {
                    fallback = sub;
                }
            }
        }

        if (fallback is not null)
        {
            return fallback;
        }

        throw new InvalidOperationException("fixtures/ directory was not found above " + AppContext.BaseDirectory);
    }

    protected static string FixturePath(string fileName)
    {
        return Path.Combine(FixtureDirectory(), fileName);
    }

    /// <summary>
    /// Returns the fixture path, or skips with an explicit message when the
    /// fixture file is absent (run <c>scripts/generate-fixtures.sh</c>).
    /// </summary>
    protected static string RequireFixture(string fileName, ITestOutputHelper? output = null)
    {
        var path = FixturePath(fileName);
        if (!File.Exists(path))
        {
            output?.WriteLine($"Fixture '{fileName}' missing, skipping (run scripts/generate-fixtures.sh).");
            Skip.If(true, $"Fixture '{fileName}' missing. Run scripts/generate-fixtures.sh to generate it.");
        }

        return path;
    }

    protected static async Task<bool> FfmpegAvailableAsync()
    {
        try
        {
            var runner = new ProcessRunner(NullLogger<ProcessRunner>.Instance);
            var workDir = ProcessRunner.CreateTempWorkingDir();
            try
            {
                var result = await runner.RunAsync(
                    "ffprobe",
                    ["-version"],
                    workDir,
                    TimeSpan.FromSeconds(15),
                    CancellationToken.None).ConfigureAwait(true);
                return !result.TimedOut && result.ExitCode == 0;
            }
            finally
            {
                DeleteDirQuietly(workDir);
            }
        }
#pragma warning disable CA1031 // Availability probe: any failure means unavailable.
        catch (Exception)
#pragma warning restore CA1031
        {
            return false;
        }
    }

    protected static async Task SkipUnlessFfmpegAsync(ITestOutputHelper? output = null)
    {
        if (!await FfmpegAvailableAsync().ConfigureAwait(true))
        {
            output?.WriteLine("ffmpeg/ffprobe missing, skipping (install ffmpeg to run media tests).");
            Skip.If(true, "ffmpeg/ffprobe missing. Install ffmpeg to run media tests.");
        }
    }

    protected static DbContextOptions<AppDbContext> CreatePgOptions(string connectionString)
    {
        return new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention()
            .AddInterceptors(new TenantSessionInterceptor())
            .Options;
    }

    protected static DbContextOptions<AppDbContext> CreatePgOptions(PostgreSqlContainer container)
    {
        return CreatePgOptions(container.GetConnectionString());
    }

    protected static async Task MigrateAsync(string connectionString)
    {
        using (TenantContext.BeginMaintenanceScope())
        {
            var options = CreatePgOptions(connectionString);
            using var db = new AppDbContext(options);
            await db.Database.MigrateAsync().ConfigureAwait(true);
        }
    }

    protected static async Task SeedTenantAsync(string connectionString, Guid tenantId)
    {
        using (TenantContext.BeginScope(tenantId))
        {
            var options = CreatePgOptions(connectionString);
            using var db = new AppDbContext(options);
            var exists = await db.Tenants.AnyAsync(t => t.Id == tenantId).ConfigureAwait(true);
            if (!exists)
            {
                db.Tenants.Add(new Tenant(tenantId, $"Tenant {tenantId:N}", $"tenant-{tenantId:N}", DateTimeOffset.UtcNow));
                await db.SaveChangesAsync().ConfigureAwait(true);
            }
        }
    }

    protected static bool IsInfrastructureUnavailable(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            var name = current.GetType().FullName ?? string.Empty;
            if (name.Contains("Docker", StringComparison.Ordinal) ||
                name.Contains("Testcontainers", StringComparison.Ordinal))
            {
                return true;
            }

            if (current is HttpRequestException
                or TimeoutException
                or ObjectDisposedException
                or UnauthorizedAccessException
                or IOException
                or SocketException
                or DbException
                or InvalidOperationException)
            {
                return true;
            }
        }

        return false;
    }

    protected static void DeleteDirQuietly(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch (Exception)
        {
            // Best effort.
        }
    }
}
