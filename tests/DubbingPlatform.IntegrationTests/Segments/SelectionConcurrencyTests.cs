using System.Data.Common;
using System.Net.Sockets;
using DubbingPlatform.Application.Errors;
using DubbingPlatform.Application.Exceptions;
using DubbingPlatform.Application.MultiTenancy;
using DubbingPlatform.Application.Segments;
using DubbingPlatform.Application.Services;
using DubbingPlatform.Contracts.Messages;
using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Domain.Enums;
using DubbingPlatform.Domain.Exceptions;
using DubbingPlatform.Infrastructure.Persistence;
using DubbingPlatform.Infrastructure.Persistence.Interceptors;
using EFCore.NamingConventions;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using Xunit.Abstractions;

namespace DubbingPlatform.IntegrationTests.Segments;

public sealed class SelectionConcurrencyTests
{
    private readonly ITestOutputHelper _output;

    public SelectionConcurrencyTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void Reason_Sanitized_Strips_Html_And_Truncates()
    {
        Assert.Null(SegmentSelectionService.SanitizeReason(null));
        Assert.Null(SegmentSelectionService.SanitizeReason("   "));
        Assert.Null(SegmentSelectionService.SanitizeReason("<br/>"));

        var cleaned = SegmentSelectionService.SanitizeReason("<b>reviewer</b> picked <script>v2</script>");
        Assert.NotNull(cleaned);
        Assert.DoesNotContain("<", cleaned, StringComparison.Ordinal);
        Assert.DoesNotContain(">", cleaned, StringComparison.Ordinal);
        Assert.Contains("reviewer", cleaned, StringComparison.Ordinal);

        var longReason = new string('r', SegmentSelectionService.MaxReasonLength + 50);
        var truncated = SegmentSelectionService.SanitizeReason(longReason);
        Assert.NotNull(truncated);
        Assert.Equal(SegmentSelectionService.MaxReasonLength, truncated.Length);
    }

    [Fact]
    public void Manual_Text_Requires_NonEmpty_And_Bounded()
    {
        Assert.Throws<DomainException>(() => SegmentSelectionService.RequireManualText(null));
        Assert.Throws<DomainException>(() => SegmentSelectionService.RequireManualText("  "));
        Assert.Throws<DomainException>(() => SegmentSelectionService.RequireManualText(new string('x', SegmentSelectionService.MaxManualTextLength + 1)));
        Assert.Equal("hello", SegmentSelectionService.RequireManualText("  hello  "));
    }

    [Fact]
    public void Conflict_Exception_Carries_Current_Version_And_Maps_409()
    {
        var transcriptId = Guid.NewGuid();
        var ex = new SegmentSelectionConflictException(3, transcriptId, null);

        Assert.Equal(ErrorCodes.Conflict, ex.ErrorCode);
        Assert.Equal(409, ex.StatusCode);
        Assert.Contains(SegmentSelectionService.SelectionConflictMarker, ex.Message, StringComparison.Ordinal);
        Assert.Equal(3, ex.CurrentSelectionVersion);
        Assert.Equal(transcriptId, ex.CurrentTranscriptVersionId);
        Assert.Null(ex.CurrentTranslationVersionId);
    }

    [Fact]
    public void Selection_Message_Uses_Frozen_Envelope()
    {
        var tenantId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var segmentId = Guid.NewGuid();
        var actorId = Guid.NewGuid();

        var message = SegmentSelectionService.BuildMessage(
            tenantId, projectId, runId, segmentId, 2,
            Guid.NewGuid(), null, "reason", actorId, false);

        Assert.Equal(MessageVersionPolicy.CurrentVersion, message.SchemaVersion);
        Assert.Equal(tenantId, message.TenantId);
        Assert.Equal(projectId, message.ProjectId);
        Assert.Equal(runId, message.ProcessingRunId);
        Assert.Equal(segmentId, message.SegmentId);
        Assert.Equal("Segment", message.ScopeType);
        Assert.Equal(2, message.SelectionVersion);
        Assert.Equal(actorId, message.UpdatedByUserId);
        Assert.False(message.OutputStale);
        Assert.NotEqual(Guid.Empty, message.MessageId);
        Assert.NotEqual(string.Empty, message.CorrelationId);
    }

