using System.Data.Common;
using System.Net.Sockets;
using Amazon.S3;
using DubbingPlatform.Application.Abstractions;
using DubbingPlatform.Application.Errors;
using DubbingPlatform.Application.Exceptions;
using DubbingPlatform.Application.MultiTenancy;
using DubbingPlatform.Application.Options;
using DubbingPlatform.Application.Orchestration;
using DubbingPlatform.Application.Services;
using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Domain.Enums;
using DubbingPlatform.Infrastructure.Media;
using DubbingPlatform.Infrastructure.Persistence;
using DubbingPlatform.Infrastructure.Persistence.Interceptors;
using DubbingPlatform.Infrastructure.Processes;
using DubbingPlatform.Infrastructure.Storage;
using EFCore.NamingConventions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Testcontainers.Minio;
using Testcontainers.PostgreSql;
using Xunit.Abstractions;

namespace DubbingPlatform.IntegrationTests.Media;

/// <summary>
/// Task 020: processing start, run creation, and canonical 48kHz audio over
/// PG (+ MinIO + FFmpeg where noted). Skips with an explicit message when
/// Docker (PG/MinIO) or ffmpeg/ffprobe is unavailable (CI runs live with all
/// three).
/// </summary>
public sealed class AudioPrepTests
{
    private const string Bucket = "dubbing-test";

    private const long PartSizeBytes = 8L * 1024L * 1024L;

    private readonly ITestOutputHelper _output;

    public AudioPrepTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [SkippableFact]
    public async Task Start_Creates_One_Run()
    {
        var pg = await StartPostgresAsync().ConfigureAwait(true);
        await using (pg.ConfigureAwait(true))
        {
            var pgOptions = CreatePgOptions(pg);
            await MigrateAsync(pg).ConfigureAwait(true);
            var tenantId = Guid.NewGuid();
            var projectId = Guid.NewGuid();
            await SeedMediaReadyAsync(pgOptions, tenantId, projectId).ConfigureAwait(true);

            var starts = CreateStartService(pgOptions);
            var runId = Guid.NewGuid();
            var result = await starts.StartAsync(tenantId, projectId, "tester", runId).ConfigureAwait(true);

            Assert.Equal(runId, result.RunId);
            Assert.Equal(0, result.Attempt);
            Assert.Equal("1.0.0", result.PipelineVersion);
            Assert.Equal(ProcessingRunStatus.Pending.ToString(), result.Status);
            Assert.Equal(64, result.ConfigurationHash.Length);
            Assert.Equal(64, result.ProviderRouteHash.Length);
            Assert.Equal(64, result.ExecutionSnapshotHash.Length);

            using (TenantContext.BeginScope(tenantId))
            {
                using var db = new AppDbContext(pgOptions);
                var run = await db.Set<ProcessingRun>().FirstAsync(r => r.Id == runId).ConfigureAwait(true);
                Assert.Equal(ProcessingRunStatus.Pending, run.Status);
                Assert.Equal("1.0.0", run.PipelineVersion);
                Assert.Null(run.StartedAt);

                var project = await db.Set<DubbingProject>().FirstAsync(p => p.Id == projectId).ConfigureAwait(true);
                Assert.Equal(ProjectStatus.Processing, project.Status);
                Assert.Equal(runId, project.ActiveRunId);

                var summaries = await db.Set<RunStageSummary>()
                    .Where(s => s.ProcessingRunId == runId)
                    .ToListAsync().ConfigureAwait(true);
                Assert.Equal(StageGraph.Nodes.Count, summaries.Count);
                foreach (var node in StageGraph.Nodes)
                {
                    var summary = summaries.First(s => s.StageType == node.StageType);
                    var expected = node.Scope is ScopeType.Project or ScopeType.Run ? 1 : 0;
                    Assert.Equal(expected, summary.ExpectedUnits);
                }

                var snapshot = await db.Set<ProviderRouteSnapshot>()
                    .FirstOrDefaultAsync(s => s.ProcessingRunId == runId).ConfigureAwait(true);
                Assert.NotNull(snapshot);
            }
        }
    }

