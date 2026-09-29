// Task 040A: cross-layer harness seeder.
//
// Creates the synthetic environment the seam specs run against. There is no
// public provisioning endpoint by design, so the rig seeds through
// `AppDbContext` - the same path the 006-013 integration suites use.
//
// What it guarantees (040A R3 - deterministic, seeded):
//   - migrations are applied (idempotent),
//   - `reset` drops every tenant-scoped row first, so a prior run cannot leak
//     state into this one (040A edge case: "reset runs before seed, always"),
//   - the tenant, the active user, the project and its membership exist,
//   - the project is promoted to `MediaReady` so `POST /processing` accepts a
//     start, and
//   - the result is printed as one JSON line on stdout so the TypeScript harness
//     never has to re-query for ids it just created.
//
// Synthetic only: fixed GUIDs, `corr-*`-style external subjects and a
// `cross-layer.invalid` email domain (RFC 2606 reserved, so it can never be a
// real mailbox). No PII, no real credentials.
//
// Usage:
//   CrossLayerSeed --connection <conn> --tenant <guid> --user <guid>
//                  --project <guid> [--reset]
//
// Exit codes: 0 seeded, 1 usage error, 2 database error.

using System.Text.Json;
using DubbingPlatform.Application.MultiTenancy;
using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Domain.Enums;
using DubbingPlatform.Infrastructure.Persistence;
using DubbingPlatform.Infrastructure.Persistence.Interceptors;
using EFCore.NamingConventions;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace DubbingPlatform.CrossLayer.Seed;

/// <summary>Row counts the harness asserts after a run (040A R2 seam assertions).</summary>
internal static class Program
{
    private const string ExternalSubject = "cross-layer-owner";
    private const string Email = "owner@cross-layer.invalid";
    private const string DisplayName = "Cross Layer Owner";
    private const string ProjectName = "Cross Layer Pilot";

    /// <summary>
    /// A second, fixture-free project. The processing seam needs a project whose
    /// single run slot is free, and the 040A harness smoke already starts a run
    /// on the pilot project - so sharing it makes one spec's run slot another
    /// spec's leftover, which is the 040B edge case "seam passes alone but fails
    /// in full suite". Per-spec isolation is the fix; a shared-tenant shortcut
    /// is not.
    /// </summary>
    private const string PipelineProjectName = "Cross Layer Pipeline";

    /// <summary>
    /// Seam fixture text. Matches the deterministic shape the mock providers
    /// produce (`MockTranscriptionProvider` builds
    /// <c>"mock transcript seg {artifactId} [{language}]"</c>; the translation
    /// mock returns a stable sentinel), so a seam fixture and a provider output
    /// are not two different vocabularies.
    /// </summary>
    private const string MockTranscriptText = "mock transcript seg seed [en]";

    private const string MockTranslationText = "mock translation seg seed [es]";

    private const double MockConfidence = 0.95;

    /// <summary>
    /// Starting <c>SelectionVersion</c> for the seeded segment. 1 (not 0) so the
    /// first edit succeeds and bumps it to 2, leaving a stale writer holding 1 -
    /// which is the conflict the stale-edit seam needs.
    /// </summary>
    private const int InitialSelectionVersion = 1;

    /// <summary>
    /// The exact bytes the export seam writes to object storage and then reads
    /// back through a signed URL. Kept as a byte count plus a matching SHA-256 so
    /// the seeder's content-object metadata and the spec's upload cannot drift.
    /// </summary>
    private static readonly byte[] ExportFixtureBytes =
        System.Text.Encoding.UTF8.GetBytes(
            "{\"schemaVersion\":1,\"format\":\"transcript-json\",\"segments\":[{\"sequence\":1,\"text\":\"mock transcript seg seed [en]\"}]}\n");

    private const string MockContentHash =
        "8f1c2d3e4a5b6c7d8e9f0a1b2c3d4e5f60718293a4b5c6d7e8f90a1b2c3d4e5";

    private const string ExportCompletenessJson =
        "{\"complete\":true,\"missingStages\":[],\"segmentCount\":1,\"includedArtifacts\":[\"transcript\"]}";