    [Fact]
    public void Selection_Entity_Rejects_Empty_Ids_And_Negative_Version()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.Throws<DomainException>(() => new SegmentSelection(
            Guid.Empty, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            null, null, null, 0, now, Guid.NewGuid()));
        Assert.Throws<DomainException>(() => new SegmentSelection(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            null, null, null, -1, now, Guid.NewGuid()));
        Assert.Throws<DomainException>(() => new SegmentSelection(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.Empty, null, null, 0, now, Guid.NewGuid()));

        var selection = new SegmentSelection(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            null, null, null, 0, now, Guid.NewGuid());
        selection.ApplySelection(Guid.NewGuid(), null, null, Guid.NewGuid(), now);
        Assert.Equal(1, selection.SelectionVersion);
        Assert.Throws<DomainException>(() => selection.ApplySelection(Guid.Empty, null, null, Guid.NewGuid(), now));
    }

    [SkippableFact]
    public async Task Happy_Path_Select_Transcript_Then_Translation()
    {
        var container = await StartContainerAsync().ConfigureAwait(true);
        await using (container.ConfigureAwait(true))
        {
            var options = CreateOptions(container);
            await MigrateAsync(options).ConfigureAwait(true);
            var seed = await SeedAsync(options).ConfigureAwait(true);
            var publisher = new FakePublisher();

            var selectService = CreateService(options, publisher);
            var first = await selectService.SelectTranscriptAsync(
                seed.TenantId, seed.ProjectId, seed.SegmentId, seed.TranscriptV1,
                0, seed.EditorId, "prefer <b>v1</b>").ConfigureAwait(true);

            Assert.Equal(1, first.SelectionVersion);
            Assert.Equal(seed.TranscriptV1, first.SelectedTranscriptVersionId);
            Assert.False(first.OutputStale);
            Assert.Null(first.WarningCode);
            Assert.Null(first.NewVersionId);

            var second = await selectService.SelectTranslationAsync(
                seed.TenantId, seed.ProjectId, seed.SegmentId, seed.TranslationV1,
                1, seed.EditorId, null).ConfigureAwait(true);

            Assert.Equal(2, second.SelectionVersion);
            Assert.Equal(seed.TranscriptV1, second.SelectedTranscriptVersionId);
            Assert.Equal(seed.TranslationV1, second.SelectedTranslationVersionId);

            var readService = CreateService(options, publisher);
            var current = await readService.GetAsync(seed.TenantId, seed.ProjectId, seed.SegmentId).ConfigureAwait(true);
            Assert.NotNull(current);
            Assert.Equal(2, current.SelectionVersion);
            Assert.Equal(seed.TranscriptV1, current.SelectedTranscriptVersionId);
            Assert.Equal(seed.TranslationV1, current.SelectedTranslationVersionId);
            Assert.Equal(seed.EditorId, current.UpdatedByUserId);

            using (TenantContext.BeginScope(seed.TenantId))
            {
                using var db = new AppDbContext(options);
                var transcripts = await db.Set<TranscriptVersion>()
                    .AsNoTracking()
                    .Where(v => v.SegmentId == seed.SegmentId)
                    .ToListAsync().ConfigureAwait(true);
                Assert.Contains(transcripts, v => v.Id == seed.TranscriptV1 && v.IsSelected);
                Assert.Contains(transcripts, v => v.Id == seed.TranscriptV2 && !v.IsSelected);

                var audits = await db.Set<AuditEvent>()
                    .AsNoTracking()
                    .Where(a => a.Action == SegmentSelectionService.AuditAction)
                    .ToListAsync().ConfigureAwait(true);
                Assert.Equal(2, audits.Count);
                Assert.All(audits, a => Assert.Equal(seed.EditorId.ToString("D"), a.Actor));
                Assert.All(audits, a => Assert.Equal(seed.SegmentId.ToString("N"), a.ResourceId));
                Assert.DoesNotContain(audits, a => a.DetailsJson != null && a.DetailsJson.Contains('<'));
            }

            Assert.Equal(2, publisher.Messages.Count);
            Assert.Equal(1, publisher.Messages[0].SelectionVersion);
            Assert.Equal(2, publisher.Messages[1].SelectionVersion);
            Assert.All(publisher.Messages, m => Assert.Equal(seed.SegmentId, m.SegmentId));
        }
    }

    [SkippableFact]
    public async Task Stale_Write_Rejected_With_409_And_State_Unchanged()
    {
        var container = await StartContainerAsync().ConfigureAwait(true);
        await using (container.ConfigureAwait(true))
        {
            var options = CreateOptions(container);
            await MigrateAsync(options).ConfigureAwait(true);
            var seed = await SeedAsync(options).ConfigureAwait(true);
            var publisher = new FakePublisher();

            var service = CreateService(options, publisher);
            await service.SelectTranscriptAsync(
                seed.TenantId, seed.ProjectId, seed.SegmentId, seed.TranscriptV1,
                0, seed.EditorId, null).ConfigureAwait(true);

            var staleService = CreateService(options, publisher);
            var ex = await Assert.ThrowsAsync<SegmentSelectionConflictException>(() => staleService.SelectTranslationAsync(
                seed.TenantId, seed.ProjectId, seed.SegmentId, seed.TranslationV1,
                0, seed.EditorId, null)).ConfigureAwait(true);

            Assert.Equal(ErrorCodes.Conflict, ex.ErrorCode);
            Assert.Equal(409, ex.StatusCode);
            Assert.Contains(SegmentSelectionService.SelectionConflictMarker, ex.Message, StringComparison.Ordinal);
            Assert.Equal(1, ex.CurrentSelectionVersion);
            Assert.Equal(seed.TranscriptV1, ex.CurrentTranscriptVersionId);
            Assert.Null(ex.CurrentTranslationVersionId);

            var readService = CreateService(options, publisher);
            var current = await readService.GetAsync(seed.TenantId, seed.ProjectId, seed.SegmentId).ConfigureAwait(true);
            Assert.NotNull(current);
            Assert.Equal(1, current.SelectionVersion);
            Assert.Null(current.SelectedTranslationVersionId);

            Assert.Single(publisher.Messages);
        }
    }

    [SkippableFact]
    public async Task Concurrent_Selects_Serialize_To_One_Winner()
    {
        var container = await StartContainerAsync().ConfigureAwait(true);
        await using (container.ConfigureAwait(true))
        {
            var options = CreateOptions(container);
            await MigrateAsync(options).ConfigureAwait(true);
            var seed = await SeedAsync(options).ConfigureAwait(true);
            var publisher = new FakePublisher();

            var setup = CreateService(options, publisher);
            await setup.SelectTranscriptAsync(
                seed.TenantId, seed.ProjectId, seed.SegmentId, seed.TranscriptV1,
                0, seed.EditorId, null).ConfigureAwait(true);

            var serviceA = CreateService(options, publisher);
            var serviceB = CreateService(options, publisher);
            var attemptA = Task.Run(() => TrySelectAsync(
                serviceA, seed.TenantId, seed.ProjectId, seed.SegmentId,
                seed.TranscriptV2, 1, seed.EditorId));
            var attemptB = Task.Run(() => TrySelectAsync(
                serviceB, seed.TenantId, seed.ProjectId, seed.SegmentId,
                seed.TranscriptV1, 1, seed.OwnerId));

            var outcomes = await Task.WhenAll(attemptA, attemptB).ConfigureAwait(true);
            Assert.Equal(1, outcomes.Count(o => o));
            Assert.Equal(1, outcomes.Count(o => !o));

            var readService = CreateService(options, publisher);
            var current = await readService.GetAsync(seed.TenantId, seed.ProjectId, seed.SegmentId).ConfigureAwait(true);
            Assert.NotNull(current);
            Assert.Equal(2, current.SelectionVersion);

            Assert.Equal(2, publisher.Messages.Count);
        }
    }

    [SkippableFact]
    public async Task Manual_Edits_Create_New_Immutable_Versions()
    {
        var container = await StartContainerAsync().ConfigureAwait(true);
        await using (container.ConfigureAwait(true))
        {
            var options = CreateOptions(container);
            await MigrateAsync(options).ConfigureAwait(true);
            var seed = await SeedAsync(options).ConfigureAwait(true);
            var publisher = new FakePublisher();

            var service = CreateService(options, publisher);
            var edited = await service.CreateManualTranscriptVersionAsync(
                seed.TenantId, seed.ProjectId, seed.SegmentId,
                0, "  corrected transcript  ", seed.EditorId, "fix typo").ConfigureAwait(true);

            Assert.Equal(1, edited.SelectionVersion);
            Assert.NotNull(edited.NewVersionId);
            Assert.NotEqual(seed.TranscriptV1, edited.NewVersionId.Value);
            Assert.Equal(edited.NewVersionId, edited.SelectedTranscriptVersionId);
            Assert.False(edited.OutputStale);

            var translateService = CreateService(options, publisher);
            var translated = await translateService.CreateManualTranslationVersionAsync(
                seed.TenantId, seed.ProjectId, seed.SegmentId,
                1, "korrigierte Übersetzung", seed.EditorId, null).ConfigureAwait(true);

            Assert.Equal(2, translated.SelectionVersion);
            Assert.NotNull(translated.NewVersionId);
            Assert.Equal(translated.NewVersionId, translated.SelectedTranslationVersionId);
            Assert.Equal(edited.NewVersionId, translated.SelectedTranscriptVersionId);

            using (TenantContext.BeginScope(seed.TenantId))
            {
                using var db = new AppDbContext(options);
                var transcripts = await db.Set<TranscriptVersion>()
                    .AsNoTracking()
                    .Where(v => v.SegmentId == seed.SegmentId)
                    .OrderBy(v => v.CreatedAt)
                    .ToListAsync().ConfigureAwait(true);
                Assert.Equal(3, transcripts.Count);
                Assert.Equal("hello world", transcripts[0].Text);
                Assert.False(transcripts[0].IsSelected);
                Assert.Equal("corrected transcript", transcripts[2].Text);
                Assert.True(transcripts[2].IsSelected);
                Assert.Equal("manual", transcripts[2].Provider);

                var translations = await db.Set<TranslationVersion>()
                    .AsNoTracking()
                    .Where(v => v.SegmentId == seed.SegmentId)
                    .ToListAsync().ConfigureAwait(true);
                Assert.Equal(2, translations.Count);
                Assert.Single(translations, v => v.IsSelected);
            }
        }
    }

    [SkippableFact]
    public async Task Missing_Version_Returns_404_And_Foreign_Version_Returns_400()
    {
        var container = await StartContainerAsync().ConfigureAwait(true);
        await using (container.ConfigureAwait(true))
        {
            var options = CreateOptions(container);
            await MigrateAsync(options).ConfigureAwait(true);
            var seed = await SeedAsync(options).ConfigureAwait(true);
            var publisher = new FakePublisher();

            var service = CreateService(options, publisher);
            var missing = await Assert.ThrowsAsync<NotFoundException>(() => service.SelectTranscriptAsync(
                seed.TenantId, seed.ProjectId, seed.SegmentId, Guid.NewGuid(),
                0, seed.EditorId, null)).ConfigureAwait(true);
            Assert.Contains(SegmentSelectionService.VersionNotFoundMarker, missing.Message, StringComparison.Ordinal);

            var foreign = await Assert.ThrowsAsync<DomainException>(() => service.SelectTranscriptAsync(
                seed.TenantId, seed.ProjectId, seed.SegmentId, seed.OtherSegmentTranscript,
                0, seed.EditorId, null)).ConfigureAwait(true);
            Assert.Contains(SegmentSelectionService.VersionSegmentMismatchMarker, foreign.Message, StringComparison.Ordinal);

            Assert.Empty(publisher.Messages);
        }
    }

    [SkippableFact]
    public async Task Cross_Tenant_Blocked_For_Foreign_Tenant_And_Non_Member()
    {
        var container = await StartContainerAsync().ConfigureAwait(true);
        await using (container.ConfigureAwait(true))
        {
            var options = CreateOptions(container);
            await MigrateAsync(options).ConfigureAwait(true);
            var seed = await SeedAsync(options).ConfigureAwait(true);
            var publisher = new FakePublisher();

            var foreignTenant = CreateService(options, publisher);
            await Assert.ThrowsAsync<ForbiddenException>(() => foreignTenant.SelectTranscriptAsync(
                Guid.NewGuid(), seed.ProjectId, seed.SegmentId, seed.TranscriptV1,
                0, seed.EditorId, null)).ConfigureAwait(true);

            var outsiderId = Guid.NewGuid();
            using (TenantContext.BeginScope(seed.TenantId))
            {
                using var db = new AppDbContext(options);
                db.Set<TenantUser>().Add(new TenantUser(
                    outsiderId, seed.TenantId, string.Concat("sub-", outsiderId.ToString("N")),
                    "outsider@example.com", "Outsider", TenantUserStatus.Active,
                    DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
                await db.SaveChangesAsync().ConfigureAwait(true);
            }

            var nonMember = CreateService(options, publisher);
            await Assert.ThrowsAsync<ForbiddenException>(() => nonMember.SelectTranscriptAsync(
                seed.TenantId, seed.ProjectId, seed.SegmentId, seed.TranscriptV1,
                0, outsiderId, null)).ConfigureAwait(true);

            Assert.Empty(publisher.Messages);
        }
    }

    [SkippableFact]
    public async Task Edit_After_Output_Finalized_Flags_Stale_With_Warning()
    {
        var container = await StartContainerAsync().ConfigureAwait(true);
        await using (container.ConfigureAwait(true))
        {
            var options = CreateOptions(container);
            await MigrateAsync(options).ConfigureAwait(true);
            var seed = await SeedAsync(options).ConfigureAwait(true);
            var publisher = new FakePublisher();

            var now = DateTimeOffset.UtcNow;
            using (TenantContext.BeginScope(seed.TenantId))
            {
                using var db = new AppDbContext(options);
                db.Set<OutputAsset>().Add(new OutputAsset(
                    Guid.NewGuid(), seed.TenantId, seed.ProjectId, seed.RunId,
                    Guid.NewGuid(), "Video", 1000, "mp4", now));
                await db.SaveChangesAsync().ConfigureAwait(true);
            }

            var service = CreateService(options, publisher);
            var result = await service.CreateManualTranslationVersionAsync(
                seed.TenantId, seed.ProjectId, seed.SegmentId,
                0, "späte Korrektur", seed.EditorId, "post-render fix").ConfigureAwait(true);

            Assert.True(result.OutputStale);
            Assert.Equal(SegmentSelectionService.OutputStaleWarningCode, result.WarningCode);
            Assert.Equal(1, result.SelectionVersion);

            Assert.Single(publisher.Messages);
            Assert.True(publisher.Messages[0].OutputStale);
        }
    }

    private static async Task<bool> TrySelectAsync(
        SegmentSelectionService service,
        Guid tenantId,
        Guid projectId,
        Guid segmentId,
        Guid versionId,
        int expectedVersion,
        Guid actorId)
    {
        try
        {
            await service.SelectTranscriptAsync(
                tenantId, projectId, segmentId, versionId,
                expectedVersion, actorId, null).ConfigureAwait(true);
            return true;
        }
        catch (SegmentSelectionConflictException)
        {
            return false;
        }
    }

    private SegmentSelectionService CreateService(DbContextOptions<AppDbContext> options, FakePublisher publisher)
    {
        return new SegmentSelectionService(new TestFactory(options), publisher);
    }

    private sealed record SeedData(
        Guid TenantId,
        Guid ProjectId,
        Guid OwnerId,
        Guid EditorId,
        Guid RunId,
        Guid SegmentId,
        Guid TranscriptV1,
        Guid TranscriptV2,
        Guid TranslationV1,
        Guid OtherSegmentId,
        Guid OtherSegmentTranscript);

    private static async Task<SeedData> SeedAsync(DbContextOptions<AppDbContext> options)
    {
        var now = DateTimeOffset.UtcNow;
        var tenantId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        var editorId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var segmentId = Guid.NewGuid();
        var otherSegmentId = Guid.NewGuid();
        var transcriptV1 = Guid.NewGuid();
        var transcriptV2 = Guid.NewGuid();
        var translationV1 = Guid.NewGuid();
        var otherTranscript = Guid.NewGuid();

        using (TenantContext.BeginScope(tenantId))
        {
            using var db = new AppDbContext(options);
            db.Tenants.Add(new Tenant(tenantId, "Selection", $"selection-{tenantId:N}", now));
            db.DubbingProjects.Add(new DubbingProject(
                projectId, tenantId, "en", "de", ProjectStatus.Created,
                "{}", new string('a', 64), null, null, now, now,
                "Selection project", null, ownerId));
            db.Set<TenantUser>().Add(new TenantUser(
                ownerId, tenantId, "sub-owner", "owner@example.com", "Owner",
                TenantUserStatus.Active, now, now));
            db.Set<TenantUser>().Add(new TenantUser(
                editorId, tenantId, "sub-editor", "editor@example.com", "Editor",
                TenantUserStatus.Active, now, now));
            db.Set<ProjectMembership>().Add(new ProjectMembership(
                Guid.NewGuid(), tenantId, projectId, editorId, ProjectRole.ProjectEditor, ownerId, now));
            db.Set<ProcessingRun>().Add(new ProcessingRun(
                runId, tenantId, projectId, 0, ProcessingRunStatus.Running, "v1",
                new string('a', 64), new string('b', 64), new string('c', 64),
                now, now, now, null));
            db.Set<SpeechSegment>().Add(new SpeechSegment(
                segmentId, tenantId, projectId, runId, 0, 0, 1000, "Pending", null, now));
            db.Set<SpeechSegment>().Add(new SpeechSegment(
                otherSegmentId, tenantId, projectId, runId, 1, 1000, 2000, "Pending", null, now));
            db.Set<TranscriptVersion>().Add(new TranscriptVersion(
                transcriptV1, tenantId, projectId, runId, segmentId,
                "mock", "mock-v1", "en", "hello world", 0.9, null, false, false, now));
            db.Set<TranscriptVersion>().Add(new TranscriptVersion(
                transcriptV2, tenantId, projectId, runId, segmentId,
                "mock", "mock-v1", "en", "hello there", 0.8, null, false, false, now));
            db.Set<TranslationVersion>().Add(new TranslationVersion(
                translationV1, tenantId, projectId, runId, segmentId,
                "hallo Welt", [], 0.9, 0.9, 0.9, "mock", "mock-v1", null, null, false, now));
            db.Set<TranscriptVersion>().Add(new TranscriptVersion(
                otherTranscript, tenantId, projectId, runId, otherSegmentId,
                "mock", "mock-v1", "en", "other segment", 0.9, null, false, false, now));
            await db.SaveChangesAsync().ConfigureAwait(true);
        }

        return new SeedData(
            tenantId, projectId, ownerId, editorId, runId, segmentId,
            transcriptV1, transcriptV2, translationV1, otherSegmentId, otherTranscript);
    }

    private static async Task MigrateAsync(DbContextOptions<AppDbContext> options)
    {
        using (TenantContext.BeginMaintenanceScope())
        {
            using var db = new AppDbContext(options);
            await db.Database.MigrateAsync().ConfigureAwait(true);
        }
    }

    private static DbContextOptions<AppDbContext> CreateOptions(PostgreSqlContainer container)
    {
        return new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(container.GetConnectionString())
            .UseSnakeCaseNamingConvention()
            .AddInterceptors(new TenantSessionInterceptor())
            .Options;
    }

    private async Task<PostgreSqlContainer> StartContainerAsync()
    {
        try
        {
            var container = new PostgreSqlBuilder("postgres:16-alpine").Build();
            await container.StartAsync().ConfigureAwait(true);
            return container;
        }
#pragma warning disable CA1031 // Docker availability probe: any transport-level failure means "skip", domain failures surface later.
        catch (Exception ex) when (IsInfrastructureUnavailable(ex))
#pragma warning restore CA1031
        {
            _output.WriteLine($"Docker/PostgreSQL unavailable, skipping selection test: {ex.Message}");
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

    private sealed class FakePublisher : ISegmentSelectionEventPublisher
    {
        private readonly object _gate = new();

        public List<SegmentSelectionChanged> Messages { get; } = [];

        public Task PublishAsync(SegmentSelectionChanged message, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(message);
            lock (_gate)
            {
                Messages.Add(message);
            }

            return Task.CompletedTask;
        }
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
