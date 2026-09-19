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
/// Task 026: bounded versioned context-aware translation with glossary
/// enforcement, deterministic selection, budget caps, and review routing over
/// PG with fake storage/provider/descriptors (no MinIO needed). Skips with an
/// explicit message when Docker (PG) is unavailable (CI live).
/// </summary>
public sealed class TranslationTests
{
    private readonly ITestOutputHelper _output;

    public TranslationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [SkippableFact]
    public async Task Glossary_Used()
    {
        var pg = await StartPostgresAsync().ConfigureAwait(true);
        await using (pg.ConfigureAwait(true))
        {
            var pgOptions = CreatePgOptions(pg);
            await MigrateAsync(pg).ConfigureAwait(true);
            var tenantId = Guid.NewGuid();
            var projectId = Guid.NewGuid();
            var runId = Guid.NewGuid();
            await SeedProjectRunAsync(
                pgOptions, tenantId, projectId, runId,
                """{"glossary": {"hello": "hola"}, "style": "formal"}""").ConfigureAwait(true);
            var segmentId = await InsertSegmentAsync(pgOptions, tenantId, projectId, runId, 0, 0, 2000).ConfigureAwait(true);
            var storage = new FakeStorage();
            var artifacts = new ArtifactService(new TestFactory(pgOptions), storage);
            var transcriptArtifactId = await PublishTranscriptArtifactAsync(pgOptions, artifacts, tenantId, projectId, runId, segmentId).ConfigureAwait(true);
            await InsertTranscriptAsync(pgOptions, tenantId, projectId, runId, segmentId, "hello world", transcriptArtifactId).ConfigureAwait(true);
            var window = await InsertContextWindowAsync(pgOptions, artifacts, storage, tenantId, projectId, runId, segmentId, 0, "# target: es\n[0 unknown: hello world]").ConfigureAwait(true);
            var claim = await ClaimSegmentAsync(pgOptions, tenantId, projectId, runId, segmentId).ConfigureAwait(true);

            var fake = new FakeTranslation(
            [
                FakeTranslation.Response("bonjour monde", ["hola mundo", "salut monde"], 0.9),
            ]);
            var service = CreateService(pgOptions, storage, fake, descriptorCount: 1, new TranslationOptions());
            var result = await service.TranslateSegmentAsync(
                tenantId, projectId, runId, segmentId, 0,
                claim.Execution.Id, claim.Execution.LeaseOwner, claim.Execution.LeaseToken).ConfigureAwait(true);

            Assert.False(result.NeedsReview);
            Assert.True(result.UsedContext);
            Assert.Equal(window.WindowId, result.ContextWindowId);

            using (TenantContext.BeginScope(tenantId))
            {
                using var db = new AppDbContext(pgOptions);
                var version = await db.Set<TranslationVersion>().FirstAsync(v => v.Id == result.SelectedVersionId).ConfigureAwait(true);
                Assert.True(version.IsSelected);
                Assert.Contains("hola", version.PrimaryText, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [SkippableFact]
    public async Task Budget_Enforced()
    {
        var pg = await StartPostgresAsync().ConfigureAwait(true);
        await using (pg.ConfigureAwait(true))
        {
            var pgOptions = CreatePgOptions(pg);
            await MigrateAsync(pg).ConfigureAwait(true);
            var tenantId = Guid.NewGuid();
            var projectId = Guid.NewGuid();
            var runId = Guid.NewGuid();
            await SeedProjectRunAsync(pgOptions, tenantId, projectId, runId, "{}").ConfigureAwait(true);
            var segmentId = await InsertSegmentAsync(pgOptions, tenantId, projectId, runId, 0, 0, 2000).ConfigureAwait(true);
            var storage = new FakeStorage();
            var artifacts = new ArtifactService(new TestFactory(pgOptions), storage);
            var transcriptArtifactId = await PublishTranscriptArtifactAsync(pgOptions, artifacts, tenantId, projectId, runId, segmentId).ConfigureAwait(true);
            await InsertTranscriptAsync(pgOptions, tenantId, projectId, runId, segmentId, "hello world", transcriptArtifactId).ConfigureAwait(true);
            await InsertContextWindowAsync(pgOptions, artifacts, storage, tenantId, projectId, runId, segmentId, 0, "# target: es\n[0 unknown: hello world]").ConfigureAwait(true);
            var claim = await ClaimSegmentAsync(pgOptions, tenantId, projectId, runId, segmentId).ConfigureAwait(true);

            var fake = new FakeTranslation(
            [
                FakeTranslation.Response("hola mundo uno", ["hola mundo dos", "hola mundo tres"], 0.9),
            ]);
            var service = CreateService(pgOptions, storage, fake, descriptorCount: 1, new TranslationOptions { MaxCandidates = 1 });
            var result = await service.TranslateSegmentAsync(
                tenantId, projectId, runId, segmentId, 0,
                claim.Execution.Id, claim.Execution.LeaseOwner, claim.Execution.LeaseToken).ConfigureAwait(true);

            Assert.False(result.NeedsReview);
            Assert.Equal(1, fake.Calls);

            using (TenantContext.BeginScope(tenantId))
            {
                using var db = new AppDbContext(pgOptions);
                var version = await db.Set<TranslationVersion>().FirstAsync(v => v.Id == result.SelectedVersionId).ConfigureAwait(true);
                Assert.Empty(version.AlternativeTexts);
                Assert.Equal("hola mundo uno", version.PrimaryText);
            }
        }
    }

    [SkippableFact]
    public async Task Selected_Marked()
    {
        var pg = await StartPostgresAsync().ConfigureAwait(true);
        await using (pg.ConfigureAwait(true))
        {
            var pgOptions = CreatePgOptions(pg);
            await MigrateAsync(pg).ConfigureAwait(true);
            var tenantId = Guid.NewGuid();
            var projectId = Guid.NewGuid();
            var runId = Guid.NewGuid();
            await SeedProjectRunAsync(pgOptions, tenantId, projectId, runId, "{}").ConfigureAwait(true);
            var segmentId = await InsertSegmentAsync(pgOptions, tenantId, projectId, runId, 0, 0, 2000).ConfigureAwait(true);
            var storage = new FakeStorage();
            var artifacts = new ArtifactService(new TestFactory(pgOptions), storage);
            var transcriptArtifactId = await PublishTranscriptArtifactAsync(pgOptions, artifacts, tenantId, projectId, runId, segmentId).ConfigureAwait(true);
            await InsertTranscriptAsync(pgOptions, tenantId, projectId, runId, segmentId, "hello world", transcriptArtifactId).ConfigureAwait(true);
            await InsertContextWindowAsync(pgOptions, artifacts, storage, tenantId, projectId, runId, segmentId, 0, "# target: es\n[0 unknown: hello world]").ConfigureAwait(true);
            var claim = await ClaimSegmentAsync(pgOptions, tenantId, projectId, runId, segmentId).ConfigureAwait(true);

            var fake = new FakeTranslation(
            [
                FakeTranslation.Response("hola mundo", ["hola mundo alt1", "hola mundo alt2"], 0.9),
            ]);
            var service = CreateService(pgOptions, storage, fake, descriptorCount: 1, new TranslationOptions());
            var result = await service.TranslateSegmentAsync(
                tenantId, projectId, runId, segmentId, 0,
                claim.Execution.Id, claim.Execution.LeaseOwner, claim.Execution.LeaseToken).ConfigureAwait(true);

            Assert.False(result.NeedsReview);
            Assert.Null(result.ReviewItemId);
            Assert.Equal("Mock", result.Provider);

            using (TenantContext.BeginScope(tenantId))
            {
                using var db = new AppDbContext(pgOptions);
                var versions = await db.Set<TranslationVersion>().Where(v => v.SegmentId == segmentId).ToListAsync().ConfigureAwait(true);
                var selected = Assert.Single(versions);
                Assert.True(selected.IsSelected);
                Assert.Equal(result.SelectedVersionId, selected.Id);
                Assert.Equal("hola mundo", selected.PrimaryText);
                Assert.Equal(2, selected.AlternativeTexts.Length);
                Assert.NotNull(selected.PromptHash);
                Assert.NotNull(selected.PromptTemplateId);

                var artifact = await db.Set<Artifact>().FirstAsync(a => a.Id == result.TranslationArtifactId).ConfigureAwait(true);
                Assert.Equal(ArtifactType.Translation, artifact.Type);
                Assert.NotNull(artifact.MetadataJson);
                Assert.Contains("\"schemaVersion\":\"1\"", artifact.MetadataJson, StringComparison.Ordinal);
                Assert.Contains(segmentId.ToString("N"), artifact.MetadataJson, StringComparison.Ordinal);

                var execution = await db.Set<StageExecution>().FirstAsync(e => e.Id == claim.Execution.Id).ConfigureAwait(true);
                Assert.Equal(StageStatus.Completed, execution.Status);
                Assert.NotNull(execution.OutputArtifactIdsJson);
                Assert.Contains(result.TranslationArtifactId.ToString("N"), execution.OutputArtifactIdsJson, StringComparison.Ordinal);

                var providerRows = await db.Set<ProviderExecution>().Where(e => e.ProcessingRunId == runId).ToListAsync().ConfigureAwait(true);
                Assert.NotEmpty(providerRows);
                Assert.All(providerRows, e => Assert.Equal(ProviderCapability.Translation, e.Capability));
                Assert.Contains(providerRows, e => e.Outcome == OutcomeClass.Success);
            }
        }
    }

    [SkippableFact]
    public async Task Quality_Routes_Review()
    {
        var pg = await StartPostgresAsync().ConfigureAwait(true);
        await using (pg.ConfigureAwait(true))
        {
            var pgOptions = CreatePgOptions(pg);
            await MigrateAsync(pg).ConfigureAwait(true);
            var tenantId = Guid.NewGuid();
            var projectId = Guid.NewGuid();
            var runId = Guid.NewGuid();
            await SeedProjectRunAsync(pgOptions, tenantId, projectId, runId, "{}").ConfigureAwait(true);
            var segmentId = await InsertSegmentAsync(pgOptions, tenantId, projectId, runId, 0, 0, 2000).ConfigureAwait(true);
            var storage = new FakeStorage();
            var artifacts = new ArtifactService(new TestFactory(pgOptions), storage);
            var transcriptArtifactId = await PublishTranscriptArtifactAsync(pgOptions, artifacts, tenantId, projectId, runId, segmentId).ConfigureAwait(true);
            await InsertTranscriptAsync(pgOptions, tenantId, projectId, runId, segmentId, "hello world", transcriptArtifactId).ConfigureAwait(true);
            await InsertContextWindowAsync(pgOptions, artifacts, storage, tenantId, projectId, runId, segmentId, 0, "# target: es\n[0 unknown: hello world]").ConfigureAwait(true);
            var claim = await ClaimSegmentAsync(pgOptions, tenantId, projectId, runId, segmentId).ConfigureAwait(true);

            var fake = new FakeTranslation(
            [
                FakeTranslation.Response("mumble", ["mumble alt"], 0.2),
                FakeTranslation.Response("mumble again", ["mumble again alt"], 0.25),
            ]);
            var service = CreateService(pgOptions, storage, fake, descriptorCount: 1, new TranslationOptions { QualityThreshold = 0.70 });
            var result = await service.TranslateSegmentAsync(
                tenantId, projectId, runId, segmentId, 0,
                claim.Execution.Id, claim.Execution.LeaseOwner, claim.Execution.LeaseToken).ConfigureAwait(true);

            Assert.True(result.NeedsReview);
            Assert.NotNull(result.ReviewItemId);
            Assert.Equal(2, fake.Calls);

            using (TenantContext.BeginScope(tenantId))
            {
                using var db = new AppDbContext(pgOptions);
                var execution = await db.Set<StageExecution>().FirstAsync(e => e.Id == claim.Execution.Id).ConfigureAwait(true);
                Assert.Equal(StageStatus.ManualReviewRequired, execution.Status);

                var review = await db.Set<ReviewItem>().FirstAsync(r => r.Id == result.ReviewItemId!.Value).ConfigureAwait(true);
                Assert.Equal(ReviewStatus.Open, review.Status);
                Assert.Equal(TranslationService.ReviewReason, review.Reason);
                Assert.Equal(ScopeType.Segment, review.ScopeType);
                Assert.Equal(segmentId, review.SegmentId);

                var version = await db.Set<TranslationVersion>().FirstAsync(v => v.Id == result.SelectedVersionId).ConfigureAwait(true);
                Assert.True(version.IsSelected);
            }
        }
    }

    [SkippableFact]
    public async Task Context_Bounded()
    {
        var pg = await StartPostgresAsync().ConfigureAwait(true);
        await using (pg.ConfigureAwait(true))
        {
            var pgOptions = CreatePgOptions(pg);
            await MigrateAsync(pg).ConfigureAwait(true);
            var tenantId = Guid.NewGuid();
            var projectId = Guid.NewGuid();
            var runId = Guid.NewGuid();
            await SeedProjectRunAsync(pgOptions, tenantId, projectId, runId, "{}").ConfigureAwait(true);
            var segmentId = await InsertSegmentAsync(pgOptions, tenantId, projectId, runId, 0, 0, 2000).ConfigureAwait(true);
            var storage = new FakeStorage();
            var artifacts = new ArtifactService(new TestFactory(pgOptions), storage);
            var transcriptArtifactId = await PublishTranscriptArtifactAsync(pgOptions, artifacts, tenantId, projectId, runId, segmentId).ConfigureAwait(true);
            await InsertTranscriptAsync(pgOptions, tenantId, projectId, runId, segmentId, "hello world", transcriptArtifactId).ConfigureAwait(true);
            var window = await InsertContextWindowAsync(pgOptions, artifacts, storage, tenantId, projectId, runId, segmentId, 0, "# target: es\n[0 unknown: hello world]").ConfigureAwait(true);
            var claim = await ClaimSegmentAsync(pgOptions, tenantId, projectId, runId, segmentId).ConfigureAwait(true);

            var fake = new FakeTranslation(
            [
                FakeTranslation.Response("hola mundo", ["hola mundo alt"], 0.9),
            ]);
            var service = CreateService(pgOptions, storage, fake, descriptorCount: 1, new TranslationOptions());
            var result = await service.TranslateSegmentAsync(
                tenantId, projectId, runId, segmentId, 0,
                claim.Execution.Id, claim.Execution.LeaseOwner, claim.Execution.LeaseToken).ConfigureAwait(true);

            Assert.False(result.NeedsReview);
            Assert.True(result.UsedContext);
            Assert.Equal(window.WindowId, result.ContextWindowId);

            using (TenantContext.BeginScope(tenantId))
            {
                using var db = new AppDbContext(pgOptions);
                var artifact = await db.Set<Artifact>().FirstAsync(a => a.Id == result.TranslationArtifactId).ConfigureAwait(true);
                Assert.NotNull(artifact.MetadataJson);
                Assert.Contains(window.WindowId.ToString("N"), artifact.MetadataJson, StringComparison.Ordinal);
                Assert.Contains(transcriptArtifactId.ToString("N"), artifact.MetadataJson, StringComparison.Ordinal);

                var parents = await db.Set<ArtifactParent>().Where(p => p.ChildArtifactId == artifact.Id).ToListAsync().ConfigureAwait(true);
                Assert.Contains(parents, p => p.ParentArtifactId == transcriptArtifactId);
                Assert.Contains(parents, p => p.ParentArtifactId == window.ArtifactId);
            }
        }
    }

    private TranslationService CreateService(
        DbContextOptions<AppDbContext> pgOptions,
        IArtifactStorage storage,
        ITranslationProvider translation,
        int descriptorCount,
        TranslationOptions translationOptions)
    {
        var factory = new TestFactory(pgOptions);
        var artifacts = new ArtifactService(factory, storage);
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
        return new TranslationService(
            factory, artifacts, translation, resolver, descriptors, recorder,
            costMock.Object,
            global::Microsoft.Extensions.Options.Options.Create(translationOptions),
            global::Microsoft.Extensions.Options.Options.Create(new ProviderOptions()),
            global::Microsoft.Extensions.Options.Options.Create(new RetryOptions()),
            NullLogger<TranslationService>.Instance);
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
                nameof(StageType.Translation), nameof(ScopeType.Segment), segmentId.ToString("D"),
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

    private static async Task<Guid> PublishTranscriptArtifactAsync(
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
            StageType.Transcription, ArtifactType.Transcript,
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


    private static async Task InsertTranscriptAsync(
        DbContextOptions<AppDbContext> options,
        Guid tenantId,
        Guid projectId,
        Guid runId,
        Guid segmentId,
        string text,
        Guid wordsArtifactId)
    {
        var now = DateTimeOffset.UtcNow;
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = new AppDbContext(options);
            db.Set<TranscriptVersion>().Add(new TranscriptVersion(
                Guid.NewGuid(), tenantId, projectId, runId, segmentId,
                "Mock", "mock-1", "en", text, 0.95,
                wordsArtifactId, true, false, now));
            await db.SaveChangesAsync().ConfigureAwait(true);
        }
    }

    private sealed record WindowRef(Guid WindowId, Guid ArtifactId);

    private static async Task<WindowRef> InsertContextWindowAsync(
        DbContextOptions<AppDbContext> options,
        ArtifactService artifacts,
        IArtifactStorage storage,
        Guid tenantId,
        Guid projectId,
        Guid runId,
        Guid segmentId,
        int sequence,
        string contextText)
    {
        var windowId = GuidUtility.From(string.Concat(runId.ToString("N"), ":contextwindow:", sequence.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        var hash = TranslationService.ComputeHash(contextText);
        var now = DateTimeOffset.UtcNow;
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = new AppDbContext(options);
            db.Set<ContextWindow>().Add(new ContextWindow(
                windowId, tenantId, projectId, runId, sequence, contextText, hash, contextText.Length / 4, now));
            await db.SaveChangesAsync().ConfigureAwait(true);
        }

        var payload = string.Concat("{\"schemaVersion\":\"1\",\"sequence\":", sequence.ToString(System.Globalization.CultureInfo.InvariantCulture), ",\"contextHash\":\"", hash, "\",\"windowId\":\"", windowId.ToString("N"), "\"}");
        Guid artifactId;
        using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(payload), writable: false))
        {
            var published = await artifacts.PublishAsync(
                tenantId, projectId, runId,
                StageType.ContextBuild, ArtifactType.ContextWindow,
                stream, ".json", "application/json",
                null, null, null, null, [],
                cancellationToken: CancellationToken.None).ConfigureAwait(true);
            artifactId = published.ArtifactId;
        }

        using (TenantContext.BeginScope(tenantId))
        {
            using var db = new AppDbContext(options);
            await db.Database.ExecuteSqlRawAsync(
                "UPDATE artifacts SET metadata_json = {0} WHERE id = {1} AND tenant_id = {2}",
                payload, artifactId, tenantId).ConfigureAwait(true);
            db.Set<SegmentContextAssignment>().Add(new SegmentContextAssignment(
                GuidUtility.From(string.Concat("ctxassign:", segmentId.ToString("N"), ":", windowId.ToString("N"))),
                tenantId, segmentId, windowId, now));
            await db.SaveChangesAsync().ConfigureAwait(true);
        }

        return new WindowRef(windowId, artifactId);
    }

    private static async Task SeedProjectRunAsync(
        DbContextOptions<AppDbContext> options,
        Guid tenantId,
        Guid projectId,
        Guid runId,
        string settingsJson)
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
                settingsJson, new string('b', 64), null, runId, now, now));
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
            _output.WriteLine($"Docker/PostgreSQL unavailable, skipping translation test: {ex.Message}");
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

    private sealed class FakeTranslation : ITranslationProvider
    {
        private readonly Queue<object> _script;

        public FakeTranslation(IEnumerable<object> script)
        {
            _script = new Queue<object>(script);
        }

        public int Calls { get; private set; }

        public static TranslationResponse Response(string primary, string[] alternatives, double score)
        {
            return new TranslationResponse(
                primary,
                [.. alternatives],
                score,
                score,
                score,
                score,
                "mock-1",
                "1",
                "mock",
                new ProviderUsage(primary.Length / 4, alternatives.Sum(a => a.Length / 4), null, 0),
                null);
        }

        public Task<TranslationResponse> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            if (_script.Count > 0)
            {
                var next = _script.Dequeue();
                if (next is Exception failure)
                {
                    return Task.FromException<TranslationResponse>(failure);
                }

                return Task.FromResult((TranslationResponse)next);
            }

            return Task.FromResult(Response("hola mundo", ["hola mundo alt"], 0.9));
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