    [SkippableFact]
    public async Task Second_Start_409()
    {
        var pg = await StartPostgresAsync().ConfigureAwait(true);
        await using (pg.ConfigureAwait(true))
        {
            var pgOptions = CreatePgOptions(pg);
            await MigrateAsync(pg).ConfigureAwait(true);
            var tenantId = Guid.NewGuid();
            var projectId = Guid.NewGuid();
            await SeedMediaReadyAsync(pgOptions, tenantId, projectId).ConfigureAwait(true);

            var starts = CreateStartService(pgOptions);
            await starts.StartAsync(tenantId, projectId, "tester", Guid.NewGuid()).ConfigureAwait(true);

            using (TenantContext.BeginMaintenanceScope())
            {
                using var db = new AppDbContext(pgOptions);
                await db.Database.ExecuteSqlRawAsync(
                    "UPDATE dubbing_projects SET status = {0} WHERE id = {1}",
                    ProjectStatus.MediaReady.ToString(), projectId).ConfigureAwait(true);
            }

            var ex = await Assert.ThrowsAsync<ConflictException>(() => starts.StartAsync(tenantId, projectId, "tester", Guid.NewGuid())).ConfigureAwait(true);
            Assert.Equal(ErrorCodes.Conflict, ex.ErrorCode);
            Assert.Contains("active run", ex.Message, StringComparison.OrdinalIgnoreCase);

            using (TenantContext.BeginScope(tenantId))
            {
                using var db = new AppDbContext(pgOptions);
                var runCount = await db.Set<ProcessingRun>()
                    .CountAsync(r => r.ProjectId == projectId).ConfigureAwait(true);
                Assert.Equal(1, runCount);
            }
        }
    }

