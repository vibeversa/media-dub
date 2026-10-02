using DubbingPlatform.Application.Abstractions;
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

namespace DubbingPlatform.IntegrationTests.Storage;

/// <summary>
/// GAP-019: retention holds block deletion. A placed hold fails the logical
/// delete (<c>POLICY_DENIED</c>), blocks the artifact-scoped gate, and makes the
/// physical sweeper skip the row; releasing the hold plus an expired retention
/// window with zero live references makes the row deletable again.
/// </summary>
public sealed class RetentionHoldTests : TestFixtureBase
{
    private readonly ITestOutputHelper _output;

    public RetentionHoldTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [SkippableFact]
    public async Task Active_Hold_Blocks_Project_Deletion_With_Policy_Denied()
    {
        var pg = await StartPostgresAsync().ConfigureAwait(true);
        await using (pg.ConfigureAwait(true))
        {
            var options = CreatePgOptions(pg.GetConnectionString());
            await MigrateAsync(pg.GetConnectionString()).ConfigureAwait(true);
            var tenantId = Guid.NewGuid();
            var projectId = Guid.NewGuid();
            await SeedProjectAsync(options, tenantId, projectId).ConfigureAwait(true);
            var holdId = await PlaceHoldAsync(options, tenantId, projectId).ConfigureAwait(true);

            var service = CreateRetentionService(options);

            var ex = await Assert.ThrowsAsync<ErrorCodeException>(() => service.RequestDeletionAsync(
                tenantId, projectId, "operator")).ConfigureAwait(true);
            Assert.Equal(ErrorCodes.PolicyDenied, ex.ErrorCode);
            Assert.Contains(holdId.ToString("N"), ex.Message, StringComparison.OrdinalIgnoreCase);

            using (TenantContext.BeginScope(tenantId))
            {
                using var db = new AppDbContext(options);
                var isDeleted = await db.DubbingProjects
                    .AsNoTracking()
                    .Where(p => p.Id == projectId)
                    .Select(p => EF.Property<bool>(p, "IsDeleted"))
                    .FirstAsync()
                    .ConfigureAwait(true);
                Assert.False(isDeleted);
                Assert.Empty(await db.Set<DeletionJob>().Where(j => j.ProjectId == projectId).ToListAsync().ConfigureAwait(true));
            }

            _output.WriteLine($"Hold {holdId} blocked deletion of project {projectId} as expected.");
        }
    }

    [SkippableFact]
    public async Task Released_Hold_Allows_Deletion_And_Is_Audited()
    {
        var pg = await StartPostgresAsync().ConfigureAwait(true);
        await using (pg.ConfigureAwait(true))
        {
            var options = CreatePgOptions(pg.GetConnectionString());
            await MigrateAsync(pg.GetConnectionString()).ConfigureAwait(true);
            var tenantId = Guid.NewGuid();
            var projectId = Guid.NewGuid();
            await SeedProjectAsync(options, tenantId, projectId).ConfigureAwait(true);
            var holdId = await PlaceHoldAsync(options, tenantId, projectId).ConfigureAwait(true);
            await ReleaseHoldAsync(options, tenantId, holdId).ConfigureAwait(true);

            var service = CreateRetentionService(options);
            var jobId = await service.RequestDeletionAsync(tenantId, projectId, "operator").ConfigureAwait(true);

            using (TenantContext.BeginScope(tenantId))
            {
                using var db = new AppDbContext(options);
                var job = await db.Set<DeletionJob>().AsNoTracking().FirstAsync(j => j.Id == jobId).ConfigureAwait(true);
                Assert.Equal("Completed", job.Status);
            }
        }
    }

    [SkippableFact]
    public async Task Sweeper_Skips_Deleted_Artifact_Under_Hold_Then_Deletes_After_Release()
    {
        var pg = await StartPostgresAsync().ConfigureAwait(true);
        await using (pg.ConfigureAwait(true))
        {
            var options = CreatePgOptions(pg.GetConnectionString());
            await MigrateAsync(pg.GetConnectionString()).ConfigureAwait(true);
            var tenantId = Guid.NewGuid();
            var projectId = Guid.NewGuid();
            var artifactId = await SeedDeletedArtifactAsync(options, tenantId, projectId).ConfigureAwait(true);
            var holdId = await PlaceHoldAsync(options, tenantId, projectId, artifactId: artifactId).ConfigureAwait(true);

            var storage = new RecordingStorage();
            var service = CreateRetentionService(options, storage);

            var held = await service.SweepAsync().ConfigureAwait(true);
            Assert.Equal(0, held.ArtifactsDeleted);
            using (TenantContext.BeginScope(tenantId))
            {
                using var db = new AppDbContext(options);
                Assert.True(await db.Set<Artifact>().AnyAsync(a => a.Id == artifactId).ConfigureAwait(true));
            }

            await ReleaseHoldAsync(options, tenantId, holdId).ConfigureAwait(true);

            var released = await service.SweepAsync().ConfigureAwait(true);
            Assert.Equal(1, released.ArtifactsDeleted);
            using (TenantContext.BeginScope(tenantId))
            {
                using var db = new AppDbContext(options);
                Assert.False(await db.Set<Artifact>().AnyAsync(a => a.Id == artifactId).ConfigureAwait(true));
            }

            _output.WriteLine("Sweeper skipped the held artifact and deleted it once released.");
        }
    }

