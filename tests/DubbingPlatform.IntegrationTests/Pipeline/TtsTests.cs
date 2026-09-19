using System.Data.Common;
using System.Net.Sockets;
using System.Text;
using DubbingPlatform.Application.Abstractions;
using DubbingPlatform.Application.Abstractions.Providers;
using DubbingPlatform.Application.Abstractions.Providers.Dtos;
using DubbingPlatform.Application.Errors;
using DubbingPlatform.Application.Exceptions;
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
/// Task 028: TTS generation with estimator pre-adjustment, preview/final
/// separation, cost reservation, FFprobe validation, and execution recording
/// over PG with fake storage/provider/ffprobe/descriptors (no MinIO or ffmpeg
/// needed; the fake FFprobe validates the WAV structure the real FFprobe
/// accepts). Skips with an explicit message when Docker (PG) is unavailable
/// (CI live).
/// </summary>
public sealed class TtsTests
{
    private readonly ITestOutputHelper _output;

    public TtsTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [SkippableFact]
    public async Task Readable_By_Ffprobe()
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
            var speakerId = await InsertSpeakerAsync(pgOptions, tenantId, projectId).ConfigureAwait(true);
            await InsertVoiceAsync(pgOptions, tenantId, projectId, runId, speakerId).ConfigureAwait(true);
            var segmentId = await InsertSegmentAsync(pgOptions, tenantId, projectId, runId, speakerId, 0, 0, 2000).ConfigureAwait(true);
            var storage = new FakeStorage();
            var artifacts = new ArtifactService(new TestFactory(pgOptions), storage);
            var translationArtifactId = await PublishTranslationArtifactAsync(pgOptions, artifacts, tenantId, projectId, runId, segmentId).ConfigureAwait(true);
            await InsertTranslationAsync(pgOptions, tenantId, projectId, runId, segmentId, "hola mundo", translationArtifactId).ConfigureAwait(true);
            var claim = await ClaimSegmentAsync(pgOptions, tenantId, projectId, runId, segmentId, attempt: 0).ConfigureAwait(true);

            var fake = new FakeTts([FakeTts.Success(durationMs: 1200)]);
            var ffprobe = new FakeFfprobe();
            var service = CreateService(pgOptions, storage, fake, ffprobe, allowCost: true, descriptorCount: 1, new TtsOptions());
            var result = await service.GenerateAsync(
                tenantId, projectId, runId, segmentId, isPreview: false, 0,
                claim.Execution.Id, claim.Execution.LeaseOwner, claim.Execution.LeaseToken).ConfigureAwait(true);

            Assert.False(result.NeedsReview);
            Assert.False(result.IsPreview);
            Assert.True(result.DurationMs > 0);
            Assert.Equal(1200, result.DurationMs);

