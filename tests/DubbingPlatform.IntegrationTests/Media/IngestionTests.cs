using System.Data.Common;
using System.Net.Sockets;
using System.Text.Json;
using Amazon.S3;
using DubbingPlatform.Application.Abstractions;
using DubbingPlatform.Application.Errors;
using DubbingPlatform.Application.Exceptions;
using DubbingPlatform.Application.MultiTenancy;
using DubbingPlatform.Application.Options;
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
/// Task 019: upload ingestion and media validation over PG + MinIO + FFmpeg.
/// Skips with an explicit message when Docker (PG/MinIO) or ffmpeg/ffprobe is
/// unavailable (CI runs live with all three).
/// </summary>
public sealed class IngestionTests
{
    private const string Bucket = "dubbing-test";

    private const long PartSizeBytes = 8L * 1024L * 1024L;

    private readonly ITestOutputHelper _output;

    public IngestionTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [SkippableFact]
    public async Task Valid_Mp4_Becomes_MediaReady()
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
                await SeedProjectAsync(pgOptions, tenantId, projectId).ConfigureAwait(true);

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
                    var result = await ingestion.IngestAsync(tenantId, projectId, uploadId).ConfigureAwait(true);

                    Assert.False(result.IsDuplicate);
                    Assert.True(result.IsValid);
                    Assert.Equal(64, result.ContentHash.Length);

