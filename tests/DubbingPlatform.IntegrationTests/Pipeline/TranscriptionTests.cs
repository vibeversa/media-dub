using System.Data.Common;
using System.Net.Sockets;
using System.Text;
using DubbingPlatform.Application.Abstractions;
using DubbingPlatform.Application.Abstractions.Providers;
using DubbingPlatform.Application.Abstractions.Providers.Dtos;
using DubbingPlatform.Application.MultiTenancy;
using DubbingPlatform.Application.Options;
using DubbingPlatform.Application.Providers;
using DubbingPlatform.Application.Services;
using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Domain.Enums;
using DubbingPlatform.Infrastructure.Persistence;
using DubbingPlatform.Infrastructure.Persistence.Interceptors;
using EFCore.NamingConventions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Testcontainers.PostgreSql;
using Xunit.Abstractions;

namespace DubbingPlatform.IntegrationTests.Pipeline;

/// <summary>
/// Task 024: versioned segment transcription with word artifacts, confidence
/// policy (fallback then review), barrier accounting, and provider-execution
/// recording over PG with fake storage/ffmpeg/provider/descriptors (no
/// MinIO/ffmpeg needed). Skips with an explicit message when Docker (PG) is
/// unavailable (CI live).
/// </summary>
public sealed class TranscriptionTests
{
    private readonly ITestOutputHelper _output;

    public TranscriptionTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [SkippableFact]
    public async Task Clear_Speech_Selected()
    {
        var pg = await StartPostgresAsync().ConfigureAwait(true);
        await using (pg.ConfigureAwait(true))
        {
            var pgOptions = CreatePgOptions(pg);
            await MigrateAsync(pg).ConfigureAwait(true);
            var tenantId = Guid.NewGuid();
            var projectId = Guid.NewGuid();
            var runId = Guid.NewGuid();
            await SeedProjectRunAsync(pgOptions, tenantId, projectId, runId).ConfigureAwait(true);
            var segmentId = await InsertSegmentAsync(pgOptions, tenantId, projectId, runId, 0, 0, 2000).ConfigureAwait(true);

            var storage = new FakeStorage();
            var artifacts = new ArtifactService(new TestFactory(pgOptions), storage);
            await PublishCanonicalAsync(artifacts, tenantId, projectId, runId).ConfigureAwait(true);
            var claim = await ClaimSegmentAsync(pgOptions, tenantId, projectId, runId, segmentId).ConfigureAwait(true);

            var fake = new FakeTranscription([FakeTranscription.Clear(0.95, "hello world")]);
            var service = CreateService(pgOptions, storage, fake, descriptorCount: 1);
            var result = await service.TranscribeSegmentAsync(
                tenantId, projectId, runId, segmentId, 0,
                claim.Execution.Id, claim.Execution.LeaseOwner, claim.Execution.LeaseToken).ConfigureAwait(true);

            Assert.False(result.NeedsReview);
            Assert.Null(result.ReviewItemId);
            Assert.Equal(0.95, result.Confidence, precision: 9);
            Assert.Equal("Mock", result.Provider);

            using (TenantContext.BeginScope(tenantId))
            {
                using var db = new AppDbContext(pgOptions);
                var versions = await db.Set<TranscriptVersion>().Where(v => v.SegmentId == segmentId).ToListAsync().ConfigureAwait(true);
                var selected = Assert.Single(versions);
                Assert.True(selected.IsSelected);
                Assert.False(selected.NeedsReview);
                Assert.Equal("hello world", selected.Text);
                Assert.Equal(result.SelectedVersionId, selected.Id);
                Assert.Equal(result.WordsArtifactId, selected.WordTimestampsArtifactId);

                var words = await db.Set<Artifact>().FirstAsync(a => a.Id == result.WordsArtifactId).ConfigureAwait(true);
                Assert.Equal(ArtifactType.Transcript, words.Type);
                Assert.NotNull(words.MetadataJson);
                Assert.Contains("\"schemaVersion\":\"1\"", words.MetadataJson, StringComparison.Ordinal);
                Assert.Contains(segmentId.ToString("N"), words.MetadataJson, StringComparison.Ordinal);

                var execution = await db.Set<StageExecution>().FirstAsync(e => e.Id == claim.Execution.Id).ConfigureAwait(true);
                Assert.Equal(StageStatus.Completed, execution.Status);
                Assert.NotNull(execution.OutputArtifactIdsJson);
                Assert.Contains(result.WordsArtifactId.ToString("N"), execution.OutputArtifactIdsJson, StringComparison.Ordinal);

                var reviews = await db.Set<ReviewItem>().CountAsync(r => r.ProcessingRunId == runId).ConfigureAwait(true);
                Assert.Equal(0, reviews);
            }
        }
    }

