using System.Data.Common;
using System.Net.Sockets;
using System.Security.Cryptography;
using Amazon.S3;
using Amazon.S3.Model;
using DubbingPlatform.Application.Abstractions;
using DubbingPlatform.Application.Exceptions;
using DubbingPlatform.Application.MultiTenancy;
using DubbingPlatform.Application.Options;
using DubbingPlatform.Application.Services;
using DubbingPlatform.Application.Storage;
using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Domain.Enums;
using DubbingPlatform.Infrastructure.Persistence;
using DubbingPlatform.Infrastructure.Persistence.Interceptors;
using DubbingPlatform.Infrastructure.Storage;
using DubbingPlatform.Workers.Services;
using EFCore.NamingConventions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Testcontainers.Minio;
using Testcontainers.PostgreSql;
using Xunit.Abstractions;

namespace DubbingPlatform.IntegrationTests.Storage;

/// <summary>
/// Verifies artifact storage and reconciliation against PostgreSQL 16 and
/// MinIO via Testcontainers. Skips when Docker is unavailable (CI runs live).
/// </summary>
public sealed class ArtifactStorageTests
{
    private const string Bucket = "dubbing-test";

    private readonly ITestOutputHelper _output;

    public ArtifactStorageTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [SkippableFact]
    public async Task Upload_Produces_Committed_Rows()
    {
        var pg = await StartPostgresAsync().ConfigureAwait(true);
        await using (pg.ConfigureAwait(true))
        {
            var minio = await StartMinioAsync().ConfigureAwait(true);
            await using (minio.ConfigureAwait(true))
            {
                var tenantId = Guid.NewGuid();
                var projectId = Guid.NewGuid();
                var runId = Guid.NewGuid();
                var pgOptions = CreatePgOptions(pg);
                await MigrateAsync(pg).ConfigureAwait(true);
                await SeedTenantProjectRunAsync(pgOptions, tenantId, projectId, runId).ConfigureAwait(true);

                var env = await CreateStorageEnvAsync(minio).ConfigureAwait(true);
                var artifacts = new ArtifactService(new TestFactory(pgOptions), env.Storage);

                var bytes = "hello artifact"u8.ToArray();
                PublishResult result;
                using (var stream = new MemoryStream(bytes, writable: false))
                {
                    result = await artifacts.PublishAsync(
                        tenantId, projectId, runId,
                        StageType.Transcription, ArtifactType.Transcript,
                        stream, ".txt", "text/plain",
                        "mock", "mock-1", null, null, [],
                        cancellationToken: CancellationToken.None).ConfigureAwait(true);
                }

                Assert.False(result.ReusedContent);
                Assert.Equal(64, result.ContentHash.Length);

                using (TenantContext.BeginScope(tenantId))
                {
                    using var db = new AppDbContext(pgOptions);
                    var content = await db.Set<ContentObject>()
                        .FirstAsync(c => c.Id == result.ContentObjectId).ConfigureAwait(true);
                    Assert.Equal(ContentObjectStatus.Committed, content.Status);
                    Assert.Equal(result.ContentHash, content.ContentHash);
                    Assert.Equal(result.ContentHash, content.Sha256Hex);
                    Assert.Equal(bytes.Length, content.SizeBytes);
                    Assert.Equal(result.StorageKey, content.StorageKey);

                    var artifact = await db.Set<Artifact>()
                        .FirstAsync(a => a.Id == result.ArtifactId).ConfigureAwait(true);
                    Assert.Equal(ArtifactStatus.Committed, artifact.Status);
                    Assert.Equal("1", artifact.SchemaVersion);
                    Assert.Equal(content.Id, artifact.ContentObjectId);
                }

                Assert.True(await env.Storage.ExistsAsync(result.StorageKey, CancellationToken.None).ConfigureAwait(true));
                using (var download = await env.Storage.DownloadAsync(result.StorageKey, CancellationToken.None).ConfigureAwait(true))
                {
                    using var reader = new MemoryStream();
                    await download.CopyToAsync(reader).ConfigureAwait(true);
                    Assert.Equal(bytes, reader.ToArray());
                }

                var recomputed = await artifacts.VerifyIntegrityAsync(tenantId, result.ArtifactId).ConfigureAwait(true);
                Assert.Equal(result.ContentHash, recomputed);
            }
        }
    }

