using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Domain.Enums;

namespace DubbingPlatform.TestFixtures;

/// <summary>
/// A fully wired synthetic project graph: the project row plus every row a
/// feature spec needs to act on, with all foreign keys resolved (Task 046 R3).
/// </summary>
/// <remarks>
/// <para>
/// Instances are produced only by <see cref="SyntheticProjects.BuildGraph"/> and
/// are immutable. Every id is derived from the tenant seed plus a stable label,
/// so two calls with the same seed produce byte-identical graphs - which is what
/// makes a visual baseline, a snapshot and a re-run of a failed test meaningful.
/// </para>
/// <para>
/// <see cref="AllRows"/> returns the rows in <b>foreign-key dependency order</b>.
/// That ordering is load-bearing rather than cosmetic: the graph is cyclic (the
/// project points at its active run and the run points back at the project), so
/// no strict topological order exists and EF Core resolves it only when both
/// halves are tracked in one context. A caller that adds rows to several
/// contexts in the wrong order persists part of the graph - the 040A seeder
/// learned that the hard way when <c>tenant_users</c> stayed empty and every
/// login returned <c>401 INVALID_CREDENTIALS</c> from a seeder that had already
/// reported success.
/// </para>
/// </remarks>
public sealed record SyntheticProjectGraph
{
    /// <summary>Owning tenant. Every row below carries this value.</summary>
    public required Guid TenantId { get; init; }

    /// <summary>The project. Its status is <see cref="ProjectStatus.MediaReady"/>.</summary>
    public required DubbingProject Project { get; init; }

    /// <summary>Memberships, one per seeded role. The owner's row is included.</summary>
    public required IReadOnlyList<ProjectMembership> Memberships { get; init; }

    /// <summary>Source media attached to the project.</summary>
    public required MediaAsset SourceMedia { get; init; }

    /// <summary>The content object <see cref="SourceMedia"/> points at.</summary>
    public required ContentObject SourceContent { get; init; }

    /// <summary>
    /// The anchor run: <see cref="ProcessingRunStatus.Completed"/>, which is what
    /// makes a start against this project succeed (only an <em>active</em> run
    /// blocks one) while still giving the segment subtree the non-empty
    /// <c>RunId</c> its validator requires.
    /// </summary>
    public required ProcessingRun Run { get; init; }

    /// <summary>One segment, so segment-scoped reads have something to return.</summary>
    public required SpeechSegment Segment { get; init; }

    /// <summary>The selected transcript version for <see cref="Segment"/>.</summary>
    public required TranscriptVersion TranscriptVersion { get; init; }

    /// <summary>The selected translation version for <see cref="Segment"/>.</summary>
    public required TranslationVersion TranslationVersion { get; init; }

    /// <summary>
    /// The selection pointer. A separate aggregate from the versions: without it
    /// the segment reads as having no selection at all and
    /// <c>SelectionVersion</c> is 0, so a stale-edit test would have no version
    /// to make stale.
    /// </summary>
    public required SegmentSelection Selection { get; init; }

    /// <summary>One speaker, so the voice-assignment seam has a target.</summary>
    public required Speaker Speaker { get; init; }

    /// <summary>Two selectable voices, so a reassignment has somewhere to go.</summary>
    public required IReadOnlyList<VoiceProfile> Voices { get; init; }

    /// <summary>The speaker-to-voice assignment the fixture starts from.</summary>
    public required SpeakerVoiceAssignment VoiceAssignment { get; init; }

    /// <summary>One open review item, so the review seam has a real decision.</summary>
    public required ReviewItem ReviewItem { get; init; }

    /// <summary>One unread notification, so read state is an observable mutation.</summary>
    public required Notification Notification { get; init; }

    /// <summary>One completed export job, so the download seam has bytes to fetch.</summary>
    public required ExportJob ExportJob { get; init; }

    /// <summary>The link row from <see cref="ExportJob"/> to <see cref="ExportArtifact"/>.</summary>
    public required ExportArtifact ExportArtifactLink { get; init; }

    /// <summary>The export artifact row.</summary>
    public required Artifact ExportArtifact { get; init; }

    /// <summary>The content object backing <see cref="ExportArtifact"/>.</summary>
    public required ContentObject ExportContent { get; init; }

    /// <summary>The project's identifier.</summary>
    public Guid ProjectId => Project.Id;

    /// <summary>The user granted <see cref="ProjectRole.ProjectOwner"/>.</summary>
    public Guid OwnerUserId => Project.OwnerUserId
        ?? throw new InvalidOperationException(
            "The synthetic project has no owner. Build it through SyntheticProjects, " +
            "which always grants ProjectOwner.");

    /// <summary>
    /// Every row in the graph, in foreign-key dependency order.
    /// </summary>
    /// <remarks>
    /// Content objects and the project come first because nothing points at them;
    /// then the run, media and memberships; then the segment and its versions;
    /// then everything that points at a segment or a run. The export trio is last
    /// because <see cref="ExportArtifactLink"/> references both the job and the
    /// artifact.
    /// </remarks>
    public IReadOnlyList<object> AllRows() =>
    [
        Project,
        SourceContent,
        SourceMedia,
        .. Memberships,
        Run,
        Segment,
        TranscriptVersion,
        TranslationVersion,
        Selection,
        Speaker,
        .. Voices,
        VoiceAssignment,
        ReviewItem,
        Notification,
        ExportContent,
        ExportArtifact,
        ExportArtifactLink,
        ExportJob,
    ];
}