    [SkippableFact]
    public async Task Low_Confidence_Fallback()
    {
        var pg = await StartPostgresAsync().ConfigureAwait(true);
        await using (pg.ConfigureAwait(true))
        {
            var pgOptions = CreatePgOptions(pg);
            await MigrateAsync(pg).ConfigureAwait(true);
            var tenantId = Guid.NewGuid();
            var projectId = Guid.NewGuid();
            var runId = Guid.NewGuid();
            await SeedProjectRunAsync(pgOptions, tenantId, projectId, runId).ConfigureAwait(true);
            var segmentId = await InsertSegmentAsync(pgOptions, tenantId, projectId, runId, 0, 0, 2000).ConfigureAwait(true);

            var storage = new FakeStorage();
            var artifacts = new ArtifactService(new TestFactory(pgOptions), storage);
            await PublishCanonicalAsync(artifacts, tenantId, projectId, runId).ConfigureAwait(true);
            var claim = await ClaimSegmentAsync(pgOptions, tenantId, projectId, runId, segmentId).ConfigureAwait(true);

            var fake = new FakeTranscription(
            [
                FakeTranscription.Clear(0.35, "mumble mumble"),
                FakeTranscription.Clear(0.9, "hello world clearly"),
            ]);
            var service = CreateService(pgOptions, storage, fake, descriptorCount: 2);
            var result = await service.TranscribeSegmentAsync(
                tenantId, projectId, runId, segmentId, 0,
                claim.Execution.Id, claim.Execution.LeaseOwner, claim.Execution.LeaseToken).ConfigureAwait(true);

            Assert.False(result.NeedsReview);
            Assert.Equal(0.9, result.Confidence, precision: 9);
            Assert.Equal(2, fake.Calls);

            using (TenantContext.BeginScope(tenantId))
            {
                using var db = new AppDbContext(pgOptions);
                var versions = await db.Set<TranscriptVersion>().Where(v => v.SegmentId == segmentId).ToListAsync().ConfigureAwait(true);
                Assert.Equal(2, versions.Count);
                var winner = Assert.Single(versions, v => v.IsSelected);
                Assert.Equal(result.SelectedVersionId, winner.Id);
                Assert.Equal("hello world clearly", winner.Text);

                var executions = await db.Set<ProviderExecution>().Where(e => e.ProcessingRunId == runId).ToListAsync().ConfigureAwait(true);
                Assert.Equal(2, executions.Count);
                Assert.All(executions, e => Assert.Equal(ProviderCapability.Transcription, e.Capability));
            }
        }
    }