    [SkippableFact]
    public async Task Duplicate_Within_Tenant_Reuses_ContentObject()
    {
        var pg = await StartPostgresAsync().ConfigureAwait(true);
        await using (pg.ConfigureAwait(true))
        {
            var minio = await StartMinioAsync().ConfigureAwait(true);
            await using (minio.ConfigureAwait(true))
            {
                var tenantId = Guid.NewGuid();
                var projectId = Guid.NewGuid();
                var runId = Guid.NewGuid();
                var pgOptions = CreatePgOptions(pg);
                await MigrateAsync(pg).ConfigureAwait(true);
                await SeedTenantProjectRunAsync(pgOptions, tenantId, projectId, runId).ConfigureAwait(true);

                var env = await CreateStorageEnvAsync(minio).ConfigureAwait(true);
                var artifacts = new ArtifactService(new TestFactory(pgOptions), env.Storage);
                var bytes = "dedup me"u8.ToArray();

                PublishResult first;
                using (var stream = new MemoryStream(bytes, writable: false))
                {
                    first = await artifacts.PublishAsync(
                        tenantId, projectId, runId,
                        StageType.Transcription, ArtifactType.Transcript,
                        stream, ".txt", "text/plain", null, null, null, null, []).ConfigureAwait(true);
                }

                // Ensure reuse touches LastReferencedAt (backdate first, then reuse bumps it).
                using (TenantContext.BeginScope(tenantId))
                {
                    using var db = new AppDbContext(pgOptions);
                    await db.Database.ExecuteSqlRawAsync(
                        "UPDATE content_objects SET last_referenced_at = {0} WHERE id = {1}",
                        DateTimeOffset.UtcNow.AddHours(-2), first.ContentObjectId).ConfigureAwait(true);
                }

                PublishResult second;
                using (var stream = new MemoryStream(bytes, writable: false))
                {
                    second = await artifacts.PublishAsync(
                        tenantId, projectId, runId,
                        StageType.Translation, ArtifactType.Translation,
                        stream, ".txt", "text/plain", null, null, null, null, []).ConfigureAwait(true);
                }

                Assert.False(first.ReusedContent);
                Assert.True(second.ReusedContent);
                Assert.Equal(first.ContentObjectId, second.ContentObjectId);
                Assert.NotEqual(first.ArtifactId, second.ArtifactId);

                using (TenantContext.BeginScope(tenantId))
                {
                    using var db = new AppDbContext(pgOptions);
                    var contents = await db.Set<ContentObject>()
                        .CountAsync(c => c.ContentHash == first.ContentHash).ConfigureAwait(true);
                    Assert.Equal(1, contents);
                    var artifactCount = await db.Set<Artifact>()
                        .CountAsync(a => a.ContentObjectId == first.ContentObjectId).ConfigureAwait(true);
                    Assert.Equal(2, artifactCount);
                }
            }
        }
    }

