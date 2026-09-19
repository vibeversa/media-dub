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

namespace DubbingPlatform.IntegrationTests.Media;

/// <summary>
/// Task 021: source separation policy, normalized confidence, fallback, and
/// audit over PG with fake storage/ffprobe/provider (no MinIO/ffmpeg needed).
/// Skips with an explicit message when Docker (PG) is unavailable (CI live).
/// Mock provider uses configurable confidence 0.5 (fallback) / 0.9 (select).
/// </summary>
public sealed class SeparationWorkerTests
{
    private readonly ITestOutputHelper _output;

    public SeparationWorkerTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [SkippableFact]
    public async Task Disabled_Skips()
    {
        var pg = await StartPostgresAsync().ConfigureAwait(true);
        await using (pg.ConfigureAwait(true))
        {
            var pgOptions = CreatePgOptions(pg);
            await MigrateAsync(pg).ConfigureAwait(true);
            var tenantId = Guid.NewGuid();
            var projectId = Guid.NewGuid();
            var runId = Guid.NewGuid();
            await SeedProjectRunAsync(pgOptions, tenantId, projectId, runId, "{\"sourceSeparation\":\"disabled\"}").ConfigureAwait(true);

            var storage = new FakeStorage();
            var artifacts = new ArtifactService(new TestFactory(pgOptions), storage);
            var canonicalId = await PublishCanonicalAsync(artifacts, tenantId, projectId, runId).ConfigureAwait(true);

            var stages = new StageExecutionService(new TestFactory(pgOptions), global::Microsoft.Extensions.Options.Options.Create(new RetryOptions()));
            StageClaimResult claim;
            using (TenantContext.BeginScope(tenantId))
            {
                claim = await stages.ClaimAsync(
                    tenantId, projectId, runId,
                    nameof(StageType.SourceSeparation), nameof(ScopeType.Project), projectId.ToString("D"),
                    null, 0, "test-owner", TimeSpan.FromMinutes(5)).ConfigureAwait(true);
            }

            var fakeSeparation = new FakeSeparation(0.9);
            var service = CreateService(pgOptions, storage, fakeSeparation);
            var decision = await service.DecideAsync(
                tenantId, projectId, runId, canonicalId,
                claim.Execution.Id, claim.Execution.LeaseOwner, claim.Execution.LeaseToken).ConfigureAwait(true);

            Assert.True(decision.IsSkipped);
            Assert.False(decision.IsFallback);
            Assert.Equal(canonicalId, decision.SelectedDialogueArtifactId);
            Assert.False(fakeSeparation.Called);

            using (TenantContext.BeginScope(tenantId))
            {
                using var db = new AppDbContext(pgOptions);
                var execution = await db.Set<StageExecution>().FirstAsync(e => e.Id == claim.Execution.Id).ConfigureAwait(true);
                Assert.Equal(StageStatus.Skipped, execution.Status);
                Assert.NotNull(execution.OutputArtifactIdsJson);
                Assert.Contains(canonicalId.ToString("N"), execution.OutputArtifactIdsJson, StringComparison.Ordinal);

                var stems = await db.Set<Artifact>()
                    .CountAsync(a => a.ProcessingRunId == runId && (a.Type == ArtifactType.DialogueStem || a.Type == ArtifactType.BackgroundStem)).ConfigureAwait(true);
                Assert.Equal(0, stems);

                var executions = await db.Set<ProviderExecution>()
                    .CountAsync(e => e.ProcessingRunId == runId).ConfigureAwait(true);
                Assert.Equal(0, executions);
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
            await SeedProjectRunAsync(pgOptions, tenantId, projectId, runId, "{\"sourceSeparation\":\"enabled\"}").ConfigureAwait(true);

            var storage = new FakeStorage();
            var artifacts = new ArtifactService(new TestFactory(pgOptions), storage);
            var canonicalId = await PublishCanonicalAsync(artifacts, tenantId, projectId, runId).ConfigureAwait(true);

            var stages = new StageExecutionService(new TestFactory(pgOptions), global::Microsoft.Extensions.Options.Options.Create(new RetryOptions()));
            StageClaimResult claim;
            using (TenantContext.BeginScope(tenantId))
            {
                claim = await stages.ClaimAsync(
                    tenantId, projectId, runId,
                    nameof(StageType.SourceSeparation), nameof(ScopeType.Project), projectId.ToString("D"),
                    null, 0, "test-owner", TimeSpan.FromMinutes(5)).ConfigureAwait(true);
            }

            var service = CreateService(pgOptions, storage, new FakeSeparation(0.5));
            var decision = await service.DecideAsync(
                tenantId, projectId, runId, canonicalId,
                claim.Execution.Id, claim.Execution.LeaseOwner, claim.Execution.LeaseToken).ConfigureAwait(true);

            Assert.False(decision.IsSkipped);
            Assert.True(decision.IsFallback);
            Assert.Equal(canonicalId, decision.SelectedDialogueArtifactId);
            Assert.Equal(0.5, decision.RawConfidence!.Value, precision: 9);
            Assert.True(decision.NormalizedConfidence!.Value < 0.70);
            Assert.NotNull(decision.FallbackReason);

            using (TenantContext.BeginScope(tenantId))
            {
                using var db = new AppDbContext(pgOptions);
                var execution = await db.Set<StageExecution>().FirstAsync(e => e.Id == claim.Execution.Id).ConfigureAwait(true);
                Assert.Equal(StageStatus.Completed, execution.Status);
                Assert.Equal(SourceSeparationService.FallbackCode, execution.ErrorCode);
                Assert.NotNull(execution.ErrorMessage);
                Assert.Contains(canonicalId.ToString("N"), execution.OutputArtifactIdsJson!, StringComparison.Ordinal);

                var stems = await db.Set<Artifact>()
                    .CountAsync(a => a.ProcessingRunId == runId && (a.Type == ArtifactType.DialogueStem || a.Type == ArtifactType.BackgroundStem)).ConfigureAwait(true);
                Assert.Equal(0, stems);
            }
        }
    }

    [SkippableFact]
    public async Task High_Confidence_Selects()
    {
        var pg = await StartPostgresAsync().ConfigureAwait(true);
        await using (pg.ConfigureAwait(true))
        {
            var pgOptions = CreatePgOptions(pg);
            await MigrateAsync(pg).ConfigureAwait(true);
            var tenantId = Guid.NewGuid();
            var projectId = Guid.NewGuid();
            var runId = Guid.NewGuid();
            await SeedProjectRunAsync(pgOptions, tenantId, projectId, runId, "{\"sourceSeparation\":\"enabled\"}").ConfigureAwait(true);

            var storage = new FakeStorage();
            var artifacts = new ArtifactService(new TestFactory(pgOptions), storage);
            var canonicalId = await PublishCanonicalAsync(artifacts, tenantId, projectId, runId).ConfigureAwait(true);

            var stages = new StageExecutionService(new TestFactory(pgOptions), global::Microsoft.Extensions.Options.Options.Create(new RetryOptions()));
            StageClaimResult claim;
            using (TenantContext.BeginScope(tenantId))
            {
                claim = await stages.ClaimAsync(
                    tenantId, projectId, runId,
                    nameof(StageType.SourceSeparation), nameof(ScopeType.Project), projectId.ToString("D"),
                    null, 0, "test-owner", TimeSpan.FromMinutes(5)).ConfigureAwait(true);
            }

            var service = CreateService(pgOptions, storage, new FakeSeparation(0.9));
            var decision = await service.DecideAsync(
                tenantId, projectId, runId, canonicalId,
                claim.Execution.Id, claim.Execution.LeaseOwner, claim.Execution.LeaseToken).ConfigureAwait(true);

            Assert.False(decision.IsSkipped);
            Assert.False(decision.IsFallback);
            Assert.NotNull(decision.DialogueArtifactId);
            Assert.NotNull(decision.BackgroundArtifactId);
            Assert.Equal(decision.DialogueArtifactId.Value, decision.SelectedDialogueArtifactId);
            Assert.True(decision.NormalizedConfidence!.Value >= 0.70);

            using (TenantContext.BeginScope(tenantId))
            {
                using var db = new AppDbContext(pgOptions);
                var execution = await db.Set<StageExecution>().FirstAsync(e => e.Id == claim.Execution.Id).ConfigureAwait(true);
                Assert.Equal(StageStatus.Completed, execution.Status);
                Assert.Null(execution.ErrorCode);
                Assert.Contains(decision.DialogueArtifactId.Value.ToString("N"), execution.OutputArtifactIdsJson!, StringComparison.Ordinal);

                var dialogue = await db.Set<Artifact>().FirstAsync(a => a.Id == decision.DialogueArtifactId.Value).ConfigureAwait(true);
                Assert.Equal(ArtifactType.DialogueStem, dialogue.Type);
                Assert.Equal(ArtifactStatus.Committed, dialogue.Status);
                Assert.NotNull(dialogue.MetadataJson);
                Assert.Contains("0.9", dialogue.MetadataJson, StringComparison.Ordinal);

                var background = await db.Set<Artifact>().FirstAsync(a => a.Id == decision.BackgroundArtifactId!.Value).ConfigureAwait(true);
                Assert.Equal(ArtifactType.BackgroundStem, background.Type);

                var parents = await db.Set<ArtifactParent>()
                    .Where(p => p.ChildArtifactId == dialogue.Id)
                    .Select(p => p.ParentArtifactId)
                    .ToListAsync().ConfigureAwait(true);
                Assert.Contains(canonicalId, parents);
            }
        }
    }

    [SkippableFact]
    public async Task Warning_Recorded()
    {
        var pg = await StartPostgresAsync().ConfigureAwait(true);
        await using (pg.ConfigureAwait(true))
        {
            var pgOptions = CreatePgOptions(pg);
            await MigrateAsync(pg).ConfigureAwait(true);
            var tenantId = Guid.NewGuid();
            var projectId = Guid.NewGuid();
            var runId = Guid.NewGuid();
            await SeedProjectRunAsync(pgOptions, tenantId, projectId, runId, "{\"sourceSeparation\":\"enabled\"}").ConfigureAwait(true);

            var storage = new FakeStorage();
            var artifacts = new ArtifactService(new TestFactory(pgOptions), storage);
            var canonicalId = await PublishCanonicalAsync(artifacts, tenantId, projectId, runId).ConfigureAwait(true);

            var stages = new StageExecutionService(new TestFactory(pgOptions), global::Microsoft.Extensions.Options.Options.Create(new RetryOptions()));
            StageClaimResult claim;
            using (TenantContext.BeginScope(tenantId))
            {
                claim = await stages.ClaimAsync(
                    tenantId, projectId, runId,
                    nameof(StageType.SourceSeparation), nameof(ScopeType.Project), projectId.ToString("D"),
                    null, 0, "test-owner", TimeSpan.FromMinutes(5)).ConfigureAwait(true);
            }

            var service = CreateService(pgOptions, storage, new FakeSeparation(0.5));
            await service.DecideAsync(
                tenantId, projectId, runId, canonicalId,
                claim.Execution.Id, claim.Execution.LeaseOwner, claim.Execution.LeaseToken).ConfigureAwait(true);

            using (TenantContext.BeginScope(tenantId))
            {
                using var db = new AppDbContext(pgOptions);
                var warning = await db.Set<QualityResult>()
                    .Where(q => q.ProcessingRunId == runId && q.Code == SourceSeparationService.FallbackCode)
                    .FirstOrDefaultAsync().ConfigureAwait(true);
                Assert.NotNull(warning);
                Assert.Equal("Warning", warning.Severity);
                Assert.Equal(QualityStatus.PassWithWarnings, warning.Status);
                Assert.Equal(canonicalId, warning.ArtifactId);
                Assert.NotNull(warning.DetailsJson);
                Assert.Contains("0.5", warning.DetailsJson, StringComparison.Ordinal);
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
            await SeedProjectRunAsync(pgOptions, tenantId, projectId, runId, "{\"sourceSeparation\":\"enabled\"}").ConfigureAwait(true);

            var storage = new FakeStorage();
            var artifacts = new ArtifactService(new TestFactory(pgOptions), storage);
            var canonicalId = await PublishCanonicalAsync(artifacts, tenantId, projectId, runId).ConfigureAwait(true);

            var stages = new StageExecutionService(new TestFactory(pgOptions), global::Microsoft.Extensions.Options.Options.Create(new RetryOptions()));
            StageClaimResult claim;
            using (TenantContext.BeginScope(tenantId))
            {
                claim = await stages.ClaimAsync(
                    tenantId, projectId, runId,
                    nameof(StageType.SourceSeparation), nameof(ScopeType.Project), projectId.ToString("D"),
                    null, 0, "test-owner", TimeSpan.FromMinutes(5)).ConfigureAwait(true);
            }

            var service = CreateService(pgOptions, storage, new FakeSeparation(0.9));
            await service.DecideAsync(
                tenantId, projectId, runId, canonicalId,
                claim.Execution.Id, claim.Execution.LeaseOwner, claim.Execution.LeaseToken).ConfigureAwait(true);

            using (TenantContext.BeginScope(tenantId))
            {
                using var db = new AppDbContext(pgOptions);
                var row = await db.Set<ProviderExecution>()
                    .Where(e => e.ProcessingRunId == runId)
                    .FirstOrDefaultAsync().ConfigureAwait(true);
                Assert.NotNull(row);
                Assert.Equal(ProviderType.Mock, row.Provider);
                Assert.Equal(ProviderCapability.SourceSeparation, row.Capability);
                Assert.Equal(OutcomeClass.Success, row.Outcome);
                Assert.Equal(claim.Execution.Id, row.StageExecutionId);
                Assert.NotNull(row.ProviderIdempotencyKey);
                Assert.NotNull(row.RequestHash);
                Assert.NotNull(row.ResponseHash);
            }
        }
    }

    private SourceSeparationService CreateService(
        DbContextOptions<AppDbContext> pgOptions,
        IArtifactStorage storage,
        ISourceSeparationProvider separation)
    {
        var factory = new TestFactory(pgOptions);
        var artifacts = new ArtifactService(factory, storage);
        var ffprobe = new FakeFfprobe();
        var descriptors = new FakeDescriptors();
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
        return new SourceSeparationService(
            factory, artifacts, storage, ffprobe, separation, resolver, descriptors, recorder,
            global::Microsoft.Extensions.Options.Options.Create(new MediaOptions()),
            global::Microsoft.Extensions.Options.Options.Create(new RetryOptions()),
            NullLogger<SourceSeparationService>.Instance);
    }

    private static async Task<Guid> PublishCanonicalAsync(
        ArtifactService artifacts,
        Guid tenantId,
        Guid projectId,
        Guid runId)
    {
        var bytes = Encoding.UTF8.GetBytes("canonical-flac-bytes-for-separation-tests");
        using var stream = new MemoryStream(bytes, writable: false);
        var published = await artifacts.PublishAsync(
            tenantId, projectId, runId,
            StageType.AudioPreparation, ArtifactType.CanonicalAudio,
            stream, ".flac", "audio/flac",
            null, null, null, null, [],
            cancellationToken: CancellationToken.None).ConfigureAwait(true);
        return published.ArtifactId;
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
                1024, 2000, 48000, 2, "stereo", MediaAssetStatus.Valid, null, hash, now));
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
            _output.WriteLine($"Docker/PostgreSQL unavailable, skipping separation test: {ex.Message}");
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

    private sealed class FakeFfprobe : IFFprobeService
    {
        public Task<FfprobeResult> ProbeAsync(string storageKeyOrLocalPath, CancellationToken cancellationToken)
        {
            IReadOnlyList<FfprobeStream> streams =
            [
                new FfprobeStream("audio", "flac", null, null, null, 48000, 2, "stereo"),
            ];
            return Task.FromResult(new FfprobeResult("flac", 2000, streams));
        }
    }

    private sealed class FakeSeparation : ISourceSeparationProvider
    {
        private readonly double _confidence;

        public FakeSeparation(double confidence)
        {
            _confidence = confidence;
        }

        public bool Called { get; private set; }

        public Task<SeparationResponse> SeparateAsync(SeparationRequest request, CancellationToken cancellationToken)
        {
            Called = true;
            var dialogue = string.Concat("prov-dialogue-", _confidence.ToString(System.Globalization.CultureInfo.InvariantCulture));
            var background = string.Concat("prov-bg-", _confidence.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return Task.FromResult(new SeparationResponse(
                dialogue, background, _confidence, "mock-1", "1", "mock",
                new ProviderUsage(null, null, 2.0, 0), null));
        }
    }

    private sealed class FakeDescriptors : IDescriptorStore
    {
        public Task<IReadOnlyList<ProviderCapabilityDescriptor>> GetCandidatesAsync(
            ProviderCapability capability,
            Guid tenantId,
            CancellationToken cancellationToken = default)
        {
            var now = DateTimeOffset.UtcNow;
            IReadOnlyList<ProviderCapabilityDescriptor> candidates =
            [
                new ProviderCapabilityDescriptor(
                    Guid.NewGuid(), tenantId, ProviderType.Mock, capability,
                    [], [], 0, 0, false, false, true, true, [], true, [],
                    "0-1", "{}", "{}", "standard", "global", 1, now),
            ];
            return Task.FromResult(candidates);
        }

        public bool IsCompatible(ProviderCapabilityDescriptor descriptor, ProviderRoutingRequest request)
        {
            return true;
        }
    }
}