    [SkippableFact]
    public async Task Persistent_Low_Creates_Review()
    {
        var pg = await StartPostgresAsync().ConfigureAwait(true);
        await using (pg.ConfigureAwait(true))
        {
            var pgOptions = CreatePgOptions(pg);
            await MigrateAsync(pg).ConfigureAwait(true);
            var tenantId = Guid.NewGuid();
            var projectId = Guid.NewGuid();
            var runId = Guid.NewGuid();
            await SeedProjectRunAsync(pgOptions, tenantId, projectId, runId).ConfigureAwait(true);
            var segmentId = await InsertSegmentAsync(pgOptions, tenantId, projectId, runId, 0, 0, 2000).ConfigureAwait(true);
            var retrySegmentId = await InsertSegmentAsync(pgOptions, tenantId, projectId, runId, 1, 2000, 4000).ConfigureAwait(true);

            var storage = new FakeStorage();
            var artifacts = new ArtifactService(new TestFactory(pgOptions), storage);
            await PublishCanonicalAsync(artifacts, tenantId, projectId, runId).ConfigureAwait(true);
            var retryClaim = await ClaimSegmentAsync(pgOptions, tenantId, projectId, runId, retrySegmentId).ConfigureAwait(true);

            var service = CreateService(pgOptions, storage, new FakeTranscription([]), descriptorCount: 1);

            var retryable = await Assert.ThrowsAsync<TranscriptionRetryableException>(() => service.TranscribeSegmentAsync(
                tenantId, projectId, runId, retrySegmentId, 0,
                retryClaim.Execution.Id, retryClaim.Execution.LeaseOwner, retryClaim.Execution.LeaseToken)).ConfigureAwait(true);
            Assert.Equal(retrySegmentId, retryable.SegmentId);

            var lastAttemptService = CreateService(pgOptions, storage, new FakeTranscription([]), descriptorCount: 1);
            var stages = new StageExecutionService(new TestFactory(pgOptions), global::Microsoft.Extensions.Options.Options.Create(new RetryOptions()));
            StageExecution lastExecution;
            using (TenantContext.BeginScope(tenantId))
            {
                var lastClaim = await stages.ClaimAsync(
                    tenantId, projectId, runId,
                    nameof(StageType.Transcription), nameof(ScopeType.Segment), segmentId.ToString("D"),
                    segmentId, 2, "test-owner", TimeSpan.FromMinutes(5)).ConfigureAwait(true);
                lastExecution = lastClaim.Execution;
            }

            var result = await lastAttemptService.TranscribeSegmentAsync(
                tenantId, projectId, runId, segmentId, 2,
                lastExecution.Id, lastExecution.LeaseOwner, lastExecution.LeaseToken).ConfigureAwait(true);

            Assert.True(result.NeedsReview);
            Assert.NotNull(result.ReviewItemId);

            using (TenantContext.BeginScope(tenantId))
            {
                using var db = new AppDbContext(pgOptions);
                var execution = await db.Set<StageExecution>().FirstAsync(e => e.Id == lastExecution.Id).ConfigureAwait(true);
                Assert.Equal(StageStatus.ManualReviewRequired, execution.Status);

                var review = await db.Set<ReviewItem>().FirstAsync(r => r.Id == result.ReviewItemId!.Value).ConfigureAwait(true);
                Assert.Equal(ReviewStatus.Open, review.Status);
                Assert.Equal(TranscriptionService.ReviewReason, review.Reason);
                Assert.Equal(ScopeType.Segment, review.ScopeType);
                Assert.Equal(segmentId, review.SegmentId);

                var winner = await db.Set<TranscriptVersion>().FirstAsync(v => v.Id == result.SelectedVersionId).ConfigureAwait(true);
                Assert.True(winner.NeedsReview);
                Assert.True(winner.IsSelected);
            }
        }
    }