    [SkippableFact]
    public async Task Duplicate_Across_Tenants_No_Reuse()
    {
        var pg = await StartPostgresAsync().ConfigureAwait(true);
        await using (pg.ConfigureAwait(true))
        {
            var minio = await StartMinioAsync().ConfigureAwait(true);
            await using (minio.ConfigureAwait(true))
            {
                var tenantA = Guid.NewGuid();
                var projectA = Guid.NewGuid();
                var runA = Guid.NewGuid();
                var tenantB = Guid.NewGuid();
                var projectB = Guid.NewGuid();
                var runB = Guid.NewGuid();
                var pgOptions = CreatePgOptions(pg);
                await MigrateAsync(pg).ConfigureAwait(true);
                await SeedTenantProjectRunAsync(pgOptions, tenantA, projectA, runA).ConfigureAwait(true);
                await SeedTenantProjectRunAsync(pgOptions, tenantB, projectB, runB).ConfigureAwait(true);

                var env = await CreateStorageEnvAsync(minio).ConfigureAwait(true);
                var artifacts = new ArtifactService(new TestFactory(pgOptions), env.Storage);
                var bytes = "shared bytes across tenants"u8.ToArray();

                PublishResult resultA;
                using (var stream = new MemoryStream(bytes, writable: false))
                {
                    resultA = await artifacts.PublishAsync(
                        tenantA, projectA, runA,
                        StageType.Transcription, ArtifactType.Transcript,
                        stream, ".txt", "text/plain", null, null, null, null, []).ConfigureAwait(true);
                }

                PublishResult resultB;
                using (var stream = new MemoryStream(bytes, writable: false))
                {
                    resultB = await artifacts.PublishAsync(
                        tenantB, projectB, runB,
                        StageType.Transcription, ArtifactType.Transcript,
                        stream, ".txt", "text/plain", null, null, null, null, []).ConfigureAwait(true);
                }

                Assert.False(resultA.ReusedContent);
                Assert.False(resultB.ReusedContent);
                Assert.NotEqual(resultA.ContentObjectId, resultB.ContentObjectId);
                Assert.StartsWith(tenantA.ToString("N") + "/", resultA.StorageKey, StringComparison.Ordinal);
                Assert.StartsWith(tenantB.ToString("N") + "/", resultB.StorageKey, StringComparison.Ordinal);
            }
        }
    }

    [SkippableFact]
    public async Task Presigned_Requires_Ownership()
    {
        var pg = await StartPostgresAsync().ConfigureAwait(true);
        await using (pg.ConfigureAwait(true))
        {
            var minio = await StartMinioAsync().ConfigureAwait(true);
            await using (minio.ConfigureAwait(true))
            {
                var tenantA = Guid.NewGuid();
                var projectA = Guid.NewGuid();
                var runA = Guid.NewGuid();
                var tenantB = Guid.NewGuid();
                var projectB = Guid.NewGuid();
                var runB = Guid.NewGuid();
                var pgOptions = CreatePgOptions(pg);
                await MigrateAsync(pg).ConfigureAwait(true);
                await SeedTenantProjectRunAsync(pgOptions, tenantA, projectA, runA).ConfigureAwait(true);
                await SeedTenantProjectRunAsync(pgOptions, tenantB, projectB, runB).ConfigureAwait(true);

                var env = await CreateStorageEnvAsync(minio).ConfigureAwait(true);
                var artifacts = new ArtifactService(new TestFactory(pgOptions), env.Storage);
                var bytes = "ownership check"u8.ToArray();

                PublishResult result;
                using (var stream = new MemoryStream(bytes, writable: false))
                {
                    result = await artifacts.PublishAsync(
                        tenantA, projectA, runA,
                        StageType.Transcription, ArtifactType.Transcript,
                        stream, ".txt", "text/plain", null, null, null, null, []).ConfigureAwait(true);
                }

                var url = await artifacts.GetDownloadUrlAsync(tenantA, projectA, result.ArtifactId).ConfigureAwait(true);
                Assert.StartsWith("http", url, StringComparison.OrdinalIgnoreCase);

                var forbidden = await Assert.ThrowsAsync<ForbiddenException>(() => artifacts.GetDownloadUrlAsync(
                    tenantB, projectB, result.ArtifactId)).ConfigureAwait(true);
                Assert.Equal(403, forbidden.StatusCode);
            }
        }
    }

