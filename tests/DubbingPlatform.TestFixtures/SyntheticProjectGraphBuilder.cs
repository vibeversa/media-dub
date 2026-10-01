using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Domain.Enums;

namespace DubbingPlatform.TestFixtures;

/// <summary>
/// Builds the synthetic project graph: project, memberships, run, segments,
/// transcript/translation versions, selection, speakers, voices, review items,
/// notifications and exports (Task 046 instruction 3 - "incl.
/// runs/segments/review items" - and R3 "tenant-isolated graphs").
/// </summary>
/// <remarks>
/// <para>
/// <b>Why one builder and not eleven.</b> The graph has foreign keys in both
/// directions and a cycle in it (project -&gt; active run -&gt; project). A
/// builder that hands rows back for the caller to assemble cannot express that;
/// one that hands back a flat list in dependency order can, but the caller still
/// has to know the order and to get it right for every fixture. One wired graph
/// makes referential integrity a property of the fixture rather than a hope
/// about a caller's assembly sequence.
/// </para>
/// <para>
/// <b>Why the anchor run is Completed, and why every graph has one.</b>
/// <c>POST /processing</c> answers <c>409 RUN_ALREADY_ACTIVE</c> only when the
/// project already has a run in an <em>active</em> status
/// (<c>ProcessingController.FindActiveRunAsync</c> filters on
/// <c>ActiveStatuses</c>), so a Completed anchor is invisible to a start - which
/// is what lets a processing-start test run against a project that already has a
/// run history.
/// </para>
/// <para>
/// An earlier draft of this file made the "startable" variant a project with
/// <em>no run at all</em>. That does not build, and the reason is worth
/// recording: <c>SpeechSegment.Validate</c> requires a non-empty
/// <c>RunId</c>, so a run-less project cannot carry a segment, and the segment
/// subtree is most of what a feature spec needs. The distinction that actually
/// matters is not "has a run" but "has an active run", so both variants carry a
/// Completed anchor and differ only in project id - which is the isolation a
/// start test actually needs, because a start it shares with another spec is a
/// test of that spec's leftover.
/// </para>
/// </remarks>
public static class SyntheticProjectGraphBuilder
{
    /// <summary>
    /// The project-id label of the anchor graph built by <see cref="BuildGraph"/>.
    /// </summary>
    public const string AnchorProjectLabel = "project";

    /// <summary>
    /// The project-id label of the graph built by <see cref="BuildStartableGraph"/>.
    /// </summary>
    public const string StartableProjectLabel = "project/startable";

    /// <summary>
    /// Builds the anchor graph: one project, one Completed run, one segment and
    /// the full review/voice/export/notification set.
    /// </summary>
    /// <remarks>
    /// This is the graph a mutation test should use. Every row is present and
    /// wired, so a spec can act on any of them without first building it.
    /// </remarks>
    /// <param name="tenantSeed">Seed for id derivation; the unit of isolation.</param>
    /// <param name="tenantId">Owning tenant.</param>
    /// <param name="ownerUserId">User granted <see cref="ProjectRole.ProjectOwner"/>.</param>
    /// <param name="name">Project display name, rendered verbatim in the UI.</param>
    /// <param name="createdAt">
    /// Creation instant. Must differ between two projects in one tenant: the
    /// project list sorts by <c>CreatedAt</c> with no tiebreaker, and two rows
    /// sharing an instant come back in whatever order the query plan produces
    /// that day.
    /// </param>
    public static SyntheticProjectGraph BuildGraph(
        Guid tenantSeed,
        Guid tenantId,
        Guid ownerUserId,
        string name,
        DateTimeOffset createdAt) =>
        Assemble(
            tenantSeed,
            tenantId,
            FixtureIds.Derive(tenantSeed, AnchorProjectLabel),
            ownerUserId,
            name,
            createdAt);