    [SkippableFact]
    public async Task Barrier_Correct()
    {
        var pg = await StartPostgresAsync().ConfigureAwait(true);
        await using (pg.ConfigureAwait(true))
        {
            var pgOptions = CreatePgOptions(pg);
            await MigrateAsync(pg).ConfigureAwait(true);
            var tenantId = Guid.NewGuid();
            var projectId = Guid.NewGuid();
            var runId = Guid.NewGuid();
            await SeedProjectRunAsync(pgOptions, tenantId, projectId, runId).ConfigureAwait(true);
            var firstId = await InsertSegmentAsync(pgOptions, tenantId, projectId, runId, 0, 0, 2000).ConfigureAwait(true);
            var secondId = await InsertSegmentAsync(pgOptions, tenantId, projectId, runId, 1, 2000, 4000).ConfigureAwait(true);

            var storage = new FakeStorage();
            var artifacts = new ArtifactService(new TestFactory(pgOptions), storage);
            await PublishCanonicalAsync(artifacts, tenantId, projectId, runId).ConfigureAwait(true);
            var firstClaim = await ClaimSegmentAsync(pgOptions, tenantId, projectId, runId, firstId).ConfigureAwait(true);
            var secondClaim = await ClaimSegmentAsync(pgOptions, tenantId, projectId, runId, secondId).ConfigureAwait(true);

            var service = CreateService(pgOptions, storage, new FakeTranscription([]), descriptorCount: 1);
            await service.TranscribeSegmentAsync(
                tenantId, projectId, runId, firstId, 0,
                firstClaim.Execution.Id, firstClaim.Execution.LeaseOwner, firstClaim.Execution.LeaseToken).ConfigureAwait(true);
            await service.TranscribeSegmentAsync(
                tenantId, projectId, runId, secondId, 0,
                secondClaim.Execution.Id, secondClaim.Execution.LeaseOwner, secondClaim.Execution.LeaseToken).ConfigureAwait(true);

            var barrier = new BarrierService(new TestFactory(pgOptions));
            await barrier.EnsureSummaryAsync(tenantId, runId, StageType.Transcription, 2).ConfigureAwait(true);
            var first = await barrier.RecordUnitCompletionAsync(
                tenantId, runId, StageType.Transcription, ScopeType.Segment, firstId.ToString("D"),
                firstClaim.Execution.Id, "Completed").ConfigureAwait(true);
            Assert.False(first.StageComplete);
            var second = await barrier.RecordUnitCompletionAsync(
                tenantId, runId, StageType.Transcription, ScopeType.Segment, secondId.ToString("D"),
                secondClaim.Execution.Id, "Completed").ConfigureAwait(true);
            Assert.True(second.StageComplete);

            var summary = await barrier.GetSummaryAsync(tenantId, runId, StageType.Transcription).ConfigureAwait(true);
            Assert.NotNull(summary);
            Assert.Equal(2, summary.ExpectedUnits);
            Assert.Equal(2, summary.CompletedUnits);
        }
    }

    [SkippableFact]
    public async Task Execution_Recorded()
    {
        var pg = await StartPostgresAsync().ConfigureAwait(true);
        await using (pg.ConfigureAwait(true))
        {
            var pgOptions = CreatePgOptions(pg);
            await MigrateAsync(pg).ConfigureAwait(true);
            var tenantId = Guid.NewGuid();
            var projectId = Guid.NewGuid();
            var runId = Guid.NewGuid();
            await SeedProjectRunAsync(pgOptions, tenantId, projectId, runId).ConfigureAwait(true);
            var segmentId = await InsertSegmentAsync(pgOptions, tenantId, projectId, runId, 0, 0, 2000).ConfigureAwait(true);

            var storage = new FakeStorage();
            var artifacts = new ArtifactService(new TestFactory(pgOptions), storage);
            await PublishCanonicalAsync(artifacts, tenantId, projectId, runId).ConfigureAwait(true);
            var claim = await ClaimSegmentAsync(pgOptions, tenantId, projectId, runId, segmentId).ConfigureAwait(true);

            var service = CreateService(pgOptions, storage, new FakeTranscription([]), descriptorCount: 1);
            await service.TranscribeSegmentAsync(
                tenantId, projectId, runId, segmentId, 0,
                claim.Execution.Id, claim.Execution.LeaseOwner, claim.Execution.LeaseToken).ConfigureAwait(true);

            using (TenantContext.BeginScope(tenantId))
            {
                using var db = new AppDbContext(pgOptions);
                var row = await db.Set<ProviderExecution>()
                    .Where(e => e.ProcessingRunId == runId)
                    .FirstOrDefaultAsync().ConfigureAwait(true);
                Assert.NotNull(row);
                Assert.Equal(ProviderType.Mock, row.Provider);
                Assert.Equal(ProviderCapability.Transcription, row.Capability);
                Assert.Equal(OutcomeClass.Success, row.Outcome);
                Assert.Equal(claim.Execution.Id, row.StageExecutionId);
                Assert.NotNull(row.ProviderIdempotencyKey);
                Assert.Contains("Transcription", row.ProviderIdempotencyKey, StringComparison.Ordinal);
                Assert.NotNull(row.RequestHash);
                Assert.NotNull(row.ResponseHash);
            }
        }
    }