    [SkippableFact]
    public async Task Failed_Commit_Leaves_No_Committed_Artifact()
    {
        var pg = await StartPostgresAsync().ConfigureAwait(true);
        await using (pg.ConfigureAwait(true))
        {
            var minio = await StartMinioAsync().ConfigureAwait(true);
            await using (minio.ConfigureAwait(true))
            {
                var tenantId = Guid.NewGuid();
                var projectId = Guid.NewGuid();
                var runId = Guid.NewGuid();
                var pgOptions = CreatePgOptions(pg);
                await MigrateAsync(pg).ConfigureAwait(true);
                await SeedTenantProjectRunAsync(pgOptions, tenantId, projectId, runId).ConfigureAwait(true);

                var env = await CreateStorageEnvAsync(minio).ConfigureAwait(true);
                var artifacts = new ArtifactService(new TestFactory(pgOptions), env.Storage);
                var bytes = "will fail on missing parent"u8.ToArray();
                var missingParent = Guid.NewGuid();

                var thrown = await Assert.ThrowsAnyAsync<Exception>(async () =>
                {
                    using var stream = new MemoryStream(bytes, writable: false);
                    await artifacts.PublishAsync(
                        tenantId, projectId, runId,
                        StageType.Transcription, ArtifactType.Transcript,
                        stream, ".txt", "text/plain", null, null, null, null, [missingParent]).ConfigureAwait(true);
                }).ConfigureAwait(true);
                Assert.True(
                    thrown is ErrorCodeException || thrown is ForbiddenException || thrown is NotFoundException,
                    $"Unexpected {thrown.GetType().Name}: {thrown.Message}");

                using (TenantContext.BeginScope(tenantId))
                {
                    using var db = new AppDbContext(pgOptions);
                    var committed = await db.Set<Artifact>()
                        .CountAsync(a => a.ProcessingRunId == runId && a.Status == ArtifactStatus.Committed)
                        .ConfigureAwait(true);
                    Assert.Equal(0, committed);
                }

                // The uploaded blob remains orphan for the reconciler.
                var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                var orphanKey = StorageKeyBuilder.BuildKey(
                    tenantId, projectId, runId, "Transcription", "Transcript", hash, ".txt");
                Assert.True(await env.Storage.ExistsAsync(orphanKey, CancellationToken.None).ConfigureAwait(true));
            }
        }
    }

    [SkippableFact]
    public async Task Orphan_Reconciler_Detects_Blob()
    {
        var pg = await StartPostgresAsync().ConfigureAwait(true);
        await using (pg.ConfigureAwait(true))
        {
            var minio = await StartMinioAsync().ConfigureAwait(true);
            await using (minio.ConfigureAwait(true))
            {
                var tenantId = Guid.NewGuid();
                var projectId = Guid.NewGuid();
                var runId = Guid.NewGuid();
                var pgOptions = CreatePgOptions(pg);
                await MigrateAsync(pg).ConfigureAwait(true);
                await SeedTenantProjectRunAsync(pgOptions, tenantId, projectId, runId).ConfigureAwait(true);

                var env = await CreateStorageEnvAsync(minio).ConfigureAwait(true);

                // Dangling blob with no metadata row at all.
                var danglingKey = string.Concat(
                    tenantId.ToString("N"), "/",
                    projectId.ToString("N"), "/",
                    runId.ToString("N"),
                    "/Transcription/Transcript/",
                    new string('d', 64), ".txt");
                using (var stream = new MemoryStream("dangling"u8.ToArray(), writable: false))
                {
                    await env.Storage.UploadAsync(stream, danglingKey, "text/plain", CancellationToken.None).ConfigureAwait(true);
                }

                // Committed row with zero artifact refs, backdated past the 7-day grace.
                var orphanContentId = Guid.NewGuid();
                var orphanKey = string.Concat(
                    tenantId.ToString("N"), "/",
                    projectId.ToString("N"), "/",
                    runId.ToString("N"),
                    "/Transcription/Transcript/",
                    new string('e', 64), ".txt");
                using (TenantContext.BeginScope(tenantId))
                {
                    using var db = new AppDbContext(pgOptions);
                    var now = DateTimeOffset.UtcNow;
                    db.Set<ContentObject>().Add(new ContentObject(
                        orphanContentId, tenantId, new string('e', 64), new string('e', 64),
                        3, "text/plain", orphanKey, ContentObjectStatus.Committed,
                        now.AddDays(-9), now.AddDays(-8)));
                    await db.SaveChangesAsync().ConfigureAwait(true);
                }

                // Listing stub reports the dangling blob as 25h old; quarantine
                // delegates to the real MinIO adapter.
                var stub = new AgedListingStub(
                    env.Storage,
                    [new StorageObjectInfo(danglingKey, DateTimeOffset.UtcNow.AddHours(-25), 8)]);
                var services = new ServiceCollection();
                services.AddLogging();
                services.AddSingleton<IStageExecutionContextFactory>(new TestFactory(pgOptions));
                services.AddSingleton<IStorageInventory>(stub);
                await using var provider = services.BuildServiceProvider();
                var scopes = provider.GetRequiredService<IServiceScopeFactory>();
                var reconciler = new OrphanObjectReconciler(scopes, NullLogger<OrphanObjectReconciler>.Instance);

                var result = await reconciler.ReconcileAsync().ConfigureAwait(true);
                Assert.Equal(1, result.QuarantinedBlobs);
                Assert.Equal(1, result.MarkedOrphaned);

                Assert.False(await env.Storage.ExistsAsync(danglingKey, CancellationToken.None).ConfigureAwait(true));
                var quarantined = await stub.ListQuarantinedAsync().ConfigureAwait(true);
                Assert.Contains(quarantined, k => k.EndsWith(danglingKey, StringComparison.Ordinal));

                using (TenantContext.BeginScope(tenantId))
                {
                    using var db = new AppDbContext(pgOptions);
                    var row = await db.Set<ContentObject>()
                        .FirstAsync(c => c.Id == orphanContentId).ConfigureAwait(true);
                    Assert.Equal(ContentObjectStatus.Orphaned, row.Status);
                }
            }
        }
    }