    /// <summary>
    /// Valid per <c>ProjectProcessingSettingsValidator</c>: object root,
    /// numeric <c>schemaVersion</c> equal to <c>SupportedSchemaVersion</c>,
    /// four optional non-blank strings of at most 64 chars, a
    /// <c>reviewThreshold</c> in [0, 1] and an optional glossary array. A seeded
    /// project with a null/blank settings blob is rejected at the API boundary,
    /// so a rig that omits it fails every processing-start seam with a 400 that
    /// names a JSON parse error rather than the missing seed data.
    /// </summary>
    private const string ProcessingSettings =
        """
        {
          "schemaVersion": 1,

          "sourceSeparationPolicy": "auto",
          "outputProfile": "standard",
          "timingStrictness": "balanced",
          "voicePolicy": "preserve",
          "reviewThreshold": 0.8,
          "glossary": [],
          "styleInstructions": "Cross-layer harness defaults."
        }
        """;

    private static async Task<int> Main(string[] args)
    {
        try
        {
            var options = SeedOptions.Parse(args);
            await SeedAsync(options).ConfigureAwait(false);
            return 0;
        }
        catch (SeedUsageException ex)
        {
            await Console.Error.WriteLineAsync(ex.Message).ConfigureAwait(false);
            return 1;
        }
        catch (Exception ex)
        {
            // Full chain: the top-level Npgsql message is routinely opaque
            // ("Exception while reading from stream"), so the harness log must
            // carry the inner exception or a rig failure is undiagnosable.
            await Console.Error.WriteLineAsync("CROSS_LAYER_SEED_FAILED: " + Describe(ex)).ConfigureAwait(false);
            await Console.Error.WriteLineAsync(ex.ToString()).ConfigureAwait(false);
            return 2;
        }
    }

    /// <summary>Renders an exception and every inner cause on one line.</summary>
    private static string Describe(Exception exception)
    {
        var parts = new List<string>();
        for (var current = exception; current is not null; current = current.InnerException)
        {
            var suffix = current is PostgresException pg ? $" [SQLSTATE {pg.SqlState}]" : string.Empty;
            parts.Add($"{current.GetType().Name}: {current.Message}{suffix}");
        }

        return string.Join(" <- ", parts);
    }

    private static DbContextOptions<AppDbContext> CreateOptions(string connectionString) =>
        new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention()
            .AddInterceptors(new TenantSessionInterceptor())
            .Options;