    private TranscriptionService CreateService(
        DbContextOptions<AppDbContext> pgOptions,
        IArtifactStorage storage,
        ITranscriptionProvider transcription,
        int descriptorCount)
    {
        var factory = new TestFactory(pgOptions);
        var artifacts = new ArtifactService(factory, storage);
        var ffmpeg = new FakeFfmpeg();
        var descriptors = new FakeDescriptors(descriptorCount);
        var healthMock = new Moq.Mock<IProviderHealthTracker>(Moq.MockBehavior.Strict);
        healthMock.Setup(h => h.IsHealthy(Moq.It.IsAny<ProviderType>())).Returns(true);
        var costMock = new Moq.Mock<IProviderCostGate>(Moq.MockBehavior.Strict);
        costMock.Setup(c => c.CanProceedAsync(Moq.It.IsAny<Guid>(), Moq.It.IsAny<ProviderCapability>(), Moq.It.IsAny<CancellationToken>())).Returns(Task.FromResult(true));
        var policyMock = new Moq.Mock<IProcessingPolicyProvider>(Moq.MockBehavior.Strict);
        policyMock.Setup(p => p.GetAsync(Moq.It.IsAny<Guid>(), Moq.It.IsAny<CancellationToken>())).Returns(Task.FromResult<ProcessingPolicy?>(null));
        var resolver = new ProviderResolver(
            global::Microsoft.Extensions.Options.Options.Create(new ProviderOptions()),
            global::Microsoft.Extensions.Options.Options.Create(new PrivacyOptions()),
            global::Microsoft.Extensions.Options.Options.Create(new RetryOptions()),
            descriptors,
            healthMock.Object,
            costMock.Object,
            policyMock.Object);
        var recorder = new ProviderExecutionRecorder(factory);
        return new TranscriptionService(
            factory, artifacts, storage, ffmpeg, transcription, resolver, descriptors, recorder,
            costMock.Object,
            global::Microsoft.Extensions.Options.Options.Create(new TranscriptionOptions()),
            global::Microsoft.Extensions.Options.Options.Create(new ProviderOptions()),
            global::Microsoft.Extensions.Options.Options.Create(new RetryOptions()),
            NullLogger<TranscriptionService>.Instance);
    }

    private static async Task<StageClaimResult> ClaimSegmentAsync(
        DbContextOptions<AppDbContext> pgOptions,
        Guid tenantId,
        Guid projectId,
        Guid runId,
        Guid segmentId)
    {
        var stages = new StageExecutionService(new TestFactory(pgOptions), global::Microsoft.Extensions.Options.Options.Create(new RetryOptions()));
        using (TenantContext.BeginScope(tenantId))
        {
            return await stages.ClaimAsync(
                tenantId, projectId, runId,
                nameof(StageType.Transcription), nameof(ScopeType.Segment), segmentId.ToString("D"),
                segmentId, 0, "test-owner", TimeSpan.FromMinutes(5)).ConfigureAwait(true);
        }
    }

    private static async Task<Guid> InsertSegmentAsync(
        DbContextOptions<AppDbContext> options,
        Guid tenantId,
        Guid projectId,
        Guid runId,
        int sequence,
        int startMs,
        int endMs)
    {
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = new AppDbContext(options);
            db.Set<SpeechSegment>().Add(new SpeechSegment(
                id, tenantId, projectId, runId, sequence, startMs, endMs,
                "Pending", null, now));
            await db.SaveChangesAsync().ConfigureAwait(true);
        }