    [SkippableFact]
    public async Task Lineage_Query_Returns_Parents()
    {
        var pg = await StartPostgresAsync().ConfigureAwait(true);
        await using (pg.ConfigureAwait(true))
        {
            var minio = await StartMinioAsync().ConfigureAwait(true);
            await using (minio.ConfigureAwait(true))
            {
                var tenantId = Guid.NewGuid();
                var projectId = Guid.NewGuid();
                var runId = Guid.NewGuid();
                var pgOptions = CreatePgOptions(pg);
                await MigrateAsync(pg).ConfigureAwait(true);
                await SeedTenantProjectRunAsync(pgOptions, tenantId, projectId, runId).ConfigureAwait(true);

                var env = await CreateStorageEnvAsync(minio).ConfigureAwait(true);
                var factory = new TestFactory(pgOptions);
                var artifacts = new ArtifactService(factory, env.Storage);

                PublishResult parent;
                using (var stream = new MemoryStream("parent bytes"u8.ToArray(), writable: false))
                {
                    parent = await artifacts.PublishAsync(
                        tenantId, projectId, runId,
                        StageType.Transcription, ArtifactType.Transcript,
                        stream, ".txt", "text/plain", null, null, null, null, []).ConfigureAwait(true);
                }

                Guid executionId;
                using (TenantContext.BeginScope(tenantId))
                {
                    var claims = new StageExecutionService(factory, Options.Create(new RetryOptions()));
                    var claim = await claims.ClaimAsync(
                        tenantId, projectId, runId, "Translation", "Segment", "seg-1",
                        null, 0, "worker-1", TimeSpan.FromMinutes(5)).ConfigureAwait(true);
                    executionId = claim.Execution.Id;
                }

                PublishResult child;
                using (var stream = new MemoryStream("child bytes"u8.ToArray(), writable: false))
                {
                    child = await artifacts.PublishAsync(
                        tenantId, projectId, runId,
                        StageType.Translation, ArtifactType.Translation,
                        stream, ".txt", "text/plain", null, null, null, null,
                        [parent.ArtifactId], executionId).ConfigureAwait(true);
                }

                var parents = await artifacts.GetParentsAsync(tenantId, child.ArtifactId).ConfigureAwait(true);
                var single = Assert.Single(parents);
                Assert.Equal(parent.ArtifactId, single.Id);

                using (TenantContext.BeginScope(tenantId))
                {
                    using var db = new AppDbContext(pgOptions);
                    var edge = await db.Set<ArtifactParent>()
                        .FirstOrDefaultAsync(p => p.ChildArtifactId == child.ArtifactId && p.ParentArtifactId == parent.ArtifactId)
                        .ConfigureAwait(true);
                    Assert.NotNull(edge);
                    var output = await db.Set<StageOutputArtifact>()
                        .FirstOrDefaultAsync(o => o.StageExecutionId == executionId && o.ArtifactId == child.ArtifactId)
                        .ConfigureAwait(true);
                    Assert.NotNull(output);
                }
            }
        }
    }