    [SkippableFact]
    public async Task Canonical_Audio_SampleRate_Layout()
    {
        await EnsureFfmpegAsync().ConfigureAwait(true);
        var mp4Path = await GenerateValidMp4Async().ConfigureAwait(true);
        var destPath = Path.Combine(Path.GetTempPath(), string.Concat("dubbing-canon-", Guid.NewGuid().ToString("N"), ".flac"));
        var workingDir = ProcessRunner.CreateTempWorkingDir();
        try
        {
            var runner = new ProcessRunner(NullLogger<ProcessRunner>.Instance);
            var storageMock = new Moq.Mock<IArtifactStorage>(Moq.MockBehavior.Strict);
            var ffprobe = new FFprobeService(
                storageMock.Object, runner,
                global::Microsoft.Extensions.Options.Options.Create(new MediaOptions()),
                NullLogger<FFprobeService>.Instance);
            var ffmpeg = new FFmpegService(
                runner, ffprobe,
                global::Microsoft.Extensions.Options.Options.Create(new MediaOptions()),
                NullLogger<FFmpegService>.Instance);

            var source = await ffprobe.ProbeAsync(mp4Path, CancellationToken.None).ConfigureAwait(true);
            var sourceAudio = source.Streams.First(s => string.Equals(s.CodecType, "audio", StringComparison.OrdinalIgnoreCase));

            var result = await ffmpeg.ExtractCanonicalAudioAsync(mp4Path, destPath, CancellationToken.None).ConfigureAwait(true);
            Assert.Contains("-ar", result.Args);
            Assert.Contains("48000", result.Args);
            Assert.Contains("flac", result.Args);

            var canonical = await ffprobe.ProbeAsync(destPath, CancellationToken.None).ConfigureAwait(true);
            var canonicalAudio = canonical.Streams.First(s => string.Equals(s.CodecType, "audio", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(48000, canonicalAudio.SampleRate);
            Assert.Equal(sourceAudio.Channels, canonicalAudio.Channels);
            Assert.Equal(
                (sourceAudio.ChannelLayout ?? string.Empty).Trim().ToLowerInvariant(),
                (canonicalAudio.ChannelLayout ?? string.Empty).Trim().ToLowerInvariant());
        }
        finally
        {
            DeleteQuietly(mp4Path);
            DeleteQuietly(destPath);
            try
            {
                Directory.Delete(workingDir, recursive: true);
            }
            catch (Exception)
            {
                // Best effort.
            }
        }
    }

    [SkippableFact]
    public async Task Duration_Within_Tolerance()
    {
        await EnsureFfmpegAsync().ConfigureAwait(true);
        var mp4Path = await GenerateValidMp4Async().ConfigureAwait(true);
        var destPath = Path.Combine(Path.GetTempPath(), string.Concat("dubbing-canon-", Guid.NewGuid().ToString("N"), ".flac"));
        try
        {
            var runner = new ProcessRunner(NullLogger<ProcessRunner>.Instance);
            var storageMock = new Moq.Mock<IArtifactStorage>(Moq.MockBehavior.Strict);
            var ffprobe = new FFprobeService(
                storageMock.Object, runner,
                global::Microsoft.Extensions.Options.Options.Create(new MediaOptions()),
                NullLogger<FFprobeService>.Instance);
            var ffmpeg = new FFmpegService(
                runner, ffprobe,
                global::Microsoft.Extensions.Options.Options.Create(new MediaOptions()),
                NullLogger<FFmpegService>.Instance);

            var source = await ffprobe.ProbeAsync(mp4Path, CancellationToken.None).ConfigureAwait(true);
            await ffmpeg.ExtractCanonicalAudioAsync(mp4Path, destPath, CancellationToken.None).ConfigureAwait(true);
            var canonical = await ffprobe.ProbeAsync(destPath, CancellationToken.None).ConfigureAwait(true);

            var drift = Math.Abs(canonical.DurationMs - source.DurationMs);
            Assert.True(drift <= 100, $"Duration drift {drift}ms exceeds 100ms tolerance.");
        }
        finally
        {
            DeleteQuietly(mp4Path);
            DeleteQuietly(destPath);
        }
    }

    [SkippableFact]
    public async Task Args_Recorded()
    {
        var pg = await StartPostgresAsync().ConfigureAwait(true);
        await using (pg.ConfigureAwait(true))
        {
            var minio = await StartMinioAsync().ConfigureAwait(true);
            await using (minio.ConfigureAwait(true))
            {
                await EnsureFfmpegAsync().ConfigureAwait(true);
                var pgOptions = CreatePgOptions(pg);
                await MigrateAsync(pg).ConfigureAwait(true);
                var tenantId = Guid.NewGuid();
                var projectId = Guid.NewGuid();
                await SeedCreatedProjectAsync(pgOptions, tenantId, projectId).ConfigureAwait(true);

                var env = await CreateStorageEnvAsync(minio).ConfigureAwait(true);
                var mp4Path = await GenerateValidMp4Async().ConfigureAwait(true);
                try
                {
                    var bytes = await File.ReadAllBytesAsync(mp4Path).ConfigureAwait(true);
                    var uploadId = Guid.NewGuid();
                    var storageKey = StorageKeyForUpload(tenantId, projectId, uploadId, "valid.mp4");
                    await UploadBytesAsync(env.Storage, bytes, storageKey, "video/mp4").ConfigureAwait(true);
                    await CreateUploadSessionAsync(pgOptions, tenantId, projectId, uploadId, "valid.mp4", "video/mp4", bytes.Length, storageKey, UploadStatus.Completed).ConfigureAwait(true);

                    var ingestion = CreateIngestion(pgOptions, env.Storage);
                    var ingested = await ingestion.IngestAsync(tenantId, projectId, uploadId).ConfigureAwait(true);
                    Assert.True(ingested.IsValid);

                    var starts = CreateStartService(pgOptions);
                    var started = await starts.StartAsync(tenantId, projectId, "tester", Guid.NewGuid()).ConfigureAwait(true);

                    var stages = new StageExecutionService(new TestFactory(pgOptions), global::Microsoft.Extensions.Options.Options.Create(new RetryOptions()));
                    StageClaimResult claim;
                    using (TenantContext.BeginScope(tenantId))
                    {
                        claim = await stages.ScheduleAsync(
                            tenantId, projectId, started.RunId,
                            nameof(StageType.AudioPreparation), nameof(ScopeType.Project), projectId.ToString("D"),
                            null, 0, "test-owner", TimeSpan.FromMinutes(5)).ConfigureAwait(true);
                    }

                    var preparation = CreatePreparation(pgOptions, env.Storage);
                    using var gate = new SemaphoreSlim(2);
                    var result = await preparation.PrepareAsync(
                        tenantId, projectId, started.RunId,
                        claim.Execution.Id, claim.Execution.LeaseOwner, claim.Execution.LeaseToken,
                        gate).ConfigureAwait(true);

                    Assert.Contains("-ar", result.FfmpegArgs);
                    Assert.Contains("48000", result.FfmpegArgs);
                    Assert.Contains("flac", result.FfmpegArgs);
                    Assert.Contains("-threads", result.FfmpegArgs);

                    using (TenantContext.BeginScope(tenantId))
                    {
                        using var db = new AppDbContext(pgOptions);
                        var artifact = await db.Set<Artifact>().FirstAsync(a => a.Id == result.ArtifactId).ConfigureAwait(true);
                        Assert.Equal(ArtifactType.CanonicalAudio, artifact.Type);
                        Assert.Equal(ArtifactStatus.Committed, artifact.Status);
                        Assert.NotNull(artifact.MetadataJson);
                        Assert.Contains("-ar", artifact.MetadataJson, StringComparison.Ordinal);
                        Assert.Contains("48000", artifact.MetadataJson, StringComparison.Ordinal);
                        Assert.Contains("flac", artifact.MetadataJson, StringComparison.Ordinal);

                        var execution = await db.Set<StageExecution>().FirstAsync(e => e.Id == claim.Execution.Id).ConfigureAwait(true);
                        Assert.Equal(StageStatus.Completed, execution.Status);
                        Assert.NotNull(execution.OutputArtifactIdsJson);
                        Assert.Contains(result.ArtifactId.ToString("N"), execution.OutputArtifactIdsJson, StringComparison.Ordinal);
                    }
                }
                finally
                {
                    DeleteQuietly(mp4Path);
                }
            }
        }
    }

    [SkippableFact]
    public async Task Disk_Full_Fails_Fast()
    {
        var pg = await StartPostgresAsync().ConfigureAwait(true);
        await using (pg.ConfigureAwait(true))
        {
            var pgOptions = CreatePgOptions(pg);
            await MigrateAsync(pg).ConfigureAwait(true);
            var tenantId = Guid.NewGuid();
            var projectId = Guid.NewGuid();
            await SeedMediaReadyAsync(pgOptions, tenantId, projectId).ConfigureAwait(true);

            var starts = CreateStartService(pgOptions);
            var started = await starts.StartAsync(tenantId, projectId, "tester", Guid.NewGuid()).ConfigureAwait(true);

            var stages = new StageExecutionService(new TestFactory(pgOptions), global::Microsoft.Extensions.Options.Options.Create(new RetryOptions()));
            StageClaimResult claim;
            using (TenantContext.BeginScope(tenantId))
            {
                claim = await stages.ScheduleAsync(
                    tenantId, projectId, started.RunId,
                    nameof(StageType.AudioPreparation), nameof(ScopeType.Project), projectId.ToString("D"),
                    null, 0, "test-owner", TimeSpan.FromMinutes(5)).ConfigureAwait(true);
            }

            var diskMock = new Moq.Mock<IDiskSpaceChecker>(Moq.MockBehavior.Strict);
            diskMock
                .Setup(d => d.EnsureFree(Moq.It.IsAny<string>(), Moq.It.IsAny<long>()))
                .Throws(new ErrorCodeException(ErrorCodes.ResourceExhausted, "Disk full (mock)."));
            var preparation = CreatePreparation(pgOptions, new UnusedStorage(), diskMock.Object);

            var ex = await Assert.ThrowsAsync<ErrorCodeException>(() => preparation.PrepareAsync(
                tenantId, projectId, started.RunId,
                claim.Execution.Id, claim.Execution.LeaseOwner, claim.Execution.LeaseToken,
                null)).ConfigureAwait(true);
            Assert.Equal(ErrorCodes.ResourceExhausted, ex.ErrorCode);

            using (TenantContext.BeginScope(tenantId))
            {
                using var db = new AppDbContext(pgOptions);
                var canonicalCount = await db.Set<Artifact>()
                    .CountAsync(a => a.ProcessingRunId == started.RunId && a.Type == ArtifactType.CanonicalAudio).ConfigureAwait(true);
                Assert.Equal(0, canonicalCount);
            }
        }
    }

    private static ProcessingStartService CreateStartService(DbContextOptions<AppDbContext> pgOptions)
    {
        return new ProcessingStartService(
            new TestFactory(pgOptions),
            global::Microsoft.Extensions.Options.Options.Create(new QuotaOptions()),
            NullLogger<ProcessingStartService>.Instance);
    }

    private MediaIngestionService CreateIngestion(DbContextOptions<AppDbContext> pgOptions, IArtifactStorage storage)
    {
        var factory = new TestFactory(pgOptions);
        var artifacts = new ArtifactService(factory, storage);
        var runner = new ProcessRunner(NullLogger<ProcessRunner>.Instance);
        var ffprobe = new FFprobeService(storage, runner, global::Microsoft.Extensions.Options.Options.Create(new MediaOptions()), NullLogger<FFprobeService>.Instance);
        var quota = new StorageQuotaGate(factory, global::Microsoft.Extensions.Options.Options.Create(new QuotaOptions()));
        return new MediaIngestionService(
            factory, storage, artifacts, ffprobe, quota,
            global::Microsoft.Extensions.Options.Options.Create(new MediaOptions()), NullLogger<MediaIngestionService>.Instance);
    }

    private AudioPreparationService CreatePreparation(DbContextOptions<AppDbContext> pgOptions, IArtifactStorage storage, IDiskSpaceChecker? disk = null)
    {
        var factory = new TestFactory(pgOptions);
        var artifacts = new ArtifactService(factory, storage);
        var runner = new ProcessRunner(NullLogger<ProcessRunner>.Instance);
        var mediaOptions = global::Microsoft.Extensions.Options.Options.Create(new MediaOptions());
        var ffprobe = new FFprobeService(storage, runner, mediaOptions, NullLogger<FFprobeService>.Instance);
        var ffmpeg = new FFmpegService(runner, ffprobe, mediaOptions, NullLogger<FFmpegService>.Instance);
        return new AudioPreparationService(
            factory, storage, artifacts, ffprobe, ffmpeg,
            disk ?? new DiskSpaceChecker(), mediaOptions,
            NullLogger<AudioPreparationService>.Instance);
    }

    private static string StorageKeyForUpload(Guid tenantId, Guid projectId, Guid uploadId, string fileName)
    {
        return string.Concat(tenantId.ToString("N"), "/", projectId.ToString("N"), "/", uploadId.ToString("N"), "/", fileName);
    }

    private static async Task UploadBytesAsync(IArtifactStorage storage, byte[] bytes, string storageKey, string contentType)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        await storage.UploadAsync(stream, storageKey, contentType, CancellationToken.None).ConfigureAwait(true);
    }

    private static async Task SeedCreatedProjectAsync(DbContextOptions<AppDbContext> options, Guid tenantId, Guid projectId)
    {
        var now = DateTimeOffset.UtcNow;
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = new AppDbContext(options);
            if (!await db.Tenants.AnyAsync(t => t.Id == tenantId).ConfigureAwait(true))
            {
                db.Tenants.Add(new Tenant(tenantId, $"T-{tenantId:N}", $"t-{tenantId:N}", now));
            }

            db.DubbingProjects.Add(new DubbingProject(
                projectId, tenantId, "en", "es", ProjectStatus.Created,
                "{}", new string('a', 64), null, null, now, now));
            await db.SaveChangesAsync().ConfigureAwait(true);
        }
    }

