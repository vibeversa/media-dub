using System.Data.Common;
using System.Globalization;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using DubbingPlatform.Application.Abstractions;
using DubbingPlatform.Application.Errors;
using DubbingPlatform.Application.MultiTenancy;
using DubbingPlatform.Application.Options;
using DubbingPlatform.Application.Services;
using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Domain.Enums;
using DubbingPlatform.Infrastructure.Media;
using DubbingPlatform.Infrastructure.Persistence;
using DubbingPlatform.Infrastructure.Persistence.Interceptors;
using DubbingPlatform.Infrastructure.Processes;
using EFCore.NamingConventions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Testcontainers.PostgreSql;
using Xunit.Abstractions;

namespace DubbingPlatform.IntegrationTests.Pipeline;

/// <summary>
/// Task 032: segment/project/signal quality control over PG with real
/// ffmpeg/ffprobe (sine fixtures mixed via <see cref="FFmpegMixer"/>) and fake
/// blob storage. Skips with an explicit message when Docker (PG) or
/// ffmpeg/ffprobe is unavailable (CI runs live with all three).
/// </summary>
public sealed class QcTests
{
    private readonly ITestOutputHelper _output;

    public QcTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [SkippableFact]
    public async Task Clean_Passes()
    {
        var pg = await StartPostgresAsync().ConfigureAwait(true);
        await using (pg.ConfigureAwait(true))
        {
            await EnsureFfmpegAsync().ConfigureAwait(true);
            var pgOptions = CreatePgOptions(pg);
            await MigrateAsync(pg).ConfigureAwait(true);
            var stack = CreateStack(pgOptions);
            var workDir = ProcessRunner.CreateTempWorkingDir();
            try
            {
                var fixture = await SeedCleanAsync(stack, pgOptions, workDir, "{}").ConfigureAwait(true);

                var qc = CreateQc(stack);
                var outcome = await qc.RunAsync(
                    fixture.TenantId, fixture.ProjectId, fixture.RunId, CancellationToken.None).ConfigureAwait(true);

                Assert.Equal(QualityStatus.Pass, outcome.Verdict);
                Assert.True(outcome.Allowed);
                Assert.Empty(outcome.Findings);
                Assert.NotEqual(Guid.Empty, outcome.ReportArtifactId);
                Assert.Null(outcome.ReviewItemId);
            }
            finally
            {
                DeleteDirQuietly(workDir);
            }
        }
    }

    [SkippableFact]
    public async Task Corrupt_Blocks_Render()
    {
        var pg = await StartPostgresAsync().ConfigureAwait(true);
        await using (pg.ConfigureAwait(true))
        {
            await EnsureFfmpegAsync().ConfigureAwait(true);
            var pgOptions = CreatePgOptions(pg);
            await MigrateAsync(pg).ConfigureAwait(true);
            var stack = CreateStack(pgOptions);
            var workDir = ProcessRunner.CreateTempWorkingDir();
            try
            {
                var fixture = await SeedCleanAsync(
                    stack, pgOptions, workDir, "{}",
                    mixedBytes: Encoding.UTF8.GetBytes("not-audio-at-all")).ConfigureAwait(true);

                var qc = CreateQc(stack);
                var outcome = await qc.RunAsync(
                    fixture.TenantId, fixture.ProjectId, fixture.RunId, CancellationToken.None).ConfigureAwait(true);

                Assert.Equal(QualityStatus.Blocked, outcome.Verdict);
                Assert.False(outcome.Allowed);
                Assert.Contains(outcome.Findings, f => f.Code == QualityControlService.CodeCorrupt && f.Status == QualityStatus.Blocked);
                Assert.NotNull(outcome.ReviewItemId);

                using (TenantContext.BeginScope(fixture.TenantId))
                {
                    using var db = new AppDbContext(pgOptions);
                    var rows = await db.Set<QualityResult>()
                        .Where(q => q.ProcessingRunId == fixture.RunId)
                        .ToListAsync().ConfigureAwait(true);
                    Assert.Contains(rows, r => r.Code == QualityControlService.CodeCorrupt && r.Status == QualityStatus.Blocked);
                    var review = await db.Set<ReviewItem>().FirstOrDefaultAsync(r => r.Id == outcome.ReviewItemId).ConfigureAwait(true);
                    Assert.NotNull(review);
                    Assert.Equal(QualityControlService.ReviewReasonBlocked, review.Reason);
                    Assert.Equal(ReviewStatus.Open, review.Status);
                }
            }
            finally
            {
                DeleteDirQuietly(workDir);
            }
        }
    }