    private static async Task SeedAsync(SeedOptions options)
    {
        // Frozen clock: 040A R3 requires deterministic seeded data, so every
        // timestamp comes from one captured instant rather than the wall clock.
        var now = new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

        using (TenantContext.BeginMaintenanceScope())
        {
            await using var context = new AppDbContext(CreateOptions(options.ConnectionString));
            await context.Database.MigrateAsync().ConfigureAwait(false);
        }

        if (options.Reset)
        {
            await ResetAsync(options).ConfigureAwait(false);
        }

        // Tenant, user, project and membership go in one tenant-scoped unit of
        // work. Two details are load-bearing and were both found the hard way:
        //   * exactly one SaveChangesAsync - adding entities to a context that is
        //     then disposed persists nothing, which produced a rig that reported
        //     success while `tenant_users` stayed empty and every later login
        //     returned 401 INVALID_CREDENTIALS.
        //   * the membership probe keys on the (project, user) pair, never on
        //     `m.Id == Guid.NewGuid()` - that predicate is trivially false and
        //     would duplicate the row on every re-seed.
        using (TenantContext.BeginScope(options.TenantId))
        {
            await using var context = new AppDbContext(CreateOptions(options.ConnectionString));

            if (!await context.Tenants.AnyAsync(t => t.Id == options.TenantId).ConfigureAwait(false))
            {
                context.Tenants.Add(new Tenant(
                    options.TenantId,
                    $"Cross-layer tenant {options.TenantId:N}",
                    $"cross-layer-{options.TenantId:N}",
                    now));
            }

            if (!await context.Set<TenantUser>().AnyAsync(u => u.Id == options.UserId).ConfigureAwait(false))
            {
                context.Set<TenantUser>().Add(new TenantUser(
                    options.UserId,
                    options.TenantId,
                    ExternalSubject,
                    Email,
                    DisplayName,
                    TenantUserStatus.Active,
                    now,
                    now));
            }

            if (!await context.Set<DubbingProject>().AnyAsync(p => p.Id == options.ProjectId).ConfigureAwait(false))
            {
                context.Set<DubbingProject>().Add(new DubbingProject(
                    options.ProjectId,
                    options.TenantId,
                    "en",
                    "es",
                    ProjectStatus.Created,
                    "{}",
                    new string('a', 64),
                    null,
                    null,
                    now,
                    now,
                    ProjectName,
                    "Cross-layer harness project.",
                    options.UserId,
                    options.UserId,
                    options.UserId,
                    isArchived: false,
                    archivedAt: null,
                    settingsVersion: 1,
                    processingSettingsJson: ProcessingSettings));
            }

            var memberExists = await context.Set<ProjectMembership>()
                .AnyAsync(m => m.ProjectId == options.ProjectId && m.UserId == options.UserId)
                .ConfigureAwait(false);
            if (!memberExists)
            {
                context.Set<ProjectMembership>().Add(new ProjectMembership(
                    Guid.NewGuid(),
                    options.TenantId,
                    options.ProjectId,
                    options.UserId,
                    ProjectRole.ProjectOwner,
                    options.UserId,
                    now));
            }

            // The pipeline project: same shape, no fixtures, and its own
            // membership so the processing seam can act on it without sharing the
            // pilot project's single run slot.
            if (!await context.Set<DubbingProject>().AnyAsync(p => p.Id == options.PipelineProjectId).ConfigureAwait(false))
            {
                context.Set<DubbingProject>().Add(new DubbingProject(
                    options.PipelineProjectId,
                    options.TenantId,
                    "en",
                    "es",
                    ProjectStatus.Created,
                    "{}",
                    new string('e', 64),
                    null,
                    null,
                    now,
                    now,
                    PipelineProjectName,
                    "Cross-layer pipeline seam project.",
                    options.UserId,
                    options.UserId,
                    options.UserId,
                    isArchived: false,
                    archivedAt: null,
                    settingsVersion: 1,
                    processingSettingsJson: ProcessingSettings));
            }

            if (!await context.Set<ProjectMembership>().AnyAsync(m => m.ProjectId == options.PipelineProjectId && m.UserId == options.UserId).ConfigureAwait(false))
            {
                context.Set<ProjectMembership>().Add(new ProjectMembership(
                    Guid.NewGuid(),
                    options.TenantId,
                    options.PipelineProjectId,
                    options.UserId,
                    ProjectRole.ProjectOwner,
                    options.UserId,
                    now));
            }

            await context.SaveChangesAsync().ConfigureAwait(false);
        }

        await PromoteToMediaReadyAsync(options, now).ConfigureAwait(false);
        var fixtures = await SeedSeamFixturesAsync(options, now).ConfigureAwait(false);

        await VerifyAsync(options, fixtures).ConfigureAwait(false);

        var result = new
        {
            tenantId = options.TenantId.ToString("D"),
            userId = options.UserId.ToString("D"),
            projectId = options.ProjectId.ToString("D"),
            pipelineProjectId = options.PipelineProjectId.ToString("D"),
            externalSubject = ExternalSubject,
            // The seeder never sees a token; the harness obtains one via
            // POST /auth/login, which is part of the seam it is proving.
            projectStatus = ProjectStatus.MediaReady.ToString(),
            seededAt = now.ToString("O"),
            // Seam fixture ids (Task 040B). Exposed so a seam spec addresses the
            // exact row it mutates instead of searching for "a" row, which would
            // make a fixture change silently retarget a seam.
            fixtures = new
            {
                completedRunId = fixtures.CompletedRunId.ToString("D"),
                segmentId = fixtures.SegmentId.ToString("D"),
                transcriptVersionId = fixtures.TranscriptVersionId.ToString("D"),
                translationVersionId = fixtures.TranslationVersionId.ToString("D"),
                reviewItemId = fixtures.ReviewItemId.ToString("D"),
                speakerId = fixtures.SpeakerId.ToString("D"),
                voiceProfileAId = fixtures.VoiceProfileAId.ToString("D"),
                voiceProfileBId = fixtures.VoiceProfileBId.ToString("D"),
                notificationId = fixtures.NotificationId.ToString("D"),
                exportJobId = fixtures.ExportJobId.ToString("D"),
                exportArtifactContentKey = fixtures.ExportContentKey,
            },
        };
        await Console.Out.WriteLineAsync(JsonSerializer.Serialize(result)).ConfigureAwait(false);
    }

    /// <summary>Ids of the rows Task 040B seams mutate.</summary>
    private sealed record SeamFixtures(
        Guid CompletedRunId,
        Guid SegmentId,
        Guid TranscriptVersionId,
        Guid TranslationVersionId,
        Guid ReviewItemId,
        Guid SpeakerId,
        Guid VoiceProfileAId,
        Guid VoiceProfileBId,
        Guid NotificationId,
        Guid ExportJobId,
        string ExportContentKey);