    /// <summary>
    /// Builds an identical graph under a <em>different project id</em>, for
    /// processing-start tests.
    /// </summary>
    /// <remarks>
    /// A separate project, therefore separate ids: a start that shares the
    /// anchor project's project row is testing the previous spec's leftover,
    /// which is the failure mode that only shows up in a full-suite run (040B's
    /// "seam passes alone but fails in full suite"). It does <em>not</em> need
    /// to be run-less - see the type remarks on why a Completed anchor is
    /// invisible to a start.
    /// </remarks>
    /// <param name="tenantSeed">Seed for id derivation; the unit of isolation.</param>
    /// <param name="tenantId">Owning tenant.</param>
    /// <param name="ownerUserId">User granted <see cref="ProjectRole.ProjectOwner"/>.</param>
    /// <param name="name">Project display name.</param>
    /// <param name="createdAt">Creation instant; must differ from the anchor project's.</param>
    public static SyntheticProjectGraph BuildStartableGraph(
        Guid tenantSeed,
        Guid tenantId,
        Guid ownerUserId,
        string name,
        DateTimeOffset createdAt) =>
        Assemble(
            tenantSeed,
            tenantId,
            FixtureIds.Derive(tenantSeed, StartableProjectLabel),
            ownerUserId,
            name,
            createdAt);