    [SkippableFact]
    public async Task Report_Persisted()
    {
        var pg = await StartPostgresAsync().ConfigureAwait(true);
        await using (pg.ConfigureAwait(true))
        {
            await EnsureFfmpegAsync().ConfigureAwait(true);
            var pgOptions = CreatePgOptions(pg);
            await MigrateAsync(pg).ConfigureAwait(true);
            var stack = CreateStack(pgOptions);
            var workDir = ProcessRunner.CreateTempWorkingDir();
            try
            {
                var fixture = await SeedCleanAsync(stack, pgOptions, workDir, "{}").ConfigureAwait(true);

                var qc = CreateQc(stack);
                var outcome = await qc.RunAsync(
                    fixture.TenantId, fixture.ProjectId, fixture.RunId, CancellationToken.None).ConfigureAwait(true);

                string reportStorageKey;
                using (TenantContext.BeginScope(fixture.TenantId))
                {
                    using var db = new AppDbContext(pgOptions);
                    var report = await db.Set<Artifact>().FirstOrDefaultAsync(a => a.Id == outcome.ReportArtifactId).ConfigureAwait(true);
                    Assert.NotNull(report);
                    Assert.Equal(ArtifactType.QcReport, report.Type);
                    Assert.NotNull(report.MetadataJson);
                    Assert.Contains("\"verdict\":\"Pass\"", report.MetadataJson, StringComparison.Ordinal);

                    var parents = await db.Set<ArtifactParent>()
                        .Where(p => p.ChildArtifactId == report.Id)
                        .ToListAsync().ConfigureAwait(true);
                    Assert.Contains(parents, p => p.ParentArtifactId == fixture.MixedArtifactId);
                    Assert.Contains(parents, p => p.ParentArtifactId == fixture.TimelineArtifactId);

                    // R5: results are queryable via direct DB access.
                    var rows = await db.Set<QualityResult>()
                        .Where(q => q.ProcessingRunId == fixture.RunId)
                        .ToListAsync().ConfigureAwait(true);
                    Assert.NotNull(rows);
                    Assert.Empty(rows);

                    var reportContent = await db.Set<ContentObject>().FirstOrDefaultAsync(c => c.Id == report.ContentObjectId).ConfigureAwait(true);
                    Assert.NotNull(reportContent);
                    reportStorageKey = reportContent.StorageKey;
                }

                var content = await stack.Storage.DownloadAsync(reportStorageKey, CancellationToken.None).ConfigureAwait(true);
                using (content)
                {
                    using var reader = new StreamReader(content, Encoding.UTF8);
                    var json = await reader.ReadToEndAsync().ConfigureAwait(true);
                    Assert.Contains("\"schemaVersion\":\"1\"", json, StringComparison.Ordinal);
                    Assert.Contains("\"verdict\":\"Pass\"", json, StringComparison.Ordinal);
                }
            }
            finally
            {
                DeleteDirQuietly(workDir);
            }
        }
    }