    /// <summary>
    /// Creates the minimum domain state the 040B seams mutate.
    ///
    /// <para>
    /// Why this exists: five of the seven seams act on entities the pipeline
    /// would create (segments, review items, speakers, exports, notifications),
    /// and the rig cannot run the pipeline. The media workers that would produce
    /// them are FFmpeg-backed and are not in the rig, the seeded
    /// <c>ContentObject</c> has no bytes in object storage, and media validation
    /// therefore never completes - the run sits in <c>Pending</c> at
    /// <c>MediaValidation</c> indefinitely.
    /// </para>
    ///
    /// <para>
    /// So the rig seeds the row a seam needs and the seam proves the
    /// <em>mutation</em> seam across frontend, API and database. It does not
    /// prove ingestion, and it must not be described as if it did: a seam written
    /// here is a test of the write path over known state, not of the DAG.
    /// </para>
    ///
    /// <para>
    /// The anchor is a <em>completed</em> run. A pending or running run would
    /// make <c>POST /processing</c> answer 409 RUN_ALREADY_ACTIVE and break the
    /// 040A smoke, and a completed run is also what the export path prefers when
    /// resolving a job.
    /// </para>
    /// </summary>
    private static async Task<SeamFixtures> SeedSeamFixturesAsync(SeedOptions options, DateTimeOffset now)
    {
        using (TenantContext.BeginScope(options.TenantId))
        {
            await using var context = new AppDbContext(CreateOptions(options.ConnectionString));

            var anchorRunId = Guid.NewGuid();
            context.Set<ProcessingRun>().Add(new ProcessingRun(
                anchorRunId, options.TenantId, options.ProjectId, 1,
                ProcessingRunStatus.Completed, "1.0.0",
                new string('a', 64), new string('b', 64), new string('c', 64),
                now, now, now, now));

            // The pipeline project's own completed anchor. A project with no run
            // at all is fine for a start, but the export path resolves a run and a
            // per-project anchor keeps each project self-consistent.
            context.Set<ProcessingRun>().Add(new ProcessingRun(
                Guid.NewGuid(), options.TenantId, options.PipelineProjectId, 1,
                ProcessingRunStatus.Completed, "1.0.0",
                new string('a', 64), new string('b', 64), new string('c', 64),
                now, now, now, now));

            // One segment with a selected transcript and translation. The
            // selected versions are what a stale-edit seam edits against, and
            // `SelectionVersion` starts at 1 so the first edit succeeds and the
            // second (stale) one conflicts.
            var segmentId = Guid.NewGuid();
            context.Set<SpeechSegment>().Add(new SpeechSegment(
                segmentId, options.TenantId, options.ProjectId, anchorRunId,
                1, 0, 2000, "Ready", null, now));

            var transcriptVersionId = Guid.NewGuid();
            context.Set<TranscriptVersion>().Add(new TranscriptVersion(
                transcriptVersionId, options.TenantId, options.ProjectId, anchorRunId, segmentId,
                "mock", "mock-transcribe", "en", MockTranscriptText,
                MockConfidence, null, isSelected: true, needsReview: false, now));

            var translationVersionId = Guid.NewGuid();
            context.Set<TranslationVersion>().Add(new TranslationVersion(
                translationVersionId, options.TenantId, options.ProjectId, anchorRunId, segmentId,
                MockTranslationText, [], 0.9, 0.9, 0.9, "mock", "mock-translate",
                null, null, isSelected: true, now));

            // The selection pointer is a separate aggregate, and it is what the
            // segment read exposes as `selectedTranscriptVersionId` /
            // `SelectionVersion`. Without this row the segment reads as having no
            // selection at all and `selectionVersion` is 0, so a stale-edit seam
            // would have no version to make stale.
            context.Set<SegmentSelection>().Add(new SegmentSelection(
                Guid.NewGuid(), options.TenantId, options.ProjectId, segmentId,
                transcriptVersionId, translationVersionId, null,
                InitialSelectionVersion, now, options.UserId));

            // A speaker with two selectable voices, so the voice seam can change
            // the assignment and observe the dependent invalidation.
            var speakerId = Guid.NewGuid();
            context.Set<Speaker>().Add(new Speaker(
                speakerId, options.TenantId, options.ProjectId, "spk-1", "Speaker 1",
                0, 2000, "mock", "1", 0.9, "mock", now));

            var voiceA = Guid.NewGuid();
            context.Set<VoiceProfile>().Add(new VoiceProfile(
                voiceA, options.TenantId, "mock", "mock-voice-a", "1", "es",
                VoiceType.Stock, false, null, now));

            var voiceB = Guid.NewGuid();
            context.Set<VoiceProfile>().Add(new VoiceProfile(
                voiceB, options.TenantId, "mock", "mock-voice-b", "1", "es",
                VoiceType.Stock, false, null, now));

            context.Set<SpeakerVoiceAssignment>().Add(new SpeakerVoiceAssignment(
                Guid.NewGuid(), options.TenantId, options.ProjectId, anchorRunId, speakerId, voiceA,
                "seeded", new string('d', 64), now));

            // An open review item scoped to the segment, so the review seam has a
            // real decision to make.
            var reviewItemId = Guid.NewGuid();
            context.Set<ReviewItem>().Add(new ReviewItem(
                reviewItemId, options.TenantId, options.ProjectId, anchorRunId,
                ScopeType.Segment, segmentId.ToString("D"), segmentId,
                ReviewStatus.Open, "low-confidence", "{}", now, now, null));

            // An unread notification, so the notification seam can prove a
            // durable row reaches the centre and that read state is a mutation.
            var notificationId = Guid.NewGuid();
            context.Set<Notification>().Add(new Notification(
                notificationId, options.TenantId, options.UserId, options.ProjectId,
                NotificationType.ManualReviewRequired, NotificationSeverity.Warning,
                "Review required", "A segment needs review.",
                "project", options.ProjectId.ToString("D"), null, null, now, null));

            // A completed export backed by a content object. The bytes are written
            // to object storage by the seam spec itself (via `mc` in the storage
            // container), so the download proves real bytes travelled through a
            // real signed URL rather than a stubbed response.
            var exportJobId = Guid.NewGuid();
            var contentId = Guid.NewGuid();
            var contentKey = string.Concat(options.TenantId.ToString("N"), "/seed/export.json");
            var contentHash = MockContentHash;

            context.Set<ContentObject>().Add(new ContentObject(
                contentId, options.TenantId, contentHash, contentHash,
                ExportFixtureBytes.Length, "application/json", contentKey,
                ContentObjectStatus.Committed, now, now));

            var artifactId = Guid.NewGuid();
            context.Set<Artifact>().Add(new Artifact(
                artifactId, options.TenantId, options.ProjectId, anchorRunId,
                null, ArtifactType.Export, "1", contentId, null, null,
                null, null, ArtifactStatus.Committed, null, now));

            // `artifactIdRef` must carry the artifact id: the download path parses
            // it and raises 409 EXPORT_NOT_READY when it is blank, so a completed
            // job without it is not actually downloadable.
            context.Set<ExportJob>().Add(new ExportJob(
                exportJobId, options.TenantId, options.ProjectId, anchorRunId,
                ExportFormat.TranscriptJson, ExportJobStatus.Completed,
                artifactId.ToString("D"), ExportCompletenessJson, false, now, now));

            context.Set<ExportArtifact>().Add(new ExportArtifact(
                Guid.NewGuid(), options.TenantId, exportJobId, artifactId, now));

            await context.SaveChangesAsync().ConfigureAwait(false);

            return new SeamFixtures(
                anchorRunId, segmentId, transcriptVersionId, translationVersionId,
                reviewItemId, speakerId, voiceA, voiceB, notificationId,
                exportJobId, contentKey);
        }
    }