                    using (TenantContext.BeginScope(tenantId))
                    {
                        using var db = new AppDbContext(pgOptions);
                        var asset = await db.Set<MediaAsset>().FirstAsync(a => a.Id == result.MediaAssetId).ConfigureAwait(true);
                        Assert.Equal(MediaAssetStatus.Valid, asset.Status);
                        Assert.Null(asset.FailureReason);
                        Assert.Equal(result.ContentHash, asset.ContentHash);

                        var project = await db.Set<DubbingProject>().FirstAsync(p => p.Id == projectId).ConfigureAwait(true);
                        Assert.Equal(ProjectStatus.MediaReady, project.Status);

                        var content = await db.Set<ContentObject>().FirstAsync(c => c.Id == result.ContentObjectId).ConfigureAwait(true);
                        Assert.Equal(ContentObjectStatus.Committed, content.Status);

                        var execution = await db.Set<StageExecution>()
                            .FirstOrDefaultAsync(e => e.ProjectId == projectId && e.StageType == StageType.MediaValidation).ConfigureAwait(true);
                        Assert.NotNull(execution);
                        Assert.Equal(StageStatus.Completed, execution.Status);
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
    public async Task Invalid_Text_Renamed_Rejected()
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
                await SeedProjectAsync(pgOptions, tenantId, projectId).ConfigureAwait(true);

                var env = await CreateStorageEnvAsync(minio).ConfigureAwait(true);
                var bytes = System.Text.Encoding.UTF8.GetBytes("this is not a video, just text renamed to mp4");
                var uploadId = Guid.NewGuid();
                var storageKey = StorageKeyForUpload(tenantId, projectId, uploadId, "clip.mp4");
                await UploadBytesAsync(env.Storage, bytes, storageKey, "video/mp4").ConfigureAwait(true);
                await CreateUploadSessionAsync(pgOptions, tenantId, projectId, uploadId, "clip.mp4", "video/mp4", bytes.Length, storageKey, UploadStatus.Completed).ConfigureAwait(true);

                var ingestion = CreateIngestion(pgOptions, env.Storage);
                var result = await ingestion.IngestAsync(tenantId, projectId, uploadId).ConfigureAwait(true);

                Assert.False(result.IsValid);
                Assert.False(result.IsDuplicate);
                Assert.True(
                    string.Equals(result.FailureCode, ErrorCodes.MediaCorrupt, StringComparison.Ordinal)
                    || string.Equals(result.FailureCode, ErrorCodes.MediaUnsupported, StringComparison.Ordinal),
                    $"Unexpected failure code '{result.FailureCode}'.");
                Assert.Equal(ErrorCodes.MediaCorrupt, result.FailureCode);

                using (TenantContext.BeginScope(tenantId))
                {
                    using var db = new AppDbContext(pgOptions);
                    var asset = await db.Set<MediaAsset>().FirstAsync(a => a.Id == result.MediaAssetId).ConfigureAwait(true);
                    Assert.Equal(MediaAssetStatus.Invalid, asset.Status);
                    Assert.NotNull(asset.FailureReason);

                    var project = await db.Set<DubbingProject>().FirstAsync(p => p.Id == projectId).ConfigureAwait(true);
                    Assert.Equal(ProjectStatus.MediaRejected, project.Status);

                    var execution = await db.Set<StageExecution>()
                        .FirstOrDefaultAsync(e => e.ProjectId == projectId && e.StageType == StageType.MediaValidation).ConfigureAwait(true);
                    Assert.NotNull(execution);
                    Assert.Equal(StageStatus.Failed, execution.Status);
                }
            }
        }
    }

    [SkippableFact]
    public async Task Incomplete_Completion_Fails()
    {
        var pg = await StartPostgresAsync().ConfigureAwait(true);
        await using (pg.ConfigureAwait(true))
        {
            var minio = await StartMinioAsync().ConfigureAwait(true);
            await using (minio.ConfigureAwait(true))
            {
                var pgOptions = CreatePgOptions(pg);
                await MigrateAsync(pg).ConfigureAwait(true);
                var tenantId = Guid.NewGuid();
                var projectId = Guid.NewGuid();
                await SeedProjectAsync(pgOptions, tenantId, projectId).ConfigureAwait(true);

                var env = await CreateStorageEnvAsync(minio).ConfigureAwait(true);
                var uploadId = Guid.NewGuid();
                var storageKey = StorageKeyForUpload(tenantId, projectId, uploadId, "clip.mp4");
                await CreateUploadSessionAsync(pgOptions, tenantId, projectId, uploadId, "clip.mp4", "video/mp4", 1048576, storageKey, UploadStatus.InProgress).ConfigureAwait(true);

                var ingestion = CreateIngestion(pgOptions, env.Storage);
                var ex = await Assert.ThrowsAsync<ErrorCodeException>(() => ingestion.IngestAsync(tenantId, projectId, uploadId)).ConfigureAwait(true);
                Assert.Equal(ErrorCodes.UploadIncomplete, ex.ErrorCode);
            }
        }
    }

    [SkippableFact]
    public async Task Duplicate_SameProject_Links()
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
                await SeedProjectAsync(pgOptions, tenantId, projectId).ConfigureAwait(true);

                var env = await CreateStorageEnvAsync(minio).ConfigureAwait(true);
                var mp4Path = await GenerateValidMp4Async().ConfigureAwait(true);
                try
                {
                    var bytes = await File.ReadAllBytesAsync(mp4Path).ConfigureAwait(true);
                    var ingestion = CreateIngestion(pgOptions, env.Storage);

                    var firstUpload = Guid.NewGuid();
                    var firstKey = StorageKeyForUpload(tenantId, projectId, firstUpload, "a.mp4");
                    await UploadBytesAsync(env.Storage, bytes, firstKey, "video/mp4").ConfigureAwait(true);
                    await CreateUploadSessionAsync(pgOptions, tenantId, projectId, firstUpload, "a.mp4", "video/mp4", bytes.Length, firstKey, UploadStatus.Completed).ConfigureAwait(true);
                    var first = await ingestion.IngestAsync(tenantId, projectId, firstUpload).ConfigureAwait(true);
                    Assert.True(first.IsValid);
                    Assert.False(first.IsDuplicate);

                    var secondUpload = Guid.NewGuid();
                    var secondKey = StorageKeyForUpload(tenantId, projectId, secondUpload, "b.mp4");
                    await UploadBytesAsync(env.Storage, bytes, secondKey, "video/mp4").ConfigureAwait(true);
                    await CreateUploadSessionAsync(pgOptions, tenantId, projectId, secondUpload, "b.mp4", "video/mp4", bytes.Length, secondKey, UploadStatus.Completed).ConfigureAwait(true);
                    var second = await ingestion.IngestAsync(tenantId, projectId, secondUpload).ConfigureAwait(true);

                    Assert.True(second.IsDuplicate);
                    Assert.Equal(first.MediaAssetId, second.ExistingAssetId);
                    Assert.Equal(first.ContentHash, second.ContentHash);

                    using (TenantContext.BeginScope(tenantId))
                    {
                        using var db = new AppDbContext(pgOptions);
                        var assets = await db.Set<MediaAsset>()
                            .CountAsync(a => a.ProjectId == projectId && a.ContentHash == first.ContentHash).ConfigureAwait(true);
                        Assert.Equal(1, assets);

                        var contents = await db.Set<ContentObject>()
                            .CountAsync(c => c.ContentHash == first.ContentHash).ConfigureAwait(true);
                        Assert.Equal(1, contents);

                        var session = await db.Set<UploadSession>().FirstAsync(s => s.Id == secondUpload).ConfigureAwait(true);
                        Assert.Equal(UploadStatus.Duplicate, session.Status);
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
    public async Task CrossProject_Reuses_Object()
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
                var projectA = Guid.NewGuid();
                var projectB = Guid.NewGuid();
                await SeedProjectAsync(pgOptions, tenantId, projectA).ConfigureAwait(true);
                await SeedProjectAsync(pgOptions, tenantId, projectB).ConfigureAwait(true);

                var env = await CreateStorageEnvAsync(minio).ConfigureAwait(true);
                var mp4Path = await GenerateValidMp4Async().ConfigureAwait(true);
                try
                {
                    var bytes = await File.ReadAllBytesAsync(mp4Path).ConfigureAwait(true);
                    var ingestion = CreateIngestion(pgOptions, env.Storage);

                    var uploadA = Guid.NewGuid();
                    var keyA = StorageKeyForUpload(tenantId, projectA, uploadA, "a.mp4");
                    await UploadBytesAsync(env.Storage, bytes, keyA, "video/mp4").ConfigureAwait(true);
                    await CreateUploadSessionAsync(pgOptions, tenantId, projectA, uploadA, "a.mp4", "video/mp4", bytes.Length, keyA, UploadStatus.Completed).ConfigureAwait(true);
                    var first = await ingestion.IngestAsync(tenantId, projectA, uploadA).ConfigureAwait(true);
                    Assert.True(first.IsValid);

                    var uploadB = Guid.NewGuid();
                    var keyB = StorageKeyForUpload(tenantId, projectB, uploadB, "b.mp4");
                    await UploadBytesAsync(env.Storage, bytes, keyB, "video/mp4").ConfigureAwait(true);
                    await CreateUploadSessionAsync(pgOptions, tenantId, projectB, uploadB, "b.mp4", "video/mp4", bytes.Length, keyB, UploadStatus.Completed).ConfigureAwait(true);
                    var second = await ingestion.IngestAsync(tenantId, projectB, uploadB).ConfigureAwait(true);

                    Assert.False(second.IsDuplicate);
                    Assert.True(second.IsValid);
                    Assert.Equal(first.ContentObjectId, second.ContentObjectId);
                    Assert.NotEqual(first.MediaAssetId, second.MediaAssetId);

                    using (TenantContext.BeginScope(tenantId))
                    {
                        using var db = new AppDbContext(pgOptions);
                        var contents = await db.Set<ContentObject>()
                            .CountAsync(c => c.ContentHash == first.ContentHash).ConfigureAwait(true);
                        Assert.Equal(1, contents);

                        var assets = await db.Set<MediaAsset>()
                            .CountAsync(a => a.ContentHash == first.ContentHash).ConfigureAwait(true);
                        Assert.Equal(2, assets);
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
    public async Task Quota_Rejection()
    {
        var pg = await StartPostgresAsync().ConfigureAwait(true);
        await using (pg.ConfigureAwait(true))
        {
            var minio = await StartMinioAsync().ConfigureAwait(true);
            await using (minio.ConfigureAwait(true))
            {
                var pgOptions = CreatePgOptions(pg);
                await MigrateAsync(pg).ConfigureAwait(true);
                var tenantId = Guid.NewGuid();
                var projectId = Guid.NewGuid();
                await SeedProjectAsync(pgOptions, tenantId, projectId).ConfigureAwait(true);

                var env = await CreateStorageEnvAsync(minio).ConfigureAwait(true);
                var bytes = new byte[1024];
                new Random(7).NextBytes(bytes);
                var uploadId = Guid.NewGuid();
                var storageKey = StorageKeyForUpload(tenantId, projectId, uploadId, "big.mp4");
                await UploadBytesAsync(env.Storage, bytes, storageKey, "video/mp4").ConfigureAwait(true);
                await CreateUploadSessionAsync(pgOptions, tenantId, projectId, uploadId, "big.mp4", "video/mp4", bytes.Length, storageKey, UploadStatus.Completed).ConfigureAwait(true);

                var tinyQuota = Options.Create(new QuotaOptions { MaxStorageBytes = 10 });
                var ingestion = CreateIngestion(pgOptions, env.Storage, tinyQuota);
                var ex = await Assert.ThrowsAsync<QuotaExceededException>(() => ingestion.IngestAsync(tenantId, projectId, uploadId)).ConfigureAwait(true);
                Assert.Equal(ErrorCodes.QuotaExceeded, ex.ErrorCode);
            }
        }
    }

    [SkippableFact]
    public async Task Ffprobe_Artifact_Exists()
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
                await SeedProjectAsync(pgOptions, tenantId, projectId).ConfigureAwait(true);

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
                    var result = await ingestion.IngestAsync(tenantId, projectId, uploadId).ConfigureAwait(true);
                    Assert.True(result.IsValid);

                    using (TenantContext.BeginScope(tenantId))
                    {
                        using var db = new AppDbContext(pgOptions);
                        var artifact = await db.Set<Artifact>()
                            .FirstOrDefaultAsync(a => a.Id == result.FfprobeArtifactId).ConfigureAwait(true);
                        Assert.NotNull(artifact);
                        Assert.Equal(ArtifactType.FfprobeAnalysis, artifact.Type);
                        Assert.Equal(ArtifactStatus.Committed, artifact.Status);
                        Assert.NotNull(artifact.MetadataJson);
                        Assert.Contains("container", artifact.MetadataJson, StringComparison.OrdinalIgnoreCase);

                        var content = await db.Set<ContentObject>().FirstAsync(c => c.Id == artifact.ContentObjectId).ConfigureAwait(true);
                        Assert.Equal(ContentObjectStatus.Committed, content.Status);
                    }

                    using (var download = await env.Storage.DownloadAsync((await GetArtifactStorageKeyAsync(pgOptions, tenantId, result.FfprobeArtifactId).ConfigureAwait(true)), CancellationToken.None).ConfigureAwait(true))
                    {
                        using var reader = new MemoryStream();
                        await download.CopyToAsync(reader).ConfigureAwait(true);
                        var json = System.Text.Encoding.UTF8.GetString(reader.ToArray());
                        using var document = JsonDocument.Parse(json);
                        Assert.True(document.RootElement.TryGetProperty("container", out _));
                    }
                }
                finally
                {
                    DeleteQuietly(mp4Path);
                }
            }
        }
    }

    private static string StorageKeyForUpload(Guid tenantId, Guid projectId, Guid uploadId, string fileName)
    {
        return string.Concat(tenantId.ToString("N"), "/", projectId.ToString("N"), "/", uploadId.ToString("N"), "/", fileName);
    }

    private MediaIngestionService CreateIngestion(DbContextOptions<AppDbContext> pgOptions, IArtifactStorage storage, IOptions<QuotaOptions>? quotaOverride = null)
    {
        var factory = new TestFactory(pgOptions);
        var artifacts = new ArtifactService(factory, storage);
        var runner = new ProcessRunner(NullLogger<ProcessRunner>.Instance);
        var ffprobe = new FFprobeService(storage, runner, Options.Create(new MediaOptions()), NullLogger<FFprobeService>.Instance);
        var quota = new StorageQuotaGate(factory, quotaOverride ?? Options.Create(new QuotaOptions()));
        return new MediaIngestionService(
            factory, storage, artifacts, ffprobe, quota,
            Options.Create(new MediaOptions()), NullLogger<MediaIngestionService>.Instance);
    }

    private static async Task UploadBytesAsync(IArtifactStorage storage, byte[] bytes, string storageKey, string contentType)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        await storage.UploadAsync(stream, storageKey, contentType, CancellationToken.None).ConfigureAwait(true);
    }

    private static async Task<string> GetArtifactStorageKeyAsync(DbContextOptions<AppDbContext> options, Guid tenantId, Guid artifactId)
    {
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = new AppDbContext(options);
            var artifact = await db.Set<Artifact>().FirstAsync(a => a.Id == artifactId).ConfigureAwait(true);
            var content = await db.Set<ContentObject>().FirstAsync(c => c.Id == artifact.ContentObjectId).ConfigureAwait(true);
            return content.StorageKey;
        }
    }

    private static async Task SeedProjectAsync(DbContextOptions<AppDbContext> options, Guid tenantId, Guid projectId)
    {
        var now = DateTimeOffset.UtcNow;
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = new AppDbContext(options);
            if (!await db.Tenants.AnyAsync(t => t.Id == tenantId).ConfigureAwait(true))
            {
                db.Tenants.Add(new Tenant(tenantId, $"T-{tenantId:N}", $"t-{tenantId:N}", now));
            }

            if (!await db.DubbingProjects.AnyAsync(p => p.Id == projectId).ConfigureAwait(true))
            {
                db.DubbingProjects.Add(new DubbingProject(
                    projectId, tenantId, "en", "es", ProjectStatus.Created,
                    "{}", new string('a', 64), null, null, now, now));
            }

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

                Skip.If(true, $"ffmpeg/ffprobe missing: ffprobe -version exited {result.ExitCode} (timedOut={result.TimedOut}). Install ffmpeg to run ingestion tests.");
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
            _output.WriteLine($"ffmpeg/ffprobe missing, skipping ingestion test: {ex.Message}");
            Skip.If(true, $"ffmpeg/ffprobe missing: {ex.Message}. Install ffmpeg to run ingestion tests.");
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
            _output.WriteLine($"Docker/PostgreSQL unavailable, skipping ingestion test: {ex.Message}");
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
            _output.WriteLine($"Docker/MinIO unavailable, skipping ingestion test: {ex.Message}");
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
}