    [SkippableFact]
    public async Task Review_Created_When_Required()
    {
        var pg = await StartPostgresAsync().ConfigureAwait(true);
        await using (pg.ConfigureAwait(true))
        {
            await EnsureFfmpegAsync().ConfigureAwait(true);
            var pgOptions = CreatePgOptions(pg);
            await MigrateAsync(pg).ConfigureAwait(true);
            var stack = CreateStack(pgOptions);
            var workDir = ProcessRunner.CreateTempWorkingDir();
            try
            {
                var fixture = await SeedCleanAsync(stack, pgOptions, workDir, "{}").ConfigureAwait(true);
                await InsertReviewAsync(pgOptions, fixture.TenantId, fixture.ProjectId, fixture.RunId, fixture.SegmentId).ConfigureAwait(true);

                var qc = CreateQc(stack);
                var outcome = await qc.RunAsync(
                    fixture.TenantId, fixture.ProjectId, fixture.RunId, CancellationToken.None).ConfigureAwait(true);

                Assert.Equal(QualityStatus.Blocked, outcome.Verdict);
                Assert.False(outcome.Allowed);
                Assert.Contains(outcome.Findings, f => f.Code == QualityControlService.CodeUnresolvedReview);
                Assert.NotNull(outcome.ReviewItemId);

                using (TenantContext.BeginScope(fixture.TenantId))
                {
                    using var db = new AppDbContext(pgOptions);
                    var created = await db.Set<ReviewItem>().FirstOrDefaultAsync(r => r.Id == outcome.ReviewItemId).ConfigureAwait(true);
                    Assert.NotNull(created);
                    Assert.Equal(QualityControlService.ReviewReasonBlocked, created.Reason);
                    Assert.Equal(ReviewStatus.Open, created.Status);
                    Assert.Equal(ScopeType.Project, created.ScopeType);
                }
            }
            finally
            {
                DeleteDirQuietly(workDir);
            }
        }
    }

    [SkippableFact]
    public async Task Checksum_Mismatch_Blocked()
    {
        var pg = await StartPostgresAsync().ConfigureAwait(true);
        await using (pg.ConfigureAwait(true))
        {
            await EnsureFfmpegAsync().ConfigureAwait(true);
            var pgOptions = CreatePgOptions(pg);
            await MigrateAsync(pg).ConfigureAwait(true);
            var stack = CreateStack(pgOptions);
            var workDir = ProcessRunner.CreateTempWorkingDir();
            try
            {
                var fixture = await SeedCleanAsync(stack, pgOptions, workDir, "{}").ConfigureAwait(true);

                using (TenantContext.BeginScope(fixture.TenantId))
                {
                    using var db = new AppDbContext(pgOptions);
                    await db.Database.ExecuteSqlRawAsync(
                        "UPDATE content_objects SET sha256_hex = {0} WHERE id = {1}",
                        new string('d', 64), fixture.AudioContentObjectId).ConfigureAwait(true);
                }

                var qc = CreateQc(stack);
                var outcome = await qc.RunAsync(
                    fixture.TenantId, fixture.ProjectId, fixture.RunId, CancellationToken.None).ConfigureAwait(true);

                Assert.Equal(QualityStatus.Blocked, outcome.Verdict);
                Assert.False(outcome.Allowed);
                Assert.Contains(
                    outcome.Findings,
                    f => string.Equals(f.Code, ErrorCodes.ArtifactChecksumMismatch, StringComparison.Ordinal)
                        && f.Status == QualityStatus.Blocked);
            }
            finally
            {
                DeleteDirQuietly(workDir);
            }
        }
    }

    private sealed record Fixture(
        Guid TenantId,
        Guid ProjectId,
        Guid RunId,
        Guid SegmentId,
        Guid AudioArtifactId,
        Guid AudioContentObjectId,
        Guid TimelineArtifactId,
        Guid MixedArtifactId);

    private sealed record Stack(
        TestFactory Factory,
        ArtifactService Artifacts,
        FakeStorage Storage,
        FFprobeService Ffprobe,
        FFmpegService Ffmpeg,
        FFmpegMixer Mixer);