    private static SyntheticProjectGraph Assemble(
        Guid tenantSeed,
        Guid tenantId,
        Guid projectId,
        Guid ownerUserId,
        string name,
        DateTimeOffset createdAt)
    {
        // Scoped by project id so the two graphs in one environment cannot share
        // a run primary key - the mistake the 040A seeder's own comments call
        // out when two labels produce the same GUID.
        var runId = FixtureIds.Derive(tenantSeed, "run/anchor", projectId);

        var project = new DubbingProject(
            projectId,
            tenantId,
            SyntheticProjects.SourceLanguage,
            SyntheticProjects.TargetLanguage,
            // MediaReady, not Created: `POST /processing` refuses a start on a
            // project whose media has not been ingested, so a fixture that seeds
            // `Created` fails every processing test with a 409/400 that says
            // nothing about the test.
            ProjectStatus.MediaReady,
            "{}",
            FixtureIds.DeriveHexHash(tenantSeed, $"project/{projectId:D}"),
            null,
            null,
            createdAt,
            createdAt,
            name,
            "Synthetic harness project.",
            ownerUserId,
            ownerUserId,
            ownerUserId,
            isArchived: false,
            archivedAt: null,
            settingsVersion: 1,
            processingSettingsJson: SyntheticProjects.ProcessingSettingsJson);

        var memberships = SyntheticUsers.SeededRoles
            .Select(role => new ProjectMembership(
                FixtureIds.Derive(tenantSeed, $"membership/{role}", projectId),
                tenantId,
                projectId,
                ownerUserId,
                ToProjectRole(role),
                ownerUserId,
                createdAt))
            .ToArray();

        // Source media. The content hash must differ per project: the
        // `ix_content_objects_tenant_id_content_hash` index is unique per tenant,
        // so two projects sharing one hash fail the second insert. Scoping the
        // derivation by project id is the fix (found by running it).
        var sourceContentId = FixtureIds.Derive(tenantSeed, "media-content", projectId);
        var sourceHash = FixtureIds.DeriveHexHash(tenantSeed, $"media/{projectId:D}");
        var sourceContent = new ContentObject(
            sourceContentId,
            tenantId,
            sourceHash,
            sourceHash,
            1024,
            "video/mp4",
            // Keyed per project: the storage key carries a uniqueness
            // constraint, so two projects sharing `{tenant}/seed/source.mp4`
            // fail the second insert.
            StorageKey(tenantId, projectId, "source.mp4"),
            ContentObjectStatus.Committed,
            FixtureClock.Now,
            FixtureClock.Now);

        var sourceMedia = new MediaAsset(
            FixtureIds.Derive(tenantSeed, "media-asset", projectId),
            tenantId,
            projectId,
            sourceContentId,
            "source.mp4",
            "mp4",
            "aac",
            "h264",
            1024,
            SyntheticProjects.SourceDurationMs,
            48000,
            2,
            "stereo",
            MediaAssetStatus.Valid,
            null,
            sourceHash,
            FixtureClock.Now);

        // Completed, never Pending or Running: a start is refused with
        // 409 RUN_ALREADY_ACTIVE only when an ACTIVE run exists, so a Completed
        // anchor is invisible to `POST /processing` while still giving the
        // segment subtree the non-empty RunId its validator requires.
        var run = new ProcessingRun(
            runId,
            tenantId,
            projectId,
            1,
            ProcessingRunStatus.Completed,
            "1.0.0",
            FixtureIds.DeriveHexHash(tenantSeed, $"run-config/{projectId:D}"),
            FixtureIds.DeriveHexHash(tenantSeed, $"run-route/{projectId:D}"),
            FixtureIds.DeriveHexHash(tenantSeed, $"run-snapshot/{projectId:D}"),
            FixtureClock.Now,
            FixtureClock.Now,
            FixtureClock.Now,
            FixtureClock.Now);

        var segmentId = FixtureIds.Derive(tenantSeed, "segment", projectId);
        var segment = new SpeechSegment(
            segmentId,
            tenantId,
            projectId,
            runId,
            1,
            0,
            SyntheticProjects.SourceDurationMs,
            "Ready",
            null,
            FixtureClock.Now);

        var transcriptVersion = new TranscriptVersion(
            FixtureIds.Derive(tenantSeed, "transcript-version", projectId),
            tenantId,
            projectId,
            runId,
            segmentId,
            SyntheticProjects.ProviderName,
            "mock-transcribe",
            SyntheticProjects.SourceLanguage,
            SyntheticProjects.TranscriptText,
            SyntheticProjects.ProviderConfidence,
            null,
            isSelected: true,
            needsReview: false,
            FixtureClock.Now);

        var translationVersion = new TranslationVersion(
            FixtureIds.Derive(tenantSeed, "translation-version", projectId),
            tenantId,
            projectId,
            runId,
            segmentId,
            SyntheticProjects.TranslationText,
            [],
            0.9,
            0.9,
            0.9,
            SyntheticProjects.ProviderName,
            "mock-translate",
            null,
            null,
            isSelected: true,
            FixtureClock.Now);

        var selection = new SegmentSelection(
            FixtureIds.Derive(tenantSeed, "segment-selection", projectId),
            tenantId,
            projectId,
            segmentId,
            transcriptVersion.Id,
            translationVersion.Id,
            null,
            SyntheticProjects.InitialSelectionVersion,
            FixtureClock.Now,
            ownerUserId);

        var speakerId = FixtureIds.Derive(tenantSeed, "speaker", projectId);
        var speaker = new Speaker(
            speakerId,
            tenantId,
            projectId,
            "spk-1",
            "Speaker 1",
            0,
            SyntheticProjects.SourceDurationMs,
            SyntheticProjects.ProviderName,
            "1",
            0.9,
            SyntheticProjects.ProviderName,
            FixtureClock.Now);

        var voices = new[]
        {
            new VoiceProfile(
                FixtureIds.Derive(tenantSeed, "voice/a", projectId),
                tenantId,
                SyntheticProjects.ProviderName,
                "mock-voice-a",
                "1",
                SyntheticProjects.TargetLanguage,
                VoiceType.Stock,
                false,
                null,
                FixtureClock.Now),
            new VoiceProfile(
                FixtureIds.Derive(tenantSeed, "voice/b", projectId),
                tenantId,
                SyntheticProjects.ProviderName,
                "mock-voice-b",
                "1",
                SyntheticProjects.TargetLanguage,
                VoiceType.Stock,
                false,
                null,
                FixtureClock.Now),
        };

        var voiceAssignment = new SpeakerVoiceAssignment(
            FixtureIds.Derive(tenantSeed, "voice-assignment", projectId),
            tenantId,
            projectId,
            runId,
            speakerId,
            voices[0].Id,
            "synthetic",
            FixtureIds.DeriveHexHash(tenantSeed, $"voice-policy/{projectId:D}"),
            FixtureClock.Now);

        var reviewItem = new ReviewItem(
            FixtureIds.Derive(tenantSeed, "review-item", projectId),
            tenantId,
            projectId,
            runId,
            ScopeType.Segment,
            // ScopeId must carry the segment id: a review row scoped to a
            // segment whose scope id is blank cannot be routed back to it, so
            // "approve this segment" has nothing to act on.
            segmentId.ToString("D"),
            segmentId,
            ReviewStatus.Open,
            "low-confidence",
            "{}",
            FixtureClock.Now,
            FixtureClock.Now,
            null);

        var notification = new Notification(
            FixtureIds.Derive(tenantSeed, "notification", projectId),
            tenantId,
            ownerUserId,
            projectId,
            NotificationType.ManualReviewRequired,
            NotificationSeverity.Warning,
            "Synthetic review required",
            "A synthetic segment needs review.",
            "project",
            projectId.ToString("D"),
            null,
            null,
            FixtureClock.Now,
            null);

        var exportContentId = FixtureIds.Derive(tenantSeed, "export-content", projectId);
        var exportHash = FixtureIds.DeriveHexHash(tenantSeed, $"export/{projectId:D}");
        var exportContent = new ContentObject(
            exportContentId,
            tenantId,
            exportHash,
            exportHash,
            SyntheticProjects.ExportFixtureBytes.Length,
            "application/json",
            StorageKey(tenantId, projectId, "export.json"),
            ContentObjectStatus.Committed,
            FixtureClock.Now,
            FixtureClock.Now);

        var exportArtifactId = FixtureIds.Derive(tenantSeed, "export-artifact", projectId);
        var exportArtifact = new Artifact(
            exportArtifactId,
            tenantId,
            projectId,
            runId,
            null,
            ArtifactType.Export,
            "1",
            exportContentId,
            null,
            null,
            null,
            null,
            ArtifactStatus.Committed,
            null,
            FixtureClock.Now);

        // `ArtifactIdRef` must carry the artifact id: the download path parses it
        // and raises `409 EXPORT_NOT_READY` when it is blank, so a "completed"
        // export with no reference is not actually downloadable.
        var exportJobId = FixtureIds.Derive(tenantSeed, "export-job", projectId);
        var exportJob = new ExportJob(
            exportJobId,
            tenantId,
            projectId,
            runId,
            ExportFormat.TranscriptJson,
            ExportJobStatus.Completed,
            exportArtifactId.ToString("D"),
            SyntheticProjects.ExportCompletenessJson,
            false,
            FixtureClock.Now,
            FixtureClock.Now);

        var exportLink = new ExportArtifact(
            FixtureIds.Derive(tenantSeed, "export-artifact-link", projectId),
            tenantId,
            exportJobId,
            exportArtifactId,
            FixtureClock.Now);

        return new SyntheticProjectGraph
        {
            TenantId = tenantId,
            Project = project,
            Memberships = memberships,
            SourceMedia = sourceMedia,
            SourceContent = sourceContent,
            Run = run,
            Segment = segment,
            TranscriptVersion = transcriptVersion,
            TranslationVersion = translationVersion,
            Selection = selection,
            Speaker = speaker,
            Voices = voices,
            VoiceAssignment = voiceAssignment,
            ReviewItem = reviewItem,
            Notification = notification,
            ExportJob = exportJob,
            ExportArtifactLink = exportLink,
            ExportArtifact = exportArtifact,
            ExportContent = exportContent,
        };
    }