    private sealed record StorageEnv(IArtifactStorage Storage, IAmazonS3 Client);

    private static async Task<StorageEnv> CreateStorageEnvAsync(MinioContainer minio)
    {
        var endpoint = string.Concat("http://", minio.Hostname, ":", minio.GetMappedPublicPort(9000).ToString(System.Globalization.CultureInfo.InvariantCulture));
        var config = new AmazonS3Config
        {
            ServiceURL = endpoint,
            ForcePathStyle = true,
        };
        var client = new AmazonS3Client(minio.GetAccessKey(), minio.GetSecretKey(), config);

        var listed = await client.ListBucketsAsync().ConfigureAwait(true);
        if (!listed.Buckets.Any(b => string.Equals(b.BucketName, Bucket, StringComparison.Ordinal)))
        {
            await client.PutBucketAsync(Bucket, CancellationToken.None).ConfigureAwait(true);
        }

        var options = Options.Create(new StorageOptions
        {
            Endpoint = endpoint,
            Bucket = Bucket,
            UseSsl = false,
            AccessKey = minio.GetAccessKey(),
            SecretKey = minio.GetSecretKey(),
        });
        return new StorageEnv(new S3ArtifactStorage(client, options), client);
    }

    private static async Task SeedTenantProjectRunAsync(
        DbContextOptions<AppDbContext> options,
        Guid tenantId,
        Guid projectId,
        Guid runId)
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
                runId, tenantId, projectId, 0, ProcessingRunStatus.Running, "v1",
                new string('a', 64), new string('b', 64), new string('c', 64),
                now, now, now, null));
            await db.SaveChangesAsync().ConfigureAwait(true);
        }
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

    private static DbContextOptions<AppDbContext> CreatePgOptions(PostgreSqlContainer container)
    {
        return new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(container.GetConnectionString())
            .UseSnakeCaseNamingConvention()
            .AddInterceptors(new TenantSessionInterceptor())
            .Options;
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
            _output.WriteLine($"Docker/PostgreSQL unavailable, skipping storage test: {ex.Message}");
            Skip.If(true, $"Docker/PostgreSQL unavailable: {ex.Message}");
            throw new InvalidOperationException("Unreachable: Skip.If always throws.", ex);
        }
    }

    private async Task<MinioContainer> StartMinioAsync()
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
#pragma warning disable CA1031 // Docker availability probe: any transport-level failure means "skip".
        catch (Exception ex) when (IsInfrastructureUnavailable(ex))
#pragma warning restore CA1031
        {
            _output.WriteLine($"Docker/MinIO unavailable, skipping storage test: {ex.Message}");
            Skip.If(true, $"Docker/MinIO unavailable: {ex.Message}");
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
                or InvalidOperationException
                or AmazonS3Exception)
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

    private sealed class AgedListingStub : IStorageInventory
    {
        private readonly IArtifactStorage _real;
        private readonly IReadOnlyList<StorageObjectInfo> _aged;

        public AgedListingStub(IArtifactStorage real, IReadOnlyList<StorageObjectInfo> aged)
        {
            _real = real;
            _aged = aged;
        }

        public Task<IReadOnlyList<StorageObjectInfo>> ListObjectsAsync(string prefix, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_aged);
        }

        public Task QuarantineAsync(string storageKey, CancellationToken cancellationToken = default)
        {
            return ((IStorageInventory)_real).QuarantineAsync(storageKey, cancellationToken);
        }

        public async Task<IReadOnlyList<string>> ListQuarantinedAsync()
        {
            var inventory = (IStorageInventory)_real;
            var all = await inventory.ListObjectsAsync("quarantine/", CancellationToken.None).ConfigureAwait(true);
            return all.Select(o => o.Key).ToList();
        }
    }
}
