using DubbingPlatform.Application.Errors;
using DubbingPlatform.Application.Exceptions;
using DubbingPlatform.Application.MultiTenancy;
using DubbingPlatform.Application.Options;
using DubbingPlatform.Application.Services;
using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Domain.Enums;
using DubbingPlatform.Infrastructure.Persistence;
using DubbingPlatform.Infrastructure.Persistence.Interceptors;
using DubbingPlatform.IntegrationTests.Fixtures;
using EFCore.NamingConventions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Testcontainers.PostgreSql;
using Xunit.Abstractions;

namespace DubbingPlatform.IntegrationTests.Media;

/// <summary>
/// GAP-013: run-scoped <c>MediaValidation</c> adoption. The saga dispatches
/// this stage on <c>RunStarted</c>; the worker re-affirms the ingestion verdict
/// on the run's own execution so the DAG barrier advances. Proves the happy
/// path completes the leased execution with the source artifact and that a
/// project without validated media fails permanently (no silent pass).
/// </summary>
public sealed class MediaValidationServiceTests : TestFixtureBase
{
    private readonly ITestOutputHelper _output;

    public MediaValidationServiceTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [SkippableFact]
    public async Task ValidateAsync_Completes_Leased_Execution_With_Source_Artifact()
    {
        var pg = await StartPostgresAsync().ConfigureAwait(true);
        await using (pg.ConfigureAwait(true))
        {
            var pgOptions = CreatePgOptions(pg.GetConnectionString());
            await MigrateAsync(pg.GetConnectionString()).ConfigureAwait(true);
            var tenantId = Guid.NewGuid();
            var projectId = Guid.NewGuid();
            var artifactId = await SeedMediaReadyAsync(pgOptions, tenantId, projectId).ConfigureAwait(true);

            var factory = new TestFactory(pgOptions);
            var started = await StartRunAsync(factory, tenantId, projectId).ConfigureAwait(true);
            var runId = started.RunId;
            var claim = await ClaimAsync(factory, tenantId, projectId, runId).ConfigureAwait(true);
            var validation = new MediaValidationService(factory, NullLogger<MediaValidationService>.Instance);

            var result = await validation.ValidateAsync(
                tenantId, projectId, runId,
                claim.Execution.Id, claim.Execution.LeaseOwner, claim.Execution.LeaseToken).ConfigureAwait(true);

            Assert.Contains(artifactId, result.OutputArtifactIds);

            using (TenantContext.BeginScope(tenantId))
            {
                using var db = new AppDbContext(pgOptions);
                var stored = await db.Set<StageExecution>()
                    .AsNoTracking()
                    .FirstAsync(e => e.Id == claim.Execution.Id).ConfigureAwait(true);
                Assert.Equal(StageStatus.Completed, stored.Status);
                Assert.NotNull(stored.CompletedAt);
                Assert.NotNull(stored.OutputArtifactIdsJson);
                Assert.Contains(artifactId.ToString("N"), stored.OutputArtifactIdsJson!, StringComparison.Ordinal);
            }

            _output.WriteLine($"MediaValidation completed for run {runId} with {result.OutputArtifactIds.Count} output(s).");
        }
    }