    /// <summary>
    /// Tables that must survive a reset.
    ///
    /// The transport tables are owned by the message bus rather than by the
    /// domain: the API is already running and holds live transport state, and
    /// truncating them would disturb consumers that are not part of a reset.
    ///
    /// <c>__EFMigrationsHistory</c> is EF's record of applied migrations.
    /// Truncating it makes the next <c>MigrateAsync</c> re-apply every migration
    /// onto a schema that already exists, which fails immediately with
    /// 42P07 "relation already exists" - the schema must be preserved and only
    /// its rows discarded.
    /// </summary>
    private static readonly string[] PreservedTables =
    [
        "inbox_state",
        "outbox_state",
        "outbox_message",
        "__EFMigrationsHistory",
    ];

    /// <summary>
    /// Wipes every domain table so a run cannot inherit state from the previous
    /// one (040A edge case: "Leftover state from prior run -> reset runs before
    /// seed, always").
    ///
    /// <para>
    /// This truncates rather than deleting by <c>tenant_id</c> for three
    /// reasons. The schema has 53 tenant-scoped tables and a hand-maintained
    /// delete list silently rots - an omitted table leaks state, and the seam
    /// then fails much later for the wrong reason (a stale
    /// <c>processing_runs</c> row made a fresh start return
    /// 409 RUN_ALREADY_ACTIVE). The foreign-key graph contains cycles, so no
    /// delete order satisfies it. And a truncate is atomic and fast.
    /// </para>
    ///
    /// <para>
    /// The table list is discovered from <c>information_schema</c> rather than
    /// hard-coded, and each identifier is re-validated before it reaches SQL
    /// because it is interpolated into a statement.
    /// </para>
    /// </summary>
    private static async Task ResetAsync(SeedOptions options)
    {
        using (TenantContext.BeginMaintenanceScope())
        {
            await using var context = new AppDbContext(CreateOptions(options.ConnectionString));

            var connection = context.Database.GetDbConnection();
            await connection.OpenAsync().ConfigureAwait(false);

            var tables = new List<string>();
            await using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    """
                    SELECT table_name
                    FROM information_schema.tables
                    WHERE table_schema = 'public' AND table_type = 'BASE TABLE'
                    ORDER BY table_name
                    """;

                await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
                while (await reader.ReadAsync().ConfigureAwait(false))
                {
                    tables.Add(reader.GetString(0));
                }
            }

            var targets = tables
                .Where(name => !PreservedTables.Contains(name, StringComparer.Ordinal))
                .ToArray();

            if (targets.Length == 0)
            {
                throw new SeedVerificationException(
                    "Reset found no domain tables. The schema is missing, so seeding " +
                    "would recreate a rig that cannot prove anything.");
            }

            foreach (var table in targets)
            {
                if (!IsSafeIdentifier(table))
                {
                    throw new SeedVerificationException($"Refusing to truncate unexpected table name '{table}'.");
                }
            }

            var list = string.Join(", ", targets.Select(name => $"\"{name}\""));
            await context.Database
                .ExecuteSqlRawAsync($"TRUNCATE TABLE {list} RESTART IDENTITY CASCADE;")
                .ConfigureAwait(false);
        }
    }

    private static bool IsSafeIdentifier(string value) =>
        value.Length > 0
        && value.Length <= 63
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character == '_')
        && !char.IsAsciiDigit(value[0]);

    /// <summary>
    /// Read-after-write proof that every row the harness depends on is really
    /// committed. Without this the seeder can print a success payload while the
    /// database is empty, and the failure surfaces three steps later as an
    /// unrelated 401. Failing here keeps the blast radius at the rig.
    /// </summary>
    private static async Task VerifyAsync(SeedOptions options, SeamFixtures fixtures)
    {
        using (TenantContext.BeginMaintenanceScope())
        {
            await using var context = new AppDbContext(CreateOptions(options.ConnectionString));

            var checks = new (string Label, bool Ok)[]
            {
                ("tenant", await context.Tenants.AnyAsync(t => t.Id == options.TenantId).ConfigureAwait(false)),
                ("tenant_user", await context.Set<TenantUser>()
                    .AnyAsync(u => u.TenantId == options.TenantId && u.ExternalSubject == ExternalSubject)
                    .ConfigureAwait(false)),
                ("project", await context.Set<DubbingProject>().AnyAsync(p => p.Id == options.ProjectId).ConfigureAwait(false)),
                ("project_membership", await context.Set<ProjectMembership>()
                    .AnyAsync(m => m.ProjectId == options.ProjectId && m.UserId == options.UserId)
                    .ConfigureAwait(false)),
                ("source_media", await context.Set<MediaAsset>()
                    .AnyAsync(a => a.ProjectId == options.ProjectId)
                    .ConfigureAwait(false)),
            };

            var missing = checks.Where(c => !c.Ok).Select(c => c.Label).ToArray();
            if (missing.Length > 0)
            {
                throw new SeedVerificationException(
                    "Seed completed without persisting: missing " + string.Join(", ", missing) +
                    ". The rig is unusable; do not run seam specs against it.");
            }

            // The 040A smoke posts a start of its own, so exactly one pre-existing
            // run (the completed anchor the seam fixtures hang off) is correct. Any
            // other count means a prior run leaked an active run into this one.
            var runs = await context.Set<ProcessingRun>()
                .IgnoreQueryFilters()
                .Where(r => r.TenantId == options.TenantId)
                .ToListAsync()
                .ConfigureAwait(false);
            // One completed anchor run per seeded project, and nothing else: the
            // 040A smoke and the processing seam each start their own run on
            // their own project, and a surviving run from a previous session makes
            // the next start fail with 409 RUN_ALREADY_ACTIVE.
            var expectedAnchors = 2;
            var anchors = runs
                .Where(r => r.Status == ProcessingRunStatus.Completed
                    && (r.Id == fixtures.CompletedRunId || r.ProjectId == options.PipelineProjectId))
                .ToArray();
            if (runs.Count != expectedAnchors || anchors.Length != expectedAnchors)
            {
                throw new SeedVerificationException(
                    $"Expected exactly {expectedAnchors} completed anchor runs for tenant " +
                    $"{options.TenantId}, found {runs.Count}. A surviving run from a previous " +
                    "session makes the next start fail with 409 RUN_ALREADY_ACTIVE.");
            }

            // Every seam fixture must be readable, so a seam never fails on
            // "fixture missing" and gets misdiagnosed as a seam defect.
            var fixtureChecks = new (string Label, bool Ok)[]
            {
                ("segment", await context.Set<SpeechSegment>().AnyAsync(s => s.Id == fixtures.SegmentId).ConfigureAwait(false)),
                ("transcript_version", await context.Set<TranscriptVersion>().AnyAsync(t => t.Id == fixtures.TranscriptVersionId).ConfigureAwait(false)),
                ("translation_version", await context.Set<TranslationVersion>().AnyAsync(t => t.Id == fixtures.TranslationVersionId).ConfigureAwait(false)),
                ("review_item", await context.Set<ReviewItem>().AnyAsync(r => r.Id == fixtures.ReviewItemId).ConfigureAwait(false)),
                ("speaker", await context.Set<Speaker>().AnyAsync(s => s.Id == fixtures.SpeakerId).ConfigureAwait(false)),
                ("voice_profile_a", await context.Set<VoiceProfile>().AnyAsync(v => v.Id == fixtures.VoiceProfileAId).ConfigureAwait(false)),
                ("voice_profile_b", await context.Set<VoiceProfile>().AnyAsync(v => v.Id == fixtures.VoiceProfileBId).ConfigureAwait(false)),
                ("notification", await context.Set<Notification>().AnyAsync(n => n.Id == fixtures.NotificationId).ConfigureAwait(false)),
                ("export_job", await context.Set<ExportJob>().AnyAsync(e => e.Id == fixtures.ExportJobId).ConfigureAwait(false)),
            };

            var missingFixtures = fixtureChecks.Where(c => !c.Ok).Select(c => c.Label).ToArray();
            if (missingFixtures.Length > 0)
            {
                throw new SeedVerificationException(
                    "Seam fixtures missing after seeding: " + string.Join(", ", missingFixtures) +
                    ". The 040B seams would fail as 'not found' and be misread as seam defects.");
            }
        }
    }

    /// <summary>
    /// Attaches a committed source media row and flips the project to
    /// `MediaReady` so processing can start. Mirrors the promotion the 006-013
    /// integration suite uses: a real ingestion pipeline (FFprobe + an AI
    /// provider) is out of scope for a rig smoke, but the seam under test is
    /// start -> SSE, not ingestion.
    /// </summary>
    /// <summary>
    /// A stable 64-hex content hash derived from the project id. Deterministic
    /// (the rig must produce identical rows every run) and unique per project,
    /// because <c>ix_content_objects_tenant_id_content_hash</c> is unique per
    /// tenant.
    /// </summary>
    private static string MockDeterministicHash(Guid projectId) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(projectId.ToByteArray()))
            .ToLowerInvariant();

    private static async Task PromoteToMediaReadyAsync(SeedOptions options, DateTimeOffset now)
    {
        // Both seeded projects are promoted, so either can accept a processing
        // start. Done per project rather than once, because the processing seam
        // has its own project and a start on a Created project is a 409.
        foreach (var projectId in new[] { options.ProjectId, options.PipelineProjectId })
        {
            // `ix_content_objects_tenant_id_content_hash` is unique per tenant, so
            // the hash must differ per project even though the "media" is
            // nominally the same synthetic 1 KiB payload.
            var hash = MockDeterministicHash(projectId);
            using (TenantContext.BeginScope(options.TenantId))
            {
                await using var context = new AppDbContext(CreateOptions(options.ConnectionString));
                var hasMedia = await context.Set<MediaAsset>()
                    .AnyAsync(m => m.ProjectId == projectId)
                    .ConfigureAwait(false);
                if (hasMedia)
                {
                    continue;
                }

                var contentId = Guid.NewGuid();
                var assetId = Guid.NewGuid();
                // Keyed per project: the storage key carries a uniqueness
                // constraint, so two projects sharing `{tenant}/seed/source.mp4`
                // fail the second insert. (Found by running it.)
                var contentKey = string.Concat(
                    options.TenantId.ToString("N"), "/seed/", projectId.ToString("N"), "/source.mp4");
                context.Set<ContentObject>().Add(new ContentObject(
                    contentId,
                    options.TenantId,
                    hash,
                    hash,
                    1024,
                    "video/mp4",
                    contentKey,
                    ContentObjectStatus.Committed,
                    now,
                    now));
                context.Set<MediaAsset>().Add(new MediaAsset(
                    assetId,
                    options.TenantId,
                    projectId,
                    contentId,
                    "source.mp4",
                    "mp4",
                    "aac",
                    "h264",
                    1024,
                    2000,
                    48000,
                    2,
                    "stereo",
                    MediaAssetStatus.Valid,
                    null,
                    hash,
                    now));
                await context.SaveChangesAsync().ConfigureAwait(false);
            }
        }

        using (TenantContext.BeginMaintenanceScope())
        {
            await using var context = new AppDbContext(CreateOptions(options.ConnectionString));
            foreach (var projectId in new[] { options.ProjectId, options.PipelineProjectId })
            {
                await context.Database.ExecuteSqlRawAsync(
                    "UPDATE dubbing_projects SET status = {0}, updated_at = {1} WHERE id = {2}",
                    ProjectStatus.MediaReady.ToString(),
                    now,
                    projectId).ConfigureAwait(false);
            }
        }
    }
}

