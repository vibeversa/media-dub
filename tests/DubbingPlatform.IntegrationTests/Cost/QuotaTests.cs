using System.Data.Common;
using System.Net.Sockets;
using DubbingPlatform.Application.Errors;
using DubbingPlatform.Application.Exceptions;
using DubbingPlatform.Application.MultiTenancy;
using DubbingPlatform.Application.Options;
using DubbingPlatform.Application.Services;
using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Domain.Enums;
using DubbingPlatform.Domain.Exceptions;
using DubbingPlatform.Infrastructure.Persistence;
using DubbingPlatform.Infrastructure.Persistence.Interceptors;
using DubbingPlatform.Infrastructure.Redis;
using EFCore.NamingConventions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;
using Xunit.Abstractions;

namespace DubbingPlatform.IntegrationTests.Cost;

/// <summary>
/// Task 036: atomic reservations, quotas, Redis rate limits, fairness.
/// Hermetic <c>[Fact]</c> tests cover the pure planners (estimates, budgets,
/// dimensions, token-bucket math, fairness thresholds, in-memory limiter) and
/// run everywhere including CI without containers. Live
/// <c>[SkippableFact]</c> tests cover PG atomicity (20 parallel reserves) and
/// Redis dims; they skip without Docker and run live in CI.
/// </summary>
public sealed class QuotaTests
{
    private readonly ITestOutputHelper _output;

    public QuotaTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task Concurrent_Reservations_No_Overspend()
    {
        const int parallel = 20;
        const double perUnit = 0.01;
        const double cap = 0.05;
        var allowed = 0;
        var sum = 0.0;
        var gate = new object();
        var tasks = Enumerable.Range(0, parallel).Select(_ => Task.Run(() =>
        {
            lock (gate)
            {
                if (CostService.IsOverBudget(sum, perUnit, cap))
                {
                    return false;
                }

                sum = checked(sum + perUnit);
                allowed++;
                return true;
            }
        })).ToList();
        var results = await Task.WhenAll(tasks).ConfigureAwait(true);

        Assert.Equal(parallel, results.Length);
        Assert.Equal(5, allowed);
        Assert.True(sum <= cap);
        Assert.Equal(5, results.Count(r => r));
        Assert.Equal(15, results.Count(r => !r));
    }

    [Fact]
    public void Quota_Exceeded_Structured()
    {
        var quotaExceeded = new QuotaExceededException("dimension cost-per-project");
        Assert.Equal(ErrorCodes.QuotaExceeded, quotaExceeded.ErrorCode);
        Assert.Equal(429, quotaExceeded.StatusCode);
        Assert.Equal(429, ErrorCodes.StatusFor(ErrorCodes.QuotaExceeded));

        var rateLimited = new RateLimitedException("dimension requests");
        Assert.Equal(ErrorCodes.RateLimited, rateLimited.ErrorCode);
        Assert.Equal(429, rateLimited.StatusCode);

        Assert.True(QuotaService.IsCostExceeded(49.0, 2.0, 50.0));
        Assert.False(QuotaService.IsCostExceeded(40.0, 2.0, 50.0));
        Assert.True(QuotaService.IsStorageExceeded(100, 50, 120));
        Assert.False(QuotaService.IsStorageExceeded(100, 20, 120));
        Assert.Equal("cost-per-project", QuotaDimensions.Normalize("Cost-Per-Project"));
        Assert.Equal("storage", QuotaDimensions.Normalize("STORAGE"));
        Assert.Throws<DomainException>(() => QuotaDimensions.Normalize("nope"));
        Assert.Equal("quota.rejections", QuotaMeters.RejectionsMetricName);
    }