    [SkippableFact]
    public async Task ValidateAsync_Fails_Permanently_Without_Validated_Media()
    {
        var pg = await StartPostgresAsync().ConfigureAwait(true);
        await using (pg.ConfigureAwait(true))
        {
            var pgOptions = CreatePgOptions(pg.GetConnectionString());
            await MigrateAsync(pg.GetConnectionString()).ConfigureAwait(true);
            var tenantId = Guid.NewGuid();
            var projectId = Guid.NewGuid();
            await SeedMediaReadyAsync(pgOptions, tenantId, projectId).ConfigureAwait(true);

            var factory = new TestFactory(pgOptions);
            var started = await StartRunAsync(factory, tenantId, projectId).ConfigureAwait(true);
            var runId = started.RunId;
            var claim = await ClaimAsync(factory, tenantId, projectId, runId).ConfigureAwait(true);

            // Media validated at ingestion but revoked before the stage runs:
            // the run must fail permanently, never silently pass.
            using (TenantContext.BeginScope(tenantId))
            {
                using var db = new AppDbContext(pgOptions);
                await db.Set<MediaAsset>()
                    .ExecuteUpdateAsync(s => s.SetProperty(a => a.Status, MediaAssetStatus.Invalid))
                    .ConfigureAwait(true);
            }

            var validation = new MediaValidationService(factory, NullLogger<MediaValidationService>.Instance);

            var ex = await Assert.ThrowsAsync<ErrorCodeException>(() => validation.ValidateAsync(
                tenantId, projectId, runId,
                claim.Execution.Id, claim.Execution.LeaseOwner, claim.Execution.LeaseToken)).ConfigureAwait(true);

            Assert.Equal(ErrorCodes.ArtifactUnavailable, ex.ErrorCode);

            using (TenantContext.BeginScope(tenantId))
            {
                using var db = new AppDbContext(pgOptions);
                var stored = await db.Set<StageExecution>()
                    .AsNoTracking()
                    .FirstAsync(e => e.Id == claim.Execution.Id).ConfigureAwait(true);

                // The lease is never completed on a permanent fault: the worker
                // fails the execution (StageWorkerFailure) instead.
                Assert.NotEqual(StageStatus.Completed, stored.Status);
                Assert.Null(stored.CompletedAt);
            }
        }
    }

    private static Task<ProcessingStartResult> StartRunAsync(
        TestFactory factory,
        Guid tenantId,
        Guid projectId)
    {
        var starts = new ProcessingStartService(
            factory,
            Microsoft.Extensions.Options.Options.Create(new QuotaOptions()),
            NullLogger<ProcessingStartService>.Instance);
        return starts.StartAsync(tenantId, projectId, "tester", Guid.NewGuid());
    }

    private static async Task<StageClaimResult> ClaimAsync(
        TestFactory factory,
        Guid tenantId,
        Guid projectId,
        Guid runId)
    {
        var stages = new StageExecutionService(factory, Microsoft.Extensions.Options.Options.Create(new RetryOptions()));
        using (TenantContext.BeginScope(tenantId))
        {
            return await stages.ClaimAsync(
                tenantId, projectId, runId,
                nameof(StageType.MediaValidation), nameof(ScopeType.Project), projectId.ToString("D"),
                null, 0, "test-owner", TimeSpan.FromMinutes(5)).ConfigureAwait(true);
        }
    }

    private static async Task<Guid> SeedMediaReadyAsync(
        DbContextOptions<AppDbContext> options,
        Guid tenantId,
        Guid projectId)
    {
        var now = DateTimeOffset.UtcNow;
        var contentId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var artifactId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var hash = new string('a', 64);
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = new AppDbContext(options);
            db.Tenants.Add(new Tenant(tenantId, $"T-{tenantId:N}", $"t-{tenantId:N}", now));
            db.Set<ContentObject>().Add(new ContentObject(
                contentId, tenantId, hash, hash, 1024, "video/mp4",
                string.Concat(tenantId.ToString("N"), "/seed/source.mp4"),
                ContentObjectStatus.Committed, now, now));
            db.Set<MediaAsset>().Add(new MediaAsset(
                assetId, tenantId, projectId, contentId, "source.mp4", "mp4", "aac", "h264",
                1024, 2000, 48000, 2, "stereo", MediaAssetStatus.Valid, null, hash, now));
            db.Set<Artifact>().Add(new Artifact(
                artifactId, tenantId, projectId, runId,
                StageType.MediaValidation, ArtifactType.SourceOriginal, "1",
                contentId, null, null, null, null,
                ArtifactStatus.Committed, null, now));
            db.DubbingProjects.Add(new DubbingProject(
                projectId, tenantId, "en", "es", ProjectStatus.MediaReady,
                "{}", new string('b', 64), assetId, null, now, now));
            await db.SaveChangesAsync().ConfigureAwait(true);
        }

        return artifactId;
    }

    private sealed class TestFactory : IStageExecutionContextFactory
    {
        private readonly DbContextOptions<AppDbContext> _options;

        public TestFactory(DbContextOptions<AppDbContext> options)
        {
            _options = options;
        }

        public DbContext CreateDbContext() => new AppDbContext(_options);
    }
}