    private static Stack CreateStack(DbContextOptions<AppDbContext> pgOptions)
    {
        var factory = new TestFactory(pgOptions);
        var storage = new FakeStorage();
        var artifacts = new ArtifactService(factory, storage);
        var runner = new ProcessRunner(NullLogger<ProcessRunner>.Instance);
        var mediaOptions = Microsoft.Extensions.Options.Options.Create(new MediaOptions());
        var ffprobe = new FFprobeService(storage, runner, mediaOptions, NullLogger<FFprobeService>.Instance);
        var ffmpeg = new FFmpegService(runner, ffprobe, mediaOptions, NullLogger<FFmpegService>.Instance);
        var gate = new MediaJobGate(mediaOptions);
        var mixer = new FFmpegMixer(
            runner, ffprobe, ffmpeg, gate,
            Microsoft.Extensions.Options.Options.Create(new MixingOptions()),
            mediaOptions, NullLogger<FFmpegMixer>.Instance);
        return new Stack(factory, artifacts, storage, ffprobe, ffmpeg, mixer);
    }

    private static QualityControlService CreateQc(Stack stack)
    {
        return new QualityControlService(
            stack.Factory, stack.Artifacts, stack.Storage, stack.Ffprobe, stack.Ffmpeg,
            Microsoft.Extensions.Options.Options.Create(new QcOptions()),
            Microsoft.Extensions.Options.Options.Create(new MixingOptions()),
            NullLogger<QualityControlService>.Instance);
    }

    private static async Task<Fixture> SeedCleanAsync(
        Stack stack,
        DbContextOptions<AppDbContext> pgOptions,
        string workDir,
        string settingsJson,
        byte[]? mixedBytes = null)
    {
        var tenantId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        const int sourceDurationMs = 4000;
        await SeedProjectRunAsync(pgOptions, tenantId, projectId, runId, settingsJson, 0, sourceDurationMs).ConfigureAwait(true);
        var voice = await InsertSpeakerAsync(pgOptions, tenantId, projectId, runId).ConfigureAwait(true);
        var segmentId = await InsertSegmentAsync(pgOptions, tenantId, projectId, runId, 0, 0, 2000, voice.SpeakerId).ConfigureAwait(true);

        var dlgFile = Path.Combine(workDir, string.Concat("dlg-", segmentId.ToString("N"), ".wav"));
        await GenerateSineAsync(880, 2, dlgFile).ConfigureAwait(true);
        var dlgBytes = await File.ReadAllBytesAsync(dlgFile).ConfigureAwait(true);
        var audioPublished = await PublishBytesAsync(
            stack.Artifacts, tenantId, projectId, runId,
            StageType.VoiceGeneration, ArtifactType.GeneratedAudioFinal,
            dlgBytes, ".wav", "audio/wav", []).ConfigureAwait(true);
        await InsertGeneratedAudioAsync(
            pgOptions, tenantId, projectId, runId, segmentId,
            voice.VoiceProfileId, audioPublished.ContentObjectId, 2000).ConfigureAwait(true);

        var timelineJson = BuildTimelineJson(
            [(segmentId, 0, 0, 2000, audioPublished.ArtifactId)], sourceDurationMs, runId);
        var timelinePublished = await PublishBytesAsync(
            stack.Artifacts, tenantId, projectId, runId,
            StageType.TimelineAssembly, ArtifactType.Timeline,
            Encoding.UTF8.GetBytes(timelineJson), ".json", "application/json", []).ConfigureAwait(true);

        byte[] finalMixed = mixedBytes ?? await MixFixtureAsync(stack.Mixer, timelineJson, [dlgFile], workDir).ConfigureAwait(true);
        var mixedPublished = await PublishBytesAsync(
            stack.Artifacts, tenantId, projectId, runId,
            StageType.AudioMixing, ArtifactType.MixedAudio,
            finalMixed, ".wav", "audio/wav", [timelinePublished.ArtifactId, audioPublished.ArtifactId]).ConfigureAwait(true);
        await SetArtifactMetadataAsync(
            pgOptions, tenantId, mixedPublished.ArtifactId,
            JsonSerializer.Serialize(new
            {
                schemaVersion = "1",
                runId = runId.ToString("N"),
                profile = "web",
                filterComplex = "premix: [dlg0]anull[dialog];[dialog]anull[mix] || final: loudnorm=I=-16:TP=-1:LRA=11,aformat=sample_fmts=s16:sample_rates=48000:channel_layouts=stereo",
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web))).ConfigureAwait(true);

        await InsertTranscriptAsync(pgOptions, tenantId, projectId, runId, segmentId, "hello world").ConfigureAwait(true);
        await InsertTranslationAsync(pgOptions, tenantId, projectId, runId, segmentId, "hola mundo").ConfigureAwait(true);
        await InsertSyncAsync(pgOptions, tenantId, projectId, runId, segmentId).ConfigureAwait(true);
        await InsertProviderExecutionAsync(pgOptions, tenantId, projectId, runId, 0).ConfigureAwait(true);

        // The report storage key is resolved by callers from the report
        // artifact row (R5 query path).
        return new Fixture(
            tenantId, projectId, runId, segmentId,
            audioPublished.ArtifactId, audioPublished.ContentObjectId,
            timelinePublished.ArtifactId, mixedPublished.ArtifactId);
    }

