using System.Data.Common;
using System.Net.Sockets;
using DubbingPlatform.Application.Exceptions;
using DubbingPlatform.Application.MultiTenancy;
using DubbingPlatform.Application.Options;
using DubbingPlatform.Application.Services;
using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Domain.Enums;
using DubbingPlatform.Infrastructure.Persistence;
using DubbingPlatform.Infrastructure.Persistence.Interceptors;
using EFCore.NamingConventions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Testcontainers.PostgreSql;
using Xunit.Abstractions;

namespace DubbingPlatform.IntegrationTests.Messaging;

/// <summary>
/// Verifies atomic claiming, lease fencing, recovery, and cancellation
/// blocking against PostgreSQL 16 via Testcontainers. Skips when Docker is
/// unavailable (CI runs live).
/// </summary>
public sealed class StageExecutionTests
{
    private readonly ITestOutputHelper _output;

    public StageExecutionTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [SkippableFact]
    public async Task Duplicate_Message_Produces_One_Execution()
    {
        var container = await StartContainerAsync().ConfigureAwait(true);
        await using (container.ConfigureAwait(true))
        {
            var tenantId = Guid.NewGuid();
            var projectId = Guid.NewGuid();
            var runId = Guid.NewGuid();
            var options = CreateOptions(container);
            await MigrateAsync(container).ConfigureAwait(true);
            await SeedTenantProjectRunAsync(options, tenantId, projectId, runId, ProcessingRunStatus.Running).ConfigureAwait(true);

            var service = CreateService(options);
            StageClaimResult first;
            StageClaimResult second;
            using (TenantContext.BeginScope(tenantId))
            {
                first = await service.ClaimAsync(
                    tenantId, projectId, runId, "Transcription", "Segment", "seg-1",
                    null, 0, "worker-1", TimeSpan.FromMinutes(5)).ConfigureAwait(true);
                second = await service.ClaimAsync(
                    tenantId, projectId, runId, "Transcription", "Segment", "seg-1",
                    null, 0, "worker-2", TimeSpan.FromMinutes(5)).ConfigureAwait(true);
            }

            Assert.True(first.IsNew);
            Assert.False(second.IsNew);
            Assert.Equal(first.Execution.Id, second.Execution.Id);

            using (TenantContext.BeginScope(tenantId))
            {
                using var db = new AppDbContext(options);
                var count = await db.Set<StageExecution>()
                    .CountAsync(e => e.ProcessingRunId == runId).ConfigureAwait(true);
                Assert.Equal(1, count);
            }
        }
    }

    [SkippableFact]
    public async Task Stale_Worker_Cannot_Commit()
    {
        var container = await StartContainerAsync().ConfigureAwait(true);
        await using (container.ConfigureAwait(true))
        {
            var tenantId = Guid.NewGuid();
            var projectId = Guid.NewGuid();
            var runId = Guid.NewGuid();
            var options = CreateOptions(container);
            await MigrateAsync(container).ConfigureAwait(true);
            await SeedTenantProjectRunAsync(options, tenantId, projectId, runId, ProcessingRunStatus.Running).ConfigureAwait(true);

            var service = CreateService(options);
            Guid executionId;
            using (TenantContext.BeginScope(tenantId))
            {
                var claim = await service.ClaimAsync(
                    tenantId, projectId, runId, "Transcription", "Segment", "seg-9",
                    null, 0, "worker-1", TimeSpan.FromMinutes(5)).ConfigureAwait(true);
                executionId = claim.Execution.Id;
            }

            using (TenantContext.BeginScope(tenantId))
            {
                await Assert.ThrowsAsync<LeaseLostException>(() => service.CompleteAsync(
                    tenantId, executionId, "worker-1", "stale-token", ["artifact-1"])).ConfigureAwait(true);
            }

            using (TenantContext.BeginScope(tenantId))
            {
                using var db = new AppDbContext(options);
                var execution = await db.Set<StageExecution>()
                    .FirstAsync(e => e.Id == executionId).ConfigureAwait(true);
                Assert.Equal(StageStatus.Running, execution.Status);
            }
        }
    }

    [SkippableFact]
    public async Task Active_Lease_Not_Recovered()
    {
        var container = await StartContainerAsync().ConfigureAwait(true);
        await using (container.ConfigureAwait(true))
        {
            var tenantId = Guid.NewGuid();
            var projectId = Guid.NewGuid();
            var runId = Guid.NewGuid();
            var options = CreateOptions(container);
            await MigrateAsync(container).ConfigureAwait(true);
            await SeedTenantProjectRunAsync(options, tenantId, projectId, runId, ProcessingRunStatus.Running).ConfigureAwait(true);

            var service = CreateService(options);
            Guid executionId;
            using (TenantContext.BeginScope(tenantId))
            {
                var claim = await service.ClaimAsync(
                    tenantId, projectId, runId, "Translation", "Segment", "seg-active",
                    null, 0, "worker-1", TimeSpan.FromMinutes(10)).ConfigureAwait(true);
                executionId = claim.Execution.Id;
            }

            int recovered;
            using (TenantContext.BeginScope(tenantId))
            {
                recovered = await service.RecoverStaleAsync().ConfigureAwait(true);
            }

            Assert.Equal(0, recovered);

            using (TenantContext.BeginScope(tenantId))
            {
                using var db = new AppDbContext(options);
                var execution = await db.Set<StageExecution>()
                    .FirstAsync(e => e.Id == executionId).ConfigureAwait(true);
                Assert.Equal(StageStatus.Running, execution.Status);
            }
        }
    }