    [Fact]
    public async Task Rate_Dims_Enforced()
    {
        Assert.Equal("requests", RateLimiter.NormalizeDimension("Requests"));
        Assert.Equal("tokens", RateLimiter.NormalizeDimension("TOKENS"));
        Assert.Equal("chars", RateLimiter.NormalizeDimension("chars"));
        Assert.Equal("audioSecs", RateLimiter.NormalizeDimension("audioSeconds"));
        Assert.Equal("concurrency", RateLimiter.NormalizeDimension("Concurrency"));
        Assert.Throws<DomainException>(() => RateLimiter.NormalizeDimension("bandwidth"));

        var limits = new RateLimitOptions
        {
            RequestsPerMin = 2,
            TokensPerMin = 100,
            CharsPerMin = 100,
            AudioSecondsPerMin = 100,
            Concurrency = 2,
        };
        Assert.Equal(2, RateLimiter.LimitForDimension(limits, "requests"));
        Assert.Equal(100, RateLimiter.LimitForDimension(limits, "tokens"));
        Assert.True(RateLimiter.IsAllowed(1, 2, 1));
        Assert.False(RateLimiter.IsAllowed(2, 2, 1));

        var tenant = Guid.NewGuid();
        var limiter = new RateLimiter(
            null,
            Options.Create(limits),
            NullLogger<RateLimiter>.Instance);
        Assert.True(await limiter.TryAcquireAsync(tenant, "mock", "requests", 1).ConfigureAwait(true));
        Assert.True(await limiter.TryAcquireAsync(tenant, "mock", "requests", 1).ConfigureAwait(true));
        Assert.False(await limiter.TryAcquireAsync(tenant, "mock", "requests", 1).ConfigureAwait(true));
        Assert.Equal("ratelimit.rejections", RateLimitMeters.RejectionsMetricName);
        Assert.StartsWith(
            RateLimiter.KeyPrefix,
            RateLimiter.KeyFor(tenant, "Mock", "requests"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Fairness_Cap_Enforced()
    {
        Assert.True(TenantFairnessGate.IsDispatchAllowed(18, 2, 20));
        Assert.False(TenantFairnessGate.IsDispatchAllowed(19, 2, 20));
        Assert.False(TenantFairnessGate.IsDispatchAllowed(20, 1, 20));
        Assert.True(TenantFairnessGate.IsDispatchAllowed(0, 20, 20));
        Assert.Throws<DomainException>(() => TenantFairnessGate.IsDispatchAllowed(-1, 0, 20));

        var quota = new QuotaOptions { MaxConcurrentStagesPerTenant = 2 };
        Assert.Equal(2, quota.MaxConcurrentStagesPerTenant);
        var rates = new RateLimitOptions { Concurrency = 2 };
        Assert.Equal(2, rates.Concurrency);
    }

    [Fact]
    public void Records_Have_Estimate_Usage()
    {
        var table = PriceTable.Default();
        Assert.Equal(PriceTable.CurrentVersion, table.Version);
        Assert.Equal("1.0.0", table.Version);
        Assert.Equal(0.02, table.UnitPrices["transcription_per_min"]);
        Assert.Equal(0.00002, table.UnitPrices["translation_per_char"]);
        Assert.Equal(0.00003, table.UnitPrices["tts_per_char"]);
        Assert.Equal(0.05, table.UnitPrices["separation_per_min"]);
        Assert.Equal(0.01, table.UnitPrices["local_per_min"]);

        Assert.Equal(0.04, CostService.Estimate(ProviderCapability.Transcription, new CostUsageDims(2.0, 0, 0)), 6);
        Assert.Equal(0.02, CostService.Estimate(ProviderCapability.Translation, new CostUsageDims(0.0, 1000, 0)), 6);
        Assert.Equal(0.03, CostService.Estimate(ProviderCapability.Tts, new CostUsageDims(0.0, 1000, 0)), 6);
        Assert.Equal(0.10, CostService.Estimate(ProviderCapability.SourceSeparation, new CostUsageDims(2.0, 0, 0)), 6);
        Assert.Equal(0.02, CostService.Estimate(ProviderCapability.Vad, new CostUsageDims(2.0, 0, 0)), 6);

        var pipeline = CostService.EstimatePipeline(2.0, 10, 100);
        Assert.Equal(0.04 + 0.02 + 0.03, pipeline, 6);

        Assert.Throws<DomainException>(() => CostService.Estimate(ProviderCapability.Tts, new CostUsageDims(0.0, -1, 0)));
        Assert.Throws<DomainException>(() => CostService.EstimatePipeline(-1.0, 1, 1));
        Assert.True(CostService.IsOverBudget(49.0, 2.0, 50.0));
        Assert.False(CostService.IsOverBudget(40.0, 2.0, 50.0));

        Assert.Equal("cost.reserved", CostMeters.ReservedMetricName);
        Assert.Equal("cost.reconciled", CostMeters.ReconciledMetricName);
    }

    [SkippableFact]
    public async Task Live_Pg_Reservations_Serialize_No_Overspend()
    {
        var container = await StartPostgresAsync().ConfigureAwait(true);
        await using (container.ConfigureAwait(true))
        {
            var options = CreatePgOptions(container);
            await MigrateAsync(container).ConfigureAwait(true);

            var tenantId = Guid.NewGuid();
            var projectId = Guid.NewGuid();
            var runId = Guid.NewGuid();
            var quota = Options.Create(new QuotaOptions
            {
                MaxCostPerProject = 0.05,
                MaxCostPerSegment = 2.0,
            });
            var factory = new TestFactory(options);
            var costs = new CostService(factory, quota, NullLogger<CostService>.Instance);

            var successes = 0;
            var quotaHits = 0;
            var gate = new object();
            _ = gate;
            var tasks = Enumerable.Range(0, 20).Select(_ => Task.Run(async () =>
            {
                try
                {
                    var reservation = await costs.ReserveAsync(
                        tenantId, projectId, runId, Guid.NewGuid(),
                        ProviderCapability.Translation, 0.01).ConfigureAwait(true);
                    Interlocked.Increment(ref successes);
                    await costs.ReconcileAsync(tenantId, reservation.Id, 0.01, 0.01).ConfigureAwait(true);
                    return true;
                }
                catch (QuotaExceededException)
                {
                    Interlocked.Increment(ref quotaHits);
                    return false;
                }
            })).ToList();
            await Task.WhenAll(tasks).ConfigureAwait(true);

            Assert.True(successes >= 1);
            Assert.True(quotaHits >= 1);
            Assert.Equal(20, successes + quotaHits);

            using (TenantContext.BeginScope(tenantId))
            {
                using var db = new AppDbContext(options);
                var total = await db.Set<CostReservation>()
                    .Where(r => r.ProjectId == projectId && (r.State == "Reserved" || r.State == "Reconciled"))
                    .SumAsync(r => (double?)(r.State == "Reserved" ? r.ReservedAmount : r.ActualAmount)).ConfigureAwait(true) ?? 0.0;
                Assert.True(total <= 0.05 + 1e-9);
            }
        }
    }

    [SkippableFact]
    public async Task Live_Redis_Rate_Dims_Enforced()
    {
        var container = await StartRedisAsync().ConfigureAwait(true);
        await using (container.ConfigureAwait(true))
        {
            using var redis = await ConnectRedisAsync(container.GetConnectionString()).ConfigureAwait(true);
            var limiter = new RateLimiter(
                redis,
                Options.Create(new RateLimitOptions
                {
                    RequestsPerMin = 2,
                    TokensPerMin = 100,
                    CharsPerMin = 100,
                    AudioSecondsPerMin = 100,
                    Concurrency = 2,
                }),
                NullLogger<RateLimiter>.Instance);

            var tenant = Guid.NewGuid();
            Assert.True(await limiter.TryAcquireAsync(tenant, "mock", "requests", 1).ConfigureAwait(true));
            Assert.True(await limiter.TryAcquireAsync(tenant, "mock", "requests", 1).ConfigureAwait(true));
            Assert.False(await limiter.TryAcquireAsync(tenant, "mock", "requests", 1).ConfigureAwait(true));
        }
    }

    private static DbContextOptions<AppDbContext> CreatePgOptions(PostgreSqlContainer container)
    {
        return new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(container.GetConnectionString())
            .UseSnakeCaseNamingConvention()
            .AddInterceptors(new TenantSessionInterceptor())
            .Options;
    }

    private static async Task MigrateAsync(PostgreSqlContainer container)
    {
        using (TenantContext.BeginMaintenanceScope())
        {
            var options = CreatePgOptions(container);
            using var db = new AppDbContext(options);
            await db.Database.MigrateAsync().ConfigureAwait(true);
        }
    }

    private async Task<PostgreSqlContainer> StartPostgresAsync()
    {
        try
        {
            var container = new PostgreSqlBuilder("postgres:16-alpine").Build();
            await container.StartAsync().ConfigureAwait(true);
            return container;
        }
#pragma warning disable CA1031 // Docker availability probe: any transport-level failure means "skip".
        catch (Exception ex) when (IsInfrastructureUnavailable(ex))
#pragma warning restore CA1031
        {
            _output.WriteLine($"Docker/PostgreSQL unavailable, skipping quota test: {ex.Message}");
            Skip.If(true, $"Docker/PostgreSQL unavailable: {ex.Message}");
            throw new InvalidOperationException("Unreachable: Skip.If always throws.", ex);
        }
    }

    private async Task<RedisContainer> StartRedisAsync()
    {
        try
        {
            var container = new RedisBuilder("redis:7-alpine").Build();
            await container.StartAsync().ConfigureAwait(true);
            return container;
        }
#pragma warning disable CA1031 // Docker availability probe: any transport-level failure means "skip".
        catch (Exception ex) when (IsInfrastructureUnavailable(ex))
#pragma warning restore CA1031
        {
            _output.WriteLine($"Docker/Redis unavailable, skipping quota test: {ex.Message}");
            Skip.If(true, $"Docker/Redis unavailable: {ex.Message}");
            throw new InvalidOperationException("Unreachable: Skip.If always throws.", ex);
        }
    }

    private static async Task<IConnectionMultiplexer> ConnectRedisAsync(string connectionString)
    {
        try
        {
            return await ConnectionMultiplexer.ConnectAsync(connectionString).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Redis availability probe: connection failure means "skip" via caller handling.
        catch (Exception ex) when (IsInfrastructureUnavailable(ex))
#pragma warning restore CA1031
        {
            Skip.If(true, $"Redis unavailable: {ex.Message}");
            throw new InvalidOperationException("Unreachable: Skip.If always throws.", ex);
        }
    }

    private static bool IsInfrastructureUnavailable(Exception exception)
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

    private sealed class TestFactory : IStageExecutionContextFactory
    {
        private readonly DbContextOptions<AppDbContext> _options;

        public TestFactory(DbContextOptions<AppDbContext> options)
        {
            _options = options;
        }

        public DbContext CreateDbContext()
        {
            return new AppDbContext(_options);
        }
    }
}