    private static async Task<byte[]> MixFixtureAsync(
        FFmpegMixer mixer,
        string timelineJson,
        IReadOnlyList<string> dialogueFiles,
        string workDir)
    {
        var output = Path.Combine(workDir, string.Concat("mixed-", Guid.NewGuid().ToString("N"), ".wav"));
        await mixer.MixAsync(timelineJson, dialogueFiles, null, output, "web", CancellationToken.None).ConfigureAwait(true);
        return await File.ReadAllBytesAsync(output).ConfigureAwait(true);
    }

    private static string BuildTimelineJson(
        IReadOnlyList<(Guid SegmentId, int Sequence, int StartMs, int DurationMs, Guid AudioArtifactId)> entries,
        int sourceDurationMs,
        Guid runId)
    {
        var list = entries.Select(e => new
        {
            audioArtifactId = e.AudioArtifactId.ToString("N"),
            durationMs = e.DurationMs,
            overlapGroupId = (string?)null,
            segmentId = e.SegmentId.ToString("N"),
            sequence = e.Sequence,
            startMs = e.StartMs,
        }).ToList();

        var root = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["backgroundArtifactId"] = null,
            ["entries"] = list,
            ["overflowToleranceMs"] = 100,
            ["runId"] = runId.ToString("N"),
            ["schemaVersion"] = "1",
            ["skippedSegmentIds"] = Array.Empty<string>(),
            ["sourceDurationMs"] = sourceDurationMs,
        };