    [SkippableFact]
    public async Task Expired_Lease_Recovered()
    {
        var container = await StartContainerAsync().ConfigureAwait(true);
        await using (container.ConfigureAwait(true))
        {
            var tenantId = Guid.NewGuid();
            var projectId = Guid.NewGuid();
            var runId = Guid.NewGuid();
            var options = CreateOptions(container);
            await MigrateAsync(container).ConfigureAwait(true);
            await SeedTenantProjectRunAsync(options, tenantId, projectId, runId, ProcessingRunStatus.Running).ConfigureAwait(true);

            var service = CreateService(options);
            Guid executionId;
            using (TenantContext.BeginScope(tenantId))
            {
                var claim = await service.ClaimAsync(
                    tenantId, projectId, runId, "Translation", "Segment", "seg-expired",
                    null, 0, "worker-1", TimeSpan.FromMinutes(5)).ConfigureAwait(true);
                executionId = claim.Execution.Id;
            }

            using (TenantContext.BeginScope(tenantId))
            {
                using var db = new AppDbContext(options);
                await db.Database.ExecuteSqlRawAsync(
                    "UPDATE stage_executions SET lease_expires_at = {0} WHERE id = {1}",
                    DateTimeOffset.UtcNow.AddHours(-1),
                    executionId).ConfigureAwait(true);
            }

            int recovered;
            using (TenantContext.BeginScope(tenantId))
            {
                recovered = await service.RecoverStaleAsync().ConfigureAwait(true);
            }

            Assert.Equal(1, recovered);

            using (TenantContext.BeginScope(tenantId))
            {
                using var db = new AppDbContext(options);
                var execution = await db.Set<StageExecution>()
                    .FirstAsync(e => e.Id == executionId).ConfigureAwait(true);
                Assert.Equal(StageStatus.RetryPending, execution.Status);
                Assert.Equal(1, execution.Attempt);
            }
        }
    }

    [SkippableFact]
    public async Task Cancellation_Blocks_Scheduling()
    {
        var container = await StartContainerAsync().ConfigureAwait(true);
        await using (container.ConfigureAwait(true))
        {
            var tenantId = Guid.NewGuid();
            var projectId = Guid.NewGuid();
            var runId = Guid.NewGuid();
            var options = CreateOptions(container);
            await MigrateAsync(container).ConfigureAwait(true);
            await SeedTenantProjectRunAsync(options, tenantId, projectId, runId, ProcessingRunStatus.Cancelling).ConfigureAwait(true);

            var service = CreateService(options);
            using (TenantContext.BeginScope(tenantId))
            {
                await Assert.ThrowsAsync<ConflictException>(() => service.ScheduleAsync(
                    tenantId, projectId, runId, "Transcription", "Segment", "seg-cancel",
                    null, 0, "worker-1", TimeSpan.FromMinutes(5))).ConfigureAwait(true);
            }
        }
    }

    private static StageExecutionService CreateService(DbContextOptions<AppDbContext> options)
    {
        return new StageExecutionService(
            new TestFactory(options),
            Options.Create(new RetryOptions()));
    }

    private static async Task SeedTenantProjectRunAsync(
        DbContextOptions<AppDbContext> options,
        Guid tenantId,
        Guid projectId,
        Guid runId,
        ProcessingRunStatus status)
    {
        var now = DateTimeOffset.UtcNow;
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = new AppDbContext(options);
            db.Tenants.Add(new Tenant(tenantId, $"T-{tenantId:N}", $"t-{tenantId:N}", now));
            db.DubbingProjects.Add(new DubbingProject(
                projectId, tenantId, "en", "de", ProjectStatus.Created,
                "{}", new string('a', 64), null, null, now, now));
            db.ProcessingRuns.Add(new ProcessingRun(
                runId, tenantId, projectId, 0, status, "v1",
                new string('a', 64), new string('b', 64), new string('c', 64),
                now, now, now, null));
            await db.SaveChangesAsync().ConfigureAwait(true);
        }
    }

    private static async Task MigrateAsync(PostgreSqlContainer container)
    {
        using (TenantContext.BeginMaintenanceScope())
        {
            var options = CreateOptions(container);
            using var db = new AppDbContext(options);
            await db.Database.MigrateAsync().ConfigureAwait(true);
        }
    }

    private static DbContextOptions<AppDbContext> CreateOptions(PostgreSqlContainer container)
    {
        return new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(container.GetConnectionString())
            .UseSnakeCaseNamingConvention()
            .AddInterceptors(new TenantSessionInterceptor())
            .Options;
    }

    private async Task<PostgreSqlContainer> StartContainerAsync()
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
            _output.WriteLine($"Docker/PostgreSQL unavailable, skipping stage execution test: {ex.Message}");
            Skip.If(true, $"Docker/PostgreSQL unavailable: {ex.Message}");
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