    private static async Task SeedMediaReadyAsync(DbContextOptions<AppDbContext> options, Guid tenantId, Guid projectId)
    {
        var now = DateTimeOffset.UtcNow;
        var contentId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var hash = new string('a', 64);
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = new AppDbContext(options);
            if (!await db.Tenants.AnyAsync(t => t.Id == tenantId).ConfigureAwait(true))
            {
                db.Tenants.Add(new Tenant(tenantId, $"T-{tenantId:N}", $"t-{tenantId:N}", now));
            }

            db.Set<ContentObject>().Add(new ContentObject(
                contentId, tenantId, hash, hash, 1024, "video/mp4",
                string.Concat(tenantId.ToString("N"), "/seed/source.mp4"),
                ContentObjectStatus.Committed, now, now));
            db.Set<MediaAsset>().Add(new MediaAsset(
                assetId, tenantId, projectId, contentId, "source.mp4", "mp4", "aac", "h264",
                1024, 2000, 48000, 2, "stereo", MediaAssetStatus.Valid, null, hash, now));
            db.DubbingProjects.Add(new DubbingProject(
                projectId, tenantId, "en", "es", ProjectStatus.MediaReady,
                "{}", new string('b', 64), assetId, null, now, now));
            await db.SaveChangesAsync().ConfigureAwait(true);
        }
    }

    private static async Task CreateUploadSessionAsync(
        DbContextOptions<AppDbContext> options,
        Guid tenantId,
        Guid projectId,
        Guid uploadId,
        string fileName,
        string contentType,
        long declaredSize,
        string storageKey,
        UploadStatus status)
    {
        var now = DateTimeOffset.UtcNow;
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = new AppDbContext(options);
            db.UploadSessions.Add(new UploadSession(
                uploadId, tenantId, projectId, fileName, contentType,
                declaredSize, PartSizeBytes, storageKey, Guid.NewGuid().ToString("N"),
                status, null, null, status == UploadStatus.Completed ? 1 : 0, now, now.AddDays(7)));
            await db.SaveChangesAsync().ConfigureAwait(true);
        }
    }

    private async Task EnsureFfmpegAsync()
    {
        try
        {
            var runner = new ProcessRunner(NullLogger<ProcessRunner>.Instance);
            var workingDir = ProcessRunner.CreateTempWorkingDir();
            try
            {
                var result = await runner.RunAsync(
                    "ffprobe",
                    ["-version"],
                    workingDir,
                    TimeSpan.FromSeconds(15),
                    CancellationToken.None).ConfigureAwait(true);
                if (!result.TimedOut && result.ExitCode == 0)
                {
                    return;
                }

                Skip.If(true, $"ffmpeg/ffprobe missing: ffprobe -version exited {result.ExitCode} (timedOut={result.TimedOut}). Install ffmpeg to run audio prep tests.");
            }
            finally
            {
                try
                {
                    Directory.Delete(workingDir, recursive: true);
                }
                catch (Exception)
                {
                    // Best effort.
                }
            }
        }
#pragma warning disable CA1031 // Availability probe: any failure means skip with an explicit message.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _output.WriteLine($"ffmpeg/ffprobe missing, skipping audio prep test: {ex.Message}");
            Skip.If(true, $"ffmpeg/ffprobe missing: {ex.Message}. Install ffmpeg to run audio prep tests.");
        }

        throw new InvalidOperationException("Unreachable: Skip.If always throws.");
    }

    private async Task<string> GenerateValidMp4Async()
    {
        var output = Path.Combine(Path.GetTempPath(), string.Concat("dubbing-fixture-", Guid.NewGuid().ToString("N"), ".mp4"));
        var runner = new ProcessRunner(NullLogger<ProcessRunner>.Instance);
        var workingDir = ProcessRunner.CreateTempWorkingDir();
        try
        {
            var result = await runner.RunAsync(
                "ffmpeg",
                ["-y", "-f", "lavfi", "-i", "testsrc=duration=2:size=320x240:rate=10", "-f", "lavfi", "-i", "sine=frequency=440:duration=2", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", output],
                workingDir,
                TimeSpan.FromSeconds(90),
                CancellationToken.None).ConfigureAwait(true);
            if (result.TimedOut || result.ExitCode != 0)
            {
                DeleteQuietly(output);
                Skip.If(true, $"ffmpeg fixture generation failed (exit {result.ExitCode}, timedOut={result.TimedOut}): {result.StdErr}. Install a full ffmpeg build with libx264/aac.");
                throw new InvalidOperationException("Unreachable: Skip.If always throws.");
            }

            return output;
        }
        finally
        {
            try
            {
                Directory.Delete(workingDir, recursive: true);
            }
            catch (Exception)
            {
                // Best effort.
            }
        }
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception)
        {
            // Best effort.
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

        var options = global::Microsoft.Extensions.Options.Options.Create(new StorageOptions
        {
            Endpoint = endpoint,
            Bucket = Bucket,
            UseSsl = false,
            AccessKey = minio.GetAccessKey(),
            SecretKey = minio.GetSecretKey(),
        });
        return new StorageEnv(new S3ArtifactStorage(client, options), client);
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
            _output.WriteLine($"Docker/PostgreSQL unavailable, skipping audio prep test: {ex.Message}");
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
            _output.WriteLine($"Docker/MinIO unavailable, skipping audio prep test: {ex.Message}");
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

    private sealed class UnusedStorage : IArtifactStorage
    {
        public Task UploadAsync(Stream content, string storageKey, string contentType, CancellationToken ct)
        {
            throw new NotSupportedException("Storage must not be used in this test.");
        }

        public Task<Stream> DownloadAsync(string storageKey, CancellationToken ct)
        {
            throw new NotSupportedException("Storage must not be used in this test.");
        }

        public Task<bool> ExistsAsync(string storageKey, CancellationToken ct)
        {
            throw new NotSupportedException("Storage must not be used in this test.");
        }

        public Task DeleteAsync(string storageKey, CancellationToken ct)
        {
            throw new NotSupportedException("Storage must not be used in this test.");
        }

        public Task<string> GetPresignedDownloadUrlAsync(string storageKey, TimeSpan expiry, CancellationToken ct)
        {
            throw new NotSupportedException("Storage must not be used in this test.");
        }

        public Task<string> GetPresignedUploadUrlAsync(string storageKey, TimeSpan expiry, CancellationToken ct)
        {
            throw new NotSupportedException("Storage must not be used in this test.");
        }

        public Task<string?> GetStorageChecksumAsync(string storageKey, CancellationToken ct)
        {
            throw new NotSupportedException("Storage must not be used in this test.");
        }
    }
}