        return JsonSerializer.Serialize(root, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    private static async Task GenerateSineAsync(int frequencyHz, int durationSec, string destPath)
    {
        var runner = new ProcessRunner(NullLogger<ProcessRunner>.Instance);
        var workDir = ProcessRunner.CreateTempWorkingDir();
        try
        {
            var args = new List<string>
            {
                "-y", "-f", "lavfi", "-i",
                string.Concat("sine=frequency=", frequencyHz.ToString(CultureInfo.InvariantCulture), ":duration=", durationSec.ToString(CultureInfo.InvariantCulture), ":sample_rate=48000"),
                "-c:a", "pcm_s16le", "-ac", "2", destPath,
            };

            var result = await runner.RunAsync(
                "ffmpeg", args, workDir, TimeSpan.FromSeconds(90), CancellationToken.None).ConfigureAwait(true);
            if (result.TimedOut || result.ExitCode != 0)
            {
                throw new InvalidOperationException($"ffmpeg sine fixture failed (exit {result.ExitCode}, timedOut={result.TimedOut}): {result.StdErr}");
            }
        }
        finally
        {
            DeleteDirQuietly(workDir);
        }
    }

    private static async Task SeedProjectRunAsync(
        DbContextOptions<AppDbContext> options,
        Guid tenantId,
        Guid projectId,
        Guid runId,
        string settingsJson,
        int attempt,
        int sourceDurationMs)
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
                1024, sourceDurationMs, 48000, 2, "stereo", MediaAssetStatus.Valid, null, hash, now));
            db.DubbingProjects.Add(new DubbingProject(
                projectId, tenantId, "en", "es", ProjectStatus.Processing,
                settingsJson, new string('b', 64), null, runId, now, now));
            db.Set<ProcessingRun>().Add(new ProcessingRun(
                runId, tenantId, projectId, attempt, ProcessingRunStatus.Running,
                "1.0.0", new string('b', 64), new string('d', 64), new string('e', 64),
                now, now, now, null));
            await db.SaveChangesAsync().ConfigureAwait(true);
        }
    }

    private sealed record VoiceRef(Guid SpeakerId, Guid VoiceProfileId);

    private static async Task<VoiceRef> InsertSpeakerAsync(
        DbContextOptions<AppDbContext> options,
        Guid tenantId,
        Guid projectId,
        Guid runId)
    {
        var now = DateTimeOffset.UtcNow;
        var speakerId = Guid.NewGuid();
        var voiceId = Guid.NewGuid();
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = new AppDbContext(options);
            db.Set<Speaker>().Add(new Speaker(
                speakerId, tenantId, projectId,
                string.Concat(projectId.ToString("N"), ":single"), "Speaker 1",
                0, 2000, "diarization", "1", 0.9, null, now));
            db.Set<VoiceProfile>().Add(new VoiceProfile(
                voiceId, tenantId, "Mock", "mock-voice-1", "1", "es", VoiceType.Stock, false, null, now));
            db.Set<SpeakerVoiceAssignment>().Add(new SpeakerVoiceAssignment(
                Guid.NewGuid(), tenantId, projectId, runId, speakerId, voiceId,
                "deterministic", "policy-hash", now));
            await db.SaveChangesAsync().ConfigureAwait(true);
        }

        return new VoiceRef(speakerId, voiceId);
    }

    private static async Task<Guid> InsertSegmentAsync(
        DbContextOptions<AppDbContext> options,
        Guid tenantId,
        Guid projectId,
        Guid runId,
        int sequence,
        int startMs,
        int endMs,
        Guid speakerId)
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

    private static async Task<PublishResult> PublishBytesAsync(
        ArtifactService artifacts,
        Guid tenantId,
        Guid projectId,
        Guid runId,
        StageType stage,
        ArtifactType type,
        byte[] bytes,
        string extension,
        string contentType,
        IReadOnlyList<Guid> parents)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        return await artifacts.PublishAsync(
            tenantId, projectId, runId, stage, type,
            stream, extension, contentType,
            null, null, null, null, parents,
            cancellationToken: CancellationToken.None).ConfigureAwait(true);
    }

    private static async Task SetArtifactMetadataAsync(
        DbContextOptions<AppDbContext> options,
        Guid tenantId,
        Guid artifactId,
        string metadataJson)
    {
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = new AppDbContext(options);
            await db.Database.ExecuteSqlRawAsync(
                "UPDATE artifacts SET metadata_json = {0} WHERE id = {1} AND tenant_id = {2}",
                metadataJson, artifactId, tenantId).ConfigureAwait(true);
        }
    }

    private static async Task InsertGeneratedAudioAsync(
        DbContextOptions<AppDbContext> options,
        Guid tenantId,
        Guid projectId,
        Guid runId,
        Guid segmentId,
        Guid voiceProfileId,
        Guid contentObjectId,
        int durationMs)
    {
        var now = DateTimeOffset.UtcNow;
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = new AppDbContext(options);
            db.Set<GeneratedAudioArtifact>().Add(new GeneratedAudioArtifact(
                Guid.NewGuid(), tenantId, projectId, runId, segmentId,
                "Mock", "mock-1", voiceProfileId, contentObjectId,
                durationMs, false, 0, now));
            await db.SaveChangesAsync().ConfigureAwait(true);
        }
    }

    private static async Task InsertTranscriptAsync(
        DbContextOptions<AppDbContext> options,
        Guid tenantId,
        Guid projectId,
        Guid runId,
        Guid segmentId,
        string text)
    {
        var now = DateTimeOffset.UtcNow;
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = new AppDbContext(options);
            db.Set<TranscriptVersion>().Add(new TranscriptVersion(
                Guid.NewGuid(), tenantId, projectId, runId, segmentId,
                "Mock", "mock-1", "en", text, 0.95, null, true, false, now));
            await db.SaveChangesAsync().ConfigureAwait(true);
        }
    }

    private static async Task InsertTranslationAsync(
        DbContextOptions<AppDbContext> options,
        Guid tenantId,
        Guid projectId,
        Guid runId,
        Guid segmentId,
        string text)
    {
        var now = DateTimeOffset.UtcNow;
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = new AppDbContext(options);
            db.Set<TranslationVersion>().Add(new TranslationVersion(
                Guid.NewGuid(), tenantId, projectId, runId, segmentId,
                text, [], 0.9, 0.9, 0.9, "Mock", "mock-1", null, null, true, now));
            await db.SaveChangesAsync().ConfigureAwait(true);
        }
    }

    private static async Task InsertSyncAsync(
        DbContextOptions<AppDbContext> options,
        Guid tenantId,
        Guid projectId,
        Guid runId,
        Guid segmentId)
    {
        var now = DateTimeOffset.UtcNow;
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = new AppDbContext(options);
            db.Set<SyncResult>().Add(new SyncResult(
                Guid.NewGuid(), tenantId, projectId, runId, segmentId,
                0.95, SyncStatus.SyncAcceptable, 2000, 2000, 0.0, 1.0, now));
            await db.SaveChangesAsync().ConfigureAwait(true);
        }
    }

    private static async Task InsertProviderExecutionAsync(
        DbContextOptions<AppDbContext> options,
        Guid tenantId,
        Guid projectId,
        Guid runId,
        int attempt)
    {
        var now = DateTimeOffset.UtcNow;
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = new AppDbContext(options);
            db.Set<ProviderExecution>().Add(new ProviderExecution(
                Guid.NewGuid(), tenantId, projectId, runId, null,
                ProviderType.Mock, ProviderCapability.Transcription, "mock-1",
                "1", "mock", null, null, attempt, new string('a', 64), null, 1,
                null, null, null, null, null, null, OutcomeClass.Success, null,
                null, null, null, null, "test-qc-key", now));
            await db.SaveChangesAsync().ConfigureAwait(true);
        }
    }

    private static async Task InsertReviewAsync(
        DbContextOptions<AppDbContext> options,
        Guid tenantId,
        Guid projectId,
        Guid runId,
        Guid segmentId)
    {
        var now = DateTimeOffset.UtcNow;
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = new AppDbContext(options);
            db.Set<ReviewItem>().Add(new ReviewItem(
                Guid.NewGuid(), tenantId, projectId, runId,
                ScopeType.Segment, segmentId.ToString("D"), segmentId,
                ReviewStatus.Open, "LOW_CONFIDENCE", "{\"reason\":\"LOW_CONFIDENCE\"}", now, now, null));
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
            _output.WriteLine($"Docker/PostgreSQL unavailable, skipping QC test: {ex.Message}");
            Skip.If(true, $"Docker/PostgreSQL unavailable: {ex.Message}");
            throw new InvalidOperationException("Unreachable: Skip.If always throws.", ex);
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

                Skip.If(true, $"ffmpeg/ffprobe missing: ffprobe -version exited {result.ExitCode} (timedOut={result.TimedOut}). Install ffmpeg to run QC tests.");
            }
            finally
            {
                DeleteDirQuietly(workingDir);
            }
        }
#pragma warning disable CA1031 // Availability probe: any failure means skip with an explicit message.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _output.WriteLine($"ffmpeg/ffprobe missing, skipping QC test: {ex.Message}");
            Skip.If(true, $"ffmpeg/ffprobe missing: {ex.Message}. Install ffmpeg to run QC tests.");
        }

        throw new InvalidOperationException("Unreachable: Skip.If always throws.");
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

    private static void DeleteDirQuietly(string dir)
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