    /// <summary>
    /// Maps a platform role name to the domain enum.
    /// </summary>
    /// <remarks>
    /// A switch with an explicit throw rather than
    /// <c>Enum.Parse(..., ignoreCase: true)</c>: the seeded role list and
    /// <c>ProjectRole</c> are two declarations that must agree, and a silent
    /// fallback to <c>ProjectViewer</c> would turn that disagreement into a spec
    /// that quietly asserts the wrong authorisation instead of a fixture that
    /// refuses to build.
    /// </remarks>
    /// <param name="role">One of <see cref="SyntheticUsers.SeededRoles"/>.</param>
    public static ProjectRole ToProjectRole(string role) => role switch
    {
        "ProjectOwner" => ProjectRole.ProjectOwner,
        "ProjectEditor" => ProjectRole.ProjectEditor,
        "Reviewer" => ProjectRole.Reviewer,
        "ProjectViewer" => ProjectRole.ProjectViewer,
        // TenantAdmin is a platform role with no ProjectRole counterpart: it is
        // granted by the tenant, not by a project. The admin is seeded as the
        // project owner here so that an admin's session carries a role the
        // project endpoints accept; a fixture that seeded only TenantAdmin would
        // be unable to read the project it administers.
        "TenantAdmin" => ProjectRole.ProjectOwner,
        _ => throw new ArgumentOutOfRangeException(
            nameof(role),
            role,
            "Unknown seeded role. Add it to SyntheticUsers.SeededRoles and give it a " +
            "ProjectRole mapping here, or the fixture refuses to build."),
    };

    /// <summary>
    /// Builds the storage key for a fixture object.
    /// </summary>
    /// <remarks>
    /// Tenant prefix first, then project, then the file name. The tenant prefix
    /// is what makes <c>e2e/support/reset.ts</c> able to wipe one tenant's objects
    /// without touching another's - the storage side of 046's tenant-isolation
    /// requirement. The project segment is required because the key carries a
    /// uniqueness constraint.
    /// </remarks>
    /// <param name="tenantId">Owning tenant.</param>
    /// <param name="projectId">Owning project.</param>
    /// <param name="fileName">Object file name.</param>
    internal static string StorageKey(Guid tenantId, Guid projectId, string fileName) =>
        string.Concat(tenantId.ToString("N"), "/harness/", projectId.ToString("N"), "/", fileName);
}