internal sealed class SeedUsageException : Exception
{
    public SeedUsageException(string message)
        : base(message)
    {
    }
}

internal sealed class SeedVerificationException : Exception
{
    public SeedVerificationException(string message)
        : base(message)
    {
    }
}

internal sealed record SeedOptions(
    string ConnectionString,
    Guid TenantId,
    Guid UserId,
    Guid ProjectId,
    Guid PipelineProjectId,
    bool Reset)
{
    public const string Usage =
        "Usage: CrossLayerSeed --connection <conn> --tenant <guid> --user <guid> " +
        "--project <guid> --pipeline-project <guid> [--reset]";

    public static SeedOptions Parse(string[] args)
    {
        string? connection = null;
        var tenant = Guid.Empty;
        var user = Guid.Empty;
        var project = Guid.Empty;
        var pipelineProject = Guid.Empty;
        var reset = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--connection" when i + 1 < args.Length:
                    connection = args[++i];
                    break;
                case "--tenant" when i + 1 < args.Length:
                    tenant = Guid.Parse(args[++i]);
                    break;
                case "--user" when i + 1 < args.Length:
                    user = Guid.Parse(args[++i]);
                    break;
                case "--project" when i + 1 < args.Length:
                    project = Guid.Parse(args[++i]);
                    break;
                case "--pipeline-project" when i + 1 < args.Length:
                    pipelineProject = Guid.Parse(args[++i]);
                    break;
                case "--reset":
                    reset = true;
                    break;
                default:
                    throw new SeedUsageException($"Unrecognised argument '{args[i]}'. {Usage}");
            }
        }

        if (string.IsNullOrWhiteSpace(connection))
        {
            throw new SeedUsageException($"--connection is required. {Usage}");
        }

        if (tenant == Guid.Empty || user == Guid.Empty || project == Guid.Empty || pipelineProject == Guid.Empty)
        {
            throw new SeedUsageException(
                $"--tenant, --user, --project and --pipeline-project must be non-empty GUIDs. {Usage}");
        }

        if (project == pipelineProject)
        {
            throw new SeedUsageException(
                "--project and --pipeline-project must differ: the pipeline seam needs its own " +
                "run slot so it cannot collide with the harness smoke's run.");
        }

        return new SeedOptions(connection, tenant, user, project, pipelineProject, reset);
    }
}