            using (TenantContext.BeginScope(tenantId))
            {
                using var db = new AppDbContext(pgOptions);
                var generated = await db.Set<GeneratedAudioArtifact>().FirstAsync(g => g.Id == result.GeneratedAudioId).ConfigureAwait(true);
                Assert.False(generated.IsPreview);
                Assert.True(generated.DurationMs > 0);

                var artifact = await db.Set<Artifact>().FirstAsync(a => a.Id == result.ArtifactId).ConfigureAwait(true);
                Assert.Equal(ArtifactType.GeneratedAudioFinal, artifact.Type);

                var content = await db.Set<ContentObject>().FirstAsync(c => c.Id == generated.ContentObjectId).ConfigureAwait(true);
                var blob = await storage.DownloadAsync(content.StorageKey, CancellationToken.None).ConfigureAwait(true);
                using (blob)
                {
                    using var memory = new MemoryStream();
                    await blob.CopyToAsync(memory).ConfigureAwait(true);
                    var bytes = memory.ToArray();
                    Assert.True(bytes.Length > 44);
                    Assert.Equal("RIFF", Encoding.ASCII.GetString(bytes, 0, 4));
                    Assert.Equal("WAVE", Encoding.ASCII.GetString(bytes, 8, 4));
                }

                var execution = await db.Set<StageExecution>().FirstAsync(e => e.Id == claim.Execution.Id).ConfigureAwait(true);
                Assert.Equal(StageStatus.Completed, execution.Status);
            }
        }
    }

    [SkippableFact]
    public async Task Preview_Final_Distinct()
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
            var speakerId = await InsertSpeakerAsync(pgOptions, tenantId, projectId).ConfigureAwait(true);
            await InsertVoiceAsync(pgOptions, tenantId, projectId, runId, speakerId).ConfigureAwait(true);
            var segmentId = await InsertSegmentAsync(pgOptions, tenantId, projectId, runId, speakerId, 0, 0, 2000).ConfigureAwait(true);
            var storage = new FakeStorage();
            var artifacts = new ArtifactService(new TestFactory(pgOptions), storage);
            var translationArtifactId = await PublishTranslationArtifactAsync(pgOptions, artifacts, tenantId, projectId, runId, segmentId).ConfigureAwait(true);
            await InsertTranslationAsync(pgOptions, tenantId, projectId, runId, segmentId, "hola mundo", translationArtifactId).ConfigureAwait(true);
            var previewClaim = await ClaimSegmentAsync(pgOptions, tenantId, projectId, runId, segmentId, attempt: 0).ConfigureAwait(true);
            var finalClaim = await ClaimSegmentAsync(pgOptions, tenantId, projectId, runId, segmentId, attempt: 1).ConfigureAwait(true);

            var fake = new FakeTts([FakeTts.Success(durationMs: 1100), FakeTts.Success(durationMs: 1300)]);
            var ffprobe = new FakeFfprobe();
            var service = CreateService(pgOptions, storage, fake, ffprobe, allowCost: true, descriptorCount: 1, new TtsOptions());
            var preview = await service.GenerateAsync(
                tenantId, projectId, runId, segmentId, isPreview: true, 0,
                previewClaim.Execution.Id, previewClaim.Execution.LeaseOwner, previewClaim.Execution.LeaseToken).ConfigureAwait(true);
            var final = await service.GenerateAsync(
                tenantId, projectId, runId, segmentId, isPreview: false, 1,
                finalClaim.Execution.Id, finalClaim.Execution.LeaseOwner, finalClaim.Execution.LeaseToken).ConfigureAwait(true);

            Assert.True(preview.IsPreview);
            Assert.False(final.IsPreview);
            Assert.NotEqual(preview.GeneratedAudioId, final.GeneratedAudioId);
            Assert.NotEqual(preview.ArtifactId, final.ArtifactId);

            using (TenantContext.BeginScope(tenantId))
            {
                using var db = new AppDbContext(pgOptions);
                var rows = await db.Set<GeneratedAudioArtifact>().Where(g => g.SegmentId == segmentId).ToListAsync().ConfigureAwait(true);
                Assert.Equal(2, rows.Count);
                Assert.Single(rows, g => g.IsPreview);
                Assert.Single(rows, g => !g.IsPreview);
                var previewRow = rows.First(g => g.IsPreview);
                var finalRow = rows.First(g => !g.IsPreview);
                Assert.Equal(preview.GeneratedAudioId, previewRow.Id);
                Assert.Equal(final.GeneratedAudioId, finalRow.Id);

                var previewArtifact = await db.Set<Artifact>().FirstAsync(a => a.Id == preview.ArtifactId).ConfigureAwait(true);
                var finalArtifact = await db.Set<Artifact>().FirstAsync(a => a.Id == final.ArtifactId).ConfigureAwait(true);
                Assert.Equal(ArtifactType.GeneratedAudioPreview, previewArtifact.Type);
                Assert.Equal(ArtifactType.GeneratedAudioFinal, finalArtifact.Type);
            }
        }
    }

    [SkippableFact]
    public async Task Estimator_Reduces_Attempts()
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
            var speakerId = await InsertSpeakerAsync(pgOptions, tenantId, projectId).ConfigureAwait(true);
            await InsertVoiceAsync(pgOptions, tenantId, projectId, runId, speakerId).ConfigureAwait(true);
            var segmentId = await InsertSegmentAsync(pgOptions, tenantId, projectId, runId, speakerId, 0, 0, 2000).ConfigureAwait(true);
            var storage = new FakeStorage();
            var artifacts = new ArtifactService(new TestFactory(pgOptions), storage);
            var longText = new string('a', 200);
            var translationArtifactId = await PublishTranslationArtifactAsync(pgOptions, artifacts, tenantId, projectId, runId, segmentId).ConfigureAwait(true);
            await InsertTranslationAsync(pgOptions, tenantId, projectId, runId, segmentId, longText, translationArtifactId).ConfigureAwait(true);
            var claim = await ClaimSegmentAsync(pgOptions, tenantId, projectId, runId, segmentId, attempt: 0).ConfigureAwait(true);

            var fake = new FakeTts([FakeTts.Success(durationMs: 1500)]);
            var ffprobe = new FakeFfprobe();
            var service = CreateService(pgOptions, storage, fake, ffprobe, allowCost: true, descriptorCount: 1, new TtsOptions { EstimatorEnabled = true });
            var result = await service.GenerateAsync(
                tenantId, projectId, runId, segmentId, isPreview: false, 0,
                claim.Execution.Id, claim.Execution.LeaseOwner, claim.Execution.LeaseToken).ConfigureAwait(true);

            Assert.False(result.NeedsReview);
            Assert.Equal(200 * 75, result.EstimatedMs);
            Assert.Equal(0.85, result.ProsodyRate, precision: 5);
            Assert.Equal(1, fake.Calls);
            Assert.NotNull(fake.LastRequest);
            Assert.Contains("<prosody", fake.LastRequest!.Text, StringComparison.Ordinal);
            Assert.Contains("rate=\"85%\"", fake.LastRequest.Text, StringComparison.Ordinal);

            Assert.Equal(DurationEstimator.EstimateMs(longText, "es"), result.EstimatedMs);
            Assert.Equal(DurationEstimator.ComputeRate(result.EstimatedMs, 2000), result.ProsodyRate, precision: 5);
        }
    }

    [SkippableFact]
    public async Task Reservation_Enforced()
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
            var speakerId = await InsertSpeakerAsync(pgOptions, tenantId, projectId).ConfigureAwait(true);
            await InsertVoiceAsync(pgOptions, tenantId, projectId, runId, speakerId).ConfigureAwait(true);
            var segmentId = await InsertSegmentAsync(pgOptions, tenantId, projectId, runId, speakerId, 0, 0, 2000).ConfigureAwait(true);
            var storage = new FakeStorage();
            var artifacts = new ArtifactService(new TestFactory(pgOptions), storage);
            var translationArtifactId = await PublishTranslationArtifactAsync(pgOptions, artifacts, tenantId, projectId, runId, segmentId).ConfigureAwait(true);
            await InsertTranslationAsync(pgOptions, tenantId, projectId, runId, segmentId, "hola mundo", translationArtifactId).ConfigureAwait(true);
            var claim = await ClaimSegmentAsync(pgOptions, tenantId, projectId, runId, segmentId, attempt: 0).ConfigureAwait(true);

            var fake = new FakeTts([FakeTts.Success(durationMs: 1200)]);
            var ffprobe = new FakeFfprobe();
            var service = CreateService(pgOptions, storage, fake, ffprobe, allowCost: false, descriptorCount: 1, new TtsOptions());
            var ex = await Assert.ThrowsAsync<ErrorCodeException>(() => service.GenerateAsync(
                tenantId, projectId, runId, segmentId, isPreview: false, 0,
                claim.Execution.Id, claim.Execution.LeaseOwner, claim.Execution.LeaseToken)).ConfigureAwait(true);

            Assert.Equal(ErrorCodes.QuotaExceeded, ex.ErrorCode);
            Assert.Equal(0, fake.Calls);

            using (TenantContext.BeginScope(tenantId))
            {
                using var db = new AppDbContext(pgOptions);
                Assert.Empty(await db.Set<GeneratedAudioArtifact>().Where(g => g.SegmentId == segmentId).ToListAsync().ConfigureAwait(true));
                Assert.Empty(await db.Set<ProviderExecution>().Where(e => e.ProcessingRunId == runId).ToListAsync().ConfigureAwait(true));
            }
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
            var speakerId = await InsertSpeakerAsync(pgOptions, tenantId, projectId).ConfigureAwait(true);
            await InsertVoiceAsync(pgOptions, tenantId, projectId, runId, speakerId).ConfigureAwait(true);
            var segmentId = await InsertSegmentAsync(pgOptions, tenantId, projectId, runId, speakerId, 0, 0, 2000).ConfigureAwait(true);
            var storage = new FakeStorage();
            var artifacts = new ArtifactService(new TestFactory(pgOptions), storage);
            var translationArtifactId = await PublishTranslationArtifactAsync(pgOptions, artifacts, tenantId, projectId, runId, segmentId).ConfigureAwait(true);
            await InsertTranslationAsync(pgOptions, tenantId, projectId, runId, segmentId, "hola mundo", translationArtifactId).ConfigureAwait(true);
            var claim = await ClaimSegmentAsync(pgOptions, tenantId, projectId, runId, segmentId, attempt: 0).ConfigureAwait(true);

            var fake = new FakeTts([FakeTts.Success(durationMs: 1200)]);
            var ffprobe = new FakeFfprobe();
            var service = CreateService(pgOptions, storage, fake, ffprobe, allowCost: true, descriptorCount: 1, new TtsOptions());
            var result = await service.GenerateAsync(
                tenantId, projectId, runId, segmentId, isPreview: false, 0,
                claim.Execution.Id, claim.Execution.LeaseOwner, claim.Execution.LeaseToken).ConfigureAwait(true);

            Assert.False(result.NeedsReview);
            Assert.Equal(1, fake.Calls);

            using (TenantContext.BeginScope(tenantId))
            {
                using var db = new AppDbContext(pgOptions);
                var rows = await db.Set<ProviderExecution>().Where(e => e.ProcessingRunId == runId).ToListAsync().ConfigureAwait(true);
                Assert.NotEmpty(rows);
                Assert.All(rows, e => Assert.Equal(ProviderCapability.Tts, e.Capability));
                Assert.Contains(rows, e => e.Outcome == OutcomeClass.Success);
                var success = rows.First(e => e.Outcome == OutcomeClass.Success);
                Assert.Contains(nameof(StageType.VoiceGeneration), success.ProviderIdempotencyKey!, StringComparison.Ordinal);
                Assert.Contains(segmentId.ToString("N"), success.ProviderIdempotencyKey!, StringComparison.Ordinal);
                Assert.Equal(DurationEstimator.SsmlTemplateId, success.PromptTemplateId);
                Assert.NotNull(success.PromptHash);
                Assert.Equal("1", success.VoiceProfileVersion);
                Assert.Equal("Mock", result.Provider);
            }
        }
    }

    private TtsService CreateService(
        DbContextOptions<AppDbContext> pgOptions,
        IArtifactStorage storage,
        ITtsProvider tts,
        IFFprobeService ffprobe,
        bool allowCost,
        int descriptorCount,
        TtsOptions ttsOptions)
    {
        var factory = new TestFactory(pgOptions);
        var artifacts = new ArtifactService(factory, storage);
        var descriptors = new FakeDescriptors(descriptorCount);
        var healthMock = new Moq.Mock<IProviderHealthTracker>(Moq.MockBehavior.Strict);
        healthMock.Setup(h => h.IsHealthy(Moq.It.IsAny<ProviderType>())).Returns(true);
        var costMock = new Moq.Mock<IProviderCostGate>(Moq.MockBehavior.Strict);
        costMock.Setup(c => c.CanProceedAsync(Moq.It.IsAny<Guid>(), Moq.It.IsAny<ProviderCapability>(), Moq.It.IsAny<CancellationToken>())).Returns(Task.FromResult(allowCost));
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
        return new TtsService(
            factory, artifacts, ffprobe, tts, resolver, descriptors, recorder,
            costMock.Object,
            global::Microsoft.Extensions.Options.Options.Create(ttsOptions),
            global::Microsoft.Extensions.Options.Options.Create(new ProviderOptions()),
            global::Microsoft.Extensions.Options.Options.Create(new RetryOptions()),
            NullLogger<TtsService>.Instance);
    }

    private static async Task<StageClaimResult> ClaimSegmentAsync(
        DbContextOptions<AppDbContext> pgOptions,
        Guid tenantId,
        Guid projectId,
        Guid runId,
        Guid segmentId,
        int attempt)
    {
        var stages = new StageExecutionService(new TestFactory(pgOptions), global::Microsoft.Extensions.Options.Options.Create(new RetryOptions()));
        using (TenantContext.BeginScope(tenantId))
        {
            return await stages.ClaimAsync(
                tenantId, projectId, runId,
                nameof(StageType.VoiceGeneration), nameof(ScopeType.Segment), segmentId.ToString("D"),
                segmentId, attempt, "test-owner", TimeSpan.FromMinutes(5)).ConfigureAwait(true);
        }
    }

    private static async Task<Guid> InsertSegmentAsync(
        DbContextOptions<AppDbContext> options,
        Guid tenantId,
        Guid projectId,
        Guid runId,
        Guid speakerId,
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
                "Pending", speakerId, now));
            await db.SaveChangesAsync().ConfigureAwait(true);
        }

        return id;
    }

    private static async Task<Guid> InsertSpeakerAsync(
        DbContextOptions<AppDbContext> options,
        Guid tenantId,
        Guid projectId)
    {
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = new AppDbContext(options);
            db.Set<Speaker>().Add(new Speaker(
                id, tenantId, projectId, $"proj:{id:N}", "Speaker 1",
                0, 2000, "test", "1", 0.9, "spk_0", now));
            await db.SaveChangesAsync().ConfigureAwait(true);
        }

        return id;
    }

    private static async Task InsertVoiceAsync(
        DbContextOptions<AppDbContext> options,
        Guid tenantId,
        Guid projectId,
        Guid runId,
        Guid speakerId)
    {
        var now = DateTimeOffset.UtcNow;
        var voiceId = VoiceAssignmentService.VoiceProfileIdFor(tenantId, "Mock", "mock-voice-1", "es");
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = new AppDbContext(options);
            db.Set<VoiceProfile>().Add(new VoiceProfile(
                voiceId, tenantId, "Mock", "mock-voice-1", "1", "es",
                VoiceType.Stock, false, null, now));
            db.Set<SpeakerVoiceAssignment>().Add(new SpeakerVoiceAssignment(
                Guid.NewGuid(), tenantId, projectId, runId,
                speakerId, voiceId, VoiceAssignmentService.ReasonDeterministic, new string('a', 64), now));
            await db.SaveChangesAsync().ConfigureAwait(true);
        }
    }

    private static async Task<Guid> PublishTranslationArtifactAsync(
        DbContextOptions<AppDbContext> options,
        ArtifactService artifacts,
        Guid tenantId,
        Guid projectId,
        Guid runId,
        Guid segmentId)
    {
        var bytes = Encoding.UTF8.GetBytes(string.Concat("{\"schemaVersion\":\"1\",\"segmentId\":\"", segmentId.ToString("N"), "\"}"));
        using var stream = new MemoryStream(bytes, writable: false);
        var published = await artifacts.PublishAsync(
            tenantId, projectId, runId,
            StageType.Translation, ArtifactType.Translation,
            stream, ".json", "application/json",
            "Mock", "mock-1", null, null, [],
            cancellationToken: CancellationToken.None).ConfigureAwait(true);
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = new AppDbContext(options);
            await db.Database.ExecuteSqlRawAsync(
                "UPDATE artifacts SET metadata_json = {0} WHERE id = {1} AND tenant_id = {2}",
                Encoding.UTF8.GetString(bytes), published.ArtifactId, tenantId).ConfigureAwait(true);
        }

        return published.ArtifactId;
    }

    private static async Task InsertTranslationAsync(
        DbContextOptions<AppDbContext> options,
        Guid tenantId,
        Guid projectId,
        Guid runId,
        Guid segmentId,
        string text,
        Guid translationArtifactId)
    {
        var now = DateTimeOffset.UtcNow;
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = new AppDbContext(options);
            db.Set<TranslationVersion>().Add(new TranslationVersion(
                Guid.NewGuid(), tenantId, projectId, runId, segmentId,
                text, [], 0.9, 0.9, 0.9,
                "Mock", "mock-1", null, null, true, now));
            await db.SaveChangesAsync().ConfigureAwait(true);
        }
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
            _output.WriteLine($"Docker/PostgreSQL unavailable, skipping TTS test: {ex.Message}");
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

        public DbContextOptions<AppDbContext> Options => _options;

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

    private sealed class FakeFfprobe : IFFprobeService
    {
        public Task<FfprobeResult> ProbeAsync(string storageKeyOrLocalPath, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(storageKeyOrLocalPath))
            {
                throw new ErrorCodeException(ErrorCodes.MediaCorrupt, "FFprobe returned empty output; media is corrupt or undecodable.");
            }

            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(storageKeyOrLocalPath.Trim());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new ErrorCodeException(ErrorCodes.MediaCorrupt, "FFprobe failed; media is corrupt or undecodable.", ex);
            }

            if (bytes.Length < 44
                || Encoding.ASCII.GetString(bytes, 0, 4) != "RIFF"
                || Encoding.ASCII.GetString(bytes, 8, 4) != "WAVE")
            {
                throw new ErrorCodeException(ErrorCodes.MediaCorrupt, "FFprobe failed; media is corrupt or undecodable.");
            }

            var dataBytes = bytes.Length - 44;
            var durationMs = dataBytes / 32;
            if (durationMs <= 0)
            {
                throw new ErrorCodeException(ErrorCodes.MediaCorrupt, "FFprobe returned zero duration; media is corrupt or undecodable.");
            }

            var streams = new List<FfprobeStream>
            {
                new("audio", "pcm_s16le", null, null, null, 16000, 1, null),
            };
            return Task.FromResult(new FfprobeResult("wav", durationMs, streams));
        }
    }

    private sealed class FakeTts : ITtsProvider
    {
        private readonly Queue<object> _script;

        public FakeTts(IEnumerable<object> script)
        {
            _script = new Queue<object>(script);
        }

        public int Calls { get; private set; }

        public TtsRequest? LastRequest { get; private set; }

        public static TtsResponse Success(int durationMs = 1200, string voiceId = "mock-voice-1", Dictionary<string, string>? metadata = null)
        {
            return new TtsResponse(
                string.Concat("mock-tts-", Guid.NewGuid().ToString("N")),
                durationMs,
                voiceId,
                0.95,
                "mock-1",
                "1",
                "mock",
                new ProviderUsage(null, null, durationMs / 1000.0, 0),
                metadata);
        }

        public static TtsResponse Corrupt(string voiceId = "mock-voice-1")
        {
            var audio = Convert.ToBase64String(Encoding.UTF8.GetBytes("not-audio-bytes"));
            return Success(durationMs: 1200, voiceId: voiceId, metadata: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["audioBase64"] = audio,
            });
        }

        public Task<TtsResponse> SynthesizeAsync(TtsRequest request, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);
            Calls++;
            LastRequest = request;
            if (_script.Count > 0)
            {
                var next = _script.Dequeue();
                if (next is Exception failure)
                {
                    return Task.FromException<TtsResponse>(failure);
                }

                if (next is TtsResponse response)
                {
                    if (!string.Equals(response.VoiceId, request.VoiceId, StringComparison.Ordinal))
                    {
                        return Task.FromResult(response with { VoiceId = request.VoiceId });
                    }

                    return Task.FromResult(response);
                }
            }

            return Task.FromResult(Success());
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