    [SkippableFact]
    public async Task Artifact_Delete_Gate_Tracks_Hold_State()
    {
        var pg = await StartPostgresAsync().ConfigureAwait(true);
        await using (pg.ConfigureAwait(true))
        {
            var options = CreatePgOptions(pg.GetConnectionString());
            await MigrateAsync(pg.GetConnectionString()).ConfigureAwait(true);
            var tenantId = Guid.NewGuid();
            var projectId = Guid.NewGuid();
            var artifactId = await SeedDeletedArtifactAsync(options, tenantId, projectId).ConfigureAwait(true);

            var contents = new ContentObjectService(new TestFactory(options));
            Assert.False(await contents.IsDeleteBlockedByHoldAsync(tenantId, artifactId).ConfigureAwait(true));

            var holdId = await PlaceHoldAsync(options, tenantId, projectId, artifactId: artifactId).ConfigureAwait(true);
            Assert.True(await contents.IsDeleteBlockedByHoldAsync(tenantId, artifactId).ConfigureAwait(true));

            await ReleaseHoldAsync(options, tenantId, holdId).ConfigureAwait(true);
            Assert.False(await contents.IsDeleteBlockedByHoldAsync(tenantId, artifactId).ConfigureAwait(true));

            // Cross-tenant and unknown ids fail closed (blocked), never allowed.
            Assert.True(await contents.IsDeleteBlockedByHoldAsync(Guid.NewGuid(), artifactId).ConfigureAwait(true));
        }
    }

    [SkippableFact]
    public async Task Content_Object_Requires_Dereference_And_Expired_Window()
    {
        var pg = await StartPostgresAsync().ConfigureAwait(true);
        await using (pg.ConfigureAwait(true))
        {
            var options = CreatePgOptions(pg.GetConnectionString());
            await MigrateAsync(pg.GetConnectionString()).ConfigureAwait(true);
            var tenantId = Guid.NewGuid();
            var projectId = Guid.NewGuid();
            var (contentId, artifactId) = await SeedContentWithArtifactAsync(options, tenantId, projectId, daysSinceReferenced: 400).ConfigureAwait(true);

            var contents = new ContentObjectService(new TestFactory(options));

            // Still referenced by an artifact row (even soft-deleted) → blocked.
            Assert.False(await contents.CanDeleteContentObjectAsync(tenantId, contentId, retentionDays: 30).ConfigureAwait(true));

            using (TenantContext.BeginScope(tenantId))
            {
                using var db = new AppDbContext(options);
                await db.Set<Artifact>().Where(a => a.Id == artifactId).ExecuteDeleteAsync().ConfigureAwait(true);
            }

            // Dereferenced + window expired → deletable.
            Assert.True(await contents.CanDeleteContentObjectAsync(tenantId, contentId, retentionDays: 30).ConfigureAwait(true));

            // Freshly referenced content stays protected regardless of the window.
            var (freshContent, freshArtifact) = await SeedContentWithArtifactAsync(options, tenantId, projectId, daysSinceReferenced: 0).ConfigureAwait(true);
            Assert.False(await contents.CanDeleteContentObjectAsync(tenantId, freshContent, retentionDays: 0).ConfigureAwait(true));
            using (TenantContext.BeginScope(tenantId))
            {
                using var db = new AppDbContext(options);
                await db.Set<Artifact>().Where(a => a.Id == freshArtifact).ExecuteDeleteAsync().ConfigureAwait(true);
            }
        }
    }

    private static RetentionService CreateRetentionService(
        DbContextOptions<AppDbContext> options,
        IArtifactStorage? storage = null)
    {
        var factory = new TestFactory(options);
        return new RetentionService(
            factory,
            storage ?? new RecordingStorage(),
            new ContentObjectService(factory),
            new AuditService(factory),
            Microsoft.Extensions.Options.Options.Create(new RetentionOptions()),
            NullLogger<RetentionService>.Instance);
    }