        return id;
    }

    private static async Task PublishCanonicalAsync(
        ArtifactService artifacts,
        Guid tenantId,
        Guid projectId,
        Guid runId)
    {
        var bytes = Encoding.UTF8.GetBytes("canonical-flac-bytes-for-transcription-tests");
        using var stream = new MemoryStream(bytes, writable: false);
        await artifacts.PublishAsync(
            tenantId, projectId, runId,
            StageType.AudioPreparation, ArtifactType.CanonicalAudio,
            stream, ".flac", "audio/flac",
            null, null, null, null, [],
            cancellationToken: CancellationToken.None).ConfigureAwait(true);
    }

    private static async Task SeedProjectRunAsync(
        DbContextOptions<AppDbContext> options,
        Guid tenantId,
        Guid projectId,
        Guid runId)
    {
        var now = DateTimeOffset.UtcNow;
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = new AppDbContext(options);
            if (!await db.Tenants.AnyAsync(t => t.Id == tenantId).ConfigureAwait(true))
            {
                db.Tenants.Add(new Tenant(tenantId, $"T-{tenantId:N}", $"t-{tenantId:N}", now));
            }

            var contentId = Guid.NewGuid();
            var hash = new string('c', 64);
            db.Set<ContentObject>().Add(new ContentObject(
                contentId, tenantId, hash, hash, 1024, "audio/flac",
                string.Concat(tenantId.ToString("N"), "/seed/canonical.flac"),
                ContentObjectStatus.Committed, now, now));
            db.Set<MediaAsset>().Add(new MediaAsset(
                Guid.NewGuid(), tenantId, projectId, contentId, "canonical.flac", "flac", "flac", null,
                1024, 4000, 48000, 2, "stereo", MediaAssetStatus.Valid, null, hash, now));
            db.DubbingProjects.Add(new DubbingProject(
                projectId, tenantId, "en", "es", ProjectStatus.Processing,
                "{}", new string('b', 64), null, runId, now, now));
            db.Set<ProcessingRun>().Add(new ProcessingRun(
                runId, tenantId, projectId, 0, ProcessingRunStatus.Running,
                "1.0.0", new string('b', 64), new string('d', 64), new string('e', 64),
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
            _output.WriteLine($"Docker/PostgreSQL unavailable, skipping transcription test: {ex.Message}");
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

    private sealed class FakeStorage : IArtifactStorage
    {
        private readonly Dictionary<string, byte[]> _blobs = new(StringComparer.Ordinal);

        public Task UploadAsync(Stream content, string storageKey, string contentType, CancellationToken ct)
        {
            using var memory = new MemoryStream();
            content.CopyTo(memory);
            _blobs[storageKey] = memory.ToArray();
            return Task.CompletedTask;
        }

        public Task<Stream> DownloadAsync(string storageKey, CancellationToken ct)
        {
            if (!_blobs.TryGetValue(storageKey, out var bytes))
            {
                throw new InvalidOperationException($"Blob '{storageKey}' was not found.");
            }

            return Task.FromResult<Stream>(new MemoryStream(bytes, writable: false));
        }

        public Task<bool> ExistsAsync(string storageKey, CancellationToken ct)
        {
            return Task.FromResult(_blobs.ContainsKey(storageKey));
        }

        public Task DeleteAsync(string storageKey, CancellationToken ct)
        {
            _blobs.Remove(storageKey);
            return Task.CompletedTask;
        }

        public Task<string> GetPresignedDownloadUrlAsync(string storageKey, TimeSpan expiry, CancellationToken ct)
        {
            return Task.FromResult(string.Concat("https://fake/", storageKey));
        }

        public Task<string> GetPresignedUploadUrlAsync(string storageKey, TimeSpan expiry, CancellationToken ct)
        {
            return Task.FromResult(string.Concat("https://fake/", storageKey));
        }

        public Task<string?> GetStorageChecksumAsync(string storageKey, CancellationToken ct)
        {
            return Task.FromResult<string?>(null);
        }
    }

    private sealed class FakeFfmpeg : IFFmpegService
    {
        public Task<FfmpegResult> RunAsync(IReadOnlyList<string> args, string workDir, CancellationToken cancellationToken)
        {
            throw new NotSupportedException("FakeFfmpeg.RunAsync is not used by transcription tests.");
        }

        public IReadOnlyList<string> BuildCanonicalArgs(string sourcePath, string destPath, int cpuThreads)
        {
            throw new NotSupportedException("FakeFfmpeg.BuildCanonicalArgs is not used by transcription tests.");
        }

        public IReadOnlyList<string> BuildFloatArgs(string sourcePath, string destPath, int cpuThreads)
        {
            throw new NotSupportedException("FakeFfmpeg.BuildFloatArgs is not used by transcription tests.");
        }

        public Task<FfmpegResult> ExtractCanonicalAudioAsync(string sourcePath, string destFlacPath, CancellationToken cancellationToken, bool needsFloatWork = false)
        {
            throw new NotSupportedException("FakeFfmpeg.ExtractCanonicalAudioAsync is not used by transcription tests.");
        }

        public IReadOnlyList<string> BuildSliceArgs(string sourcePath, string destPath, double startSec, double durationSec, int cpuThreads)
        {
            return ["-ss", startSec.ToString(System.Globalization.CultureInfo.InvariantCulture), "-i", sourcePath, "-t", durationSec.ToString(System.Globalization.CultureInfo.InvariantCulture), destPath];
        }

        public Task<FfmpegResult> ExtractSegmentSliceAsync(string sourcePath, string destWavPath, int startMs, int durationMs, CancellationToken cancellationToken)
        {
            if (startMs < 0 || durationMs <= 0)
            {
                throw new DubbingPlatform.Application.Exceptions.ErrorCodeException(
                    DubbingPlatform.Application.Errors.ErrorCodes.ValidationFailed, "Segment audio slice is empty; transcription cannot proceed.");
            }

            File.WriteAllBytes(destWavPath, new byte[640]);
            return Task.FromResult(new FfmpegResult(
                0,
                BuildSliceArgs(sourcePath, destWavPath, startMs / 1000.0, durationMs / 1000.0, 1),
                TimeSpan.Zero, false, destWavPath));
        }

        public Task<DubbingPlatform.Application.Abstractions.SignalLoudness> MeasureSignalLoudnessAsync(string file, string workDir, CancellationToken cancellationToken)
        {
            throw new NotSupportedException("FakeFfmpeg.MeasureSignalLoudnessAsync is not used by transcription tests.");
        }

        public Task<IReadOnlyList<double>> MeasureChannelRmsDbAsync(string file, string workDir, int? startMs, int? durationMs, CancellationToken cancellationToken)
        {
            throw new NotSupportedException("FakeFfmpeg.MeasureChannelRmsDbAsync is not used by transcription tests.");
        }

        public Task<double> MeasureMaxSilenceSecAsync(string file, string workDir, double silenceThresholdDb, double minSilenceSec, CancellationToken cancellationToken)
        {
            throw new NotSupportedException("FakeFfmpeg.MeasureMaxSilenceSecAsync is not used by transcription tests.");
        }
    }

    private sealed class FakeTranscription : ITranscriptionProvider
    {
        private readonly Queue<object> _script;

        public FakeTranscription(IEnumerable<object> script)
        {
            _script = new Queue<object>(script);
        }

        public int Calls { get; private set; }

        public static TranscriptionResponse Clear(double confidence, string text)
        {
            var tokens = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var words = tokens
                .Select((token, index) => new DubbingPlatform.Application.Abstractions.Providers.Dtos.WordTimestamp(token, index * 300, (index + 1) * 300, confidence))
                .ToList();
            return new TranscriptionResponse(
                text, confidence, words, "mock-1", "1", "mock",
                new ProviderUsage(text.Length / 4, words.Count, 2.0, 0), null);
        }

        public Task<TranscriptionResponse> TranscribeAsync(TranscriptionRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            if (_script.Count > 0)
            {
                var next = _script.Dequeue();
                if (next is Exception failure)
                {
                    return Task.FromException<TranscriptionResponse>(failure);
                }

                return Task.FromResult((TranscriptionResponse)next);
            }

            return Task.FromResult(Clear(0.35, "mumble mumble"));
        }
    }

    private sealed class FakeDescriptors : IDescriptorStore
    {
        private readonly int _count;

        public FakeDescriptors(int count)
        {
            _count = count;
        }

        public Task<IReadOnlyList<ProviderCapabilityDescriptor>> GetCandidatesAsync(
            ProviderCapability capability,
            Guid tenantId,
            CancellationToken cancellationToken = default)
        {
            var now = DateTimeOffset.UtcNow;
            var candidates = new List<ProviderCapabilityDescriptor>(_count);
            for (var i = 0; i < _count; i++)
            {
                candidates.Add(new ProviderCapabilityDescriptor(
                    Guid.NewGuid(), tenantId, ProviderType.Mock, capability,
                    [], [], 0, 0, false, false, true, true, [], true, [],
                    "0-1", "{}", "{}", "standard", "global", 1, now));
            }

            return Task.FromResult<IReadOnlyList<ProviderCapabilityDescriptor>>(candidates);
        }

        public bool IsCompatible(ProviderCapabilityDescriptor descriptor, ProviderRoutingRequest request)
        {
            return true;
        }
    }
}