    private static async Task SeedProjectAsync(DbContextOptions<AppDbContext> options, Guid tenantId, Guid projectId)
    {
        var now = DateTimeOffset.UtcNow;
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = new AppDbContext(options);
            db.Tenants.Add(new Tenant(tenantId, $"T-{tenantId:N}", $"t-{tenantId:N}", now));
            db.DubbingProjects.Add(new DubbingProject(
                projectId, tenantId, "en", "es", ProjectStatus.MediaReady,
                "{}", new string('a', 64), null, null, now, now));
            await db.SaveChangesAsync().ConfigureAwait(true);
        }
    }

    private static async Task<Guid> SeedDeletedArtifactAsync(DbContextOptions<AppDbContext> options, Guid tenantId, Guid projectId)
    {
        var artifactId = Guid.NewGuid();
        var contentId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow.AddDays(-400);
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = new AppDbContext(options);
            db.Set<ContentObject>().Add(new ContentObject(
                contentId, tenantId, new string('a', 64), new string('a', 64), 128, "audio/flac",
                $"{tenantId:N}/{projectId:N}/old.flac", ContentObjectStatus.Committed, now, now));
            db.Set<Artifact>().Add(new Artifact(
                artifactId, tenantId, projectId, Guid.NewGuid(),
                StageType.AudioMixing, ArtifactType.MixedAudio, "1", contentId,
                null, null, null, null, ArtifactStatus.Deleted, null, now));
            await db.SaveChangesAsync().ConfigureAwait(true);
        }

        return artifactId;
    }

    private static async Task<(Guid ContentId, Guid ArtifactId)> SeedContentWithArtifactAsync(
        DbContextOptions<AppDbContext> options,
        Guid tenantId,
        Guid projectId,
        int daysSinceReferenced)
    {
        var contentId = Guid.NewGuid();
        var artifactId = Guid.NewGuid();
        var hash = Convert.ToHexString(Guid.NewGuid().ToByteArray()).ToLowerInvariant() + new string('0', 32);
        var referenced = DateTimeOffset.UtcNow.AddDays(-daysSinceReferenced);
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = new AppDbContext(options);
            db.Set<ContentObject>().Add(new ContentObject(
                contentId, tenantId, hash, hash, 256, "audio/wav",
                $"{tenantId:N}/{projectId:N}/{contentId:N}.wav", ContentObjectStatus.Committed, referenced, referenced));
            db.Set<Artifact>().Add(new Artifact(
                artifactId, tenantId, projectId, Guid.NewGuid(),
                StageType.AudioMixing, ArtifactType.MixedAudio, "1", contentId,
                null, null, null, null, ArtifactStatus.Deleted, null, referenced));
            await db.SaveChangesAsync().ConfigureAwait(true);
        }

        return (contentId, artifactId);
    }

    private static async Task<Guid> PlaceHoldAsync(
        DbContextOptions<AppDbContext> options,
        Guid tenantId,
        Guid projectId,
        Guid? artifactId = null)
    {
        var holdId = Guid.NewGuid();
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = new AppDbContext(options);
            db.Set<RetentionHold>().Add(new RetentionHold(
                holdId, tenantId, artifactId is null ? projectId : null, artifactId,
                "legal hold", "counsel", DateTimeOffset.UtcNow, null, isActive: true));
            await db.SaveChangesAsync().ConfigureAwait(true);
        }

        return holdId;
    }

    private static async Task ReleaseHoldAsync(DbContextOptions<AppDbContext> options, Guid tenantId, Guid holdId)
    {
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = new AppDbContext(options);
            await db.Set<RetentionHold>()
                .Where(h => h.Id == holdId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(h => h.IsActive, false)
                    .SetProperty(h => h.ReleasedAt, DateTimeOffset.UtcNow))
                .ConfigureAwait(true);
        }
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

    /// <summary>
    /// Storage double that records deletes; retention rows are metadata-only in
    /// these tests, so no bytes are required.
    /// </summary>
    private sealed class RecordingStorage : IArtifactStorage
    {
        public List<string> Deleted { get; } = [];

        public Task UploadAsync(Stream content, string storageKey, string contentType, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Uploads are not used by retention tests.");

        public Task<Stream> DownloadAsync(string storageKey, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Downloads are not used by retention tests.");

        public Task<bool> ExistsAsync(string storageKey, CancellationToken cancellationToken) =>
            Task.FromResult(false);

        public Task DeleteAsync(string storageKey, CancellationToken cancellationToken)
        {
            Deleted.Add(storageKey);
            return Task.CompletedTask;
        }

        public Task<string> GetPresignedDownloadUrlAsync(string storageKey, TimeSpan expiry, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Signed URLs are not used by retention tests.");

        public Task<string> GetPresignedUploadUrlAsync(string storageKey, TimeSpan expiry, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Signed URLs are not used by retention tests.");

        public Task<string?> GetStorageChecksumAsync(string storageKey, CancellationToken cancellationToken) =>
            Task.FromResult<string?>(null);
    }
}