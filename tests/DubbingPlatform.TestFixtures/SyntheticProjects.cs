using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Domain.Enums;

namespace DubbingPlatform.TestFixtures;

/// <summary>
/// Constants describing the synthetic project graph, and the entry points that
/// build it (Task 046 instruction 3 - "incl. runs/segments/review items" - and
/// R3 "tenant-isolated graphs").
/// </summary>
/// <remarks>
/// <para>
/// The graph is assembled by <see cref="SyntheticProjectGraphBuilder"/>. This
/// type owns the <em>vocabulary</em>: statuses, languages, provider names,
/// texts and JSON blobs. Those values are shared by the backend fixtures, the
/// E2E seeder and the frontend's MSW handlers, and three hand-copied copies of
/// <c>"reviewThreshold": 0.8</c> is exactly the drift that makes a seam fail for
/// a reason nobody can find.
/// </para>
/// <para>
/// <b>Why the anchor run is Completed.</b> A pending or running run makes
/// <c>POST /processing</c> answer <c>409 RUN_ALREADY_ACTIVE</c>, so a fixture
/// that seeds an in-flight run cannot accept a start and every processing test
/// built on it fails at the API boundary rather than at its assertion. A
/// completed run is also what the export path prefers when resolving a job. A
/// project with <em>no</em> run at all is what a start test wants, which is why
/// <see cref="BuildWithoutRunGraph"/> exists on a separate project.
/// </para>
/// <para>
/// <b>Why selection version starts at 1.</b> <c>SegmentSelection.SelectionVersion</c>
/// starts at 0 and the first successful edit bumps it to 1, so a test that wants
/// to prove a stale writer gets <c>409 SELECTION_CONFLICT</c> needs a version it
/// can hold onto <em>before</em> its first edit. Starting at 1 means the first
/// edit yields 2 and the stale writer still holds 1.
/// </para>
/// </remarks>
public static class SyntheticProjects
{
    /// <summary>Builds the wired graph; see <see cref="SyntheticProjectGraphBuilder.BuildGraph"/>.</summary>
    /// <param name="tenantId">Owning tenant.</param>
    /// <param name="ownerUserId">User granted <see cref="ProjectRole.ProjectOwner"/>.</param>
    /// <param name="name">Project display name.</param>
    /// <param name="createdAt">Creation instant; must differ between two projects in one tenant.</param>
    public static SyntheticProjectGraph BuildGraph(
        Guid tenantSeed,
        Guid tenantId,
        Guid ownerUserId,
        string name,
        DateTimeOffset createdAt) =>
        SyntheticProjectGraphBuilder.BuildGraph(tenantSeed, tenantId, ownerUserId, name, createdAt);

    /// <summary>Builds a run-free project id; see <see cref="SyntheticProjectGraphBuilder.BuildStartableGraph"/>.</summary>
    /// <param name="tenantSeed">Seed for id derivation; the unit of isolation.</param>
    /// <param name="tenantId">Owning tenant.</param>
    /// <param name="ownerUserId">User granted <see cref="ProjectRole.ProjectOwner"/>.</param>
    /// <param name="name">Project display name.</param>
    /// <param name="createdAt">Creation instant.</param>
    public static SyntheticProjectGraph BuildStartableGraph(
        Guid tenantSeed,
        Guid tenantId,
        Guid ownerUserId,
        string name,
        DateTimeOffset createdAt) =>
        SyntheticProjectGraphBuilder.BuildStartableGraph(tenantSeed, tenantId, ownerUserId, name, createdAt);

    /// <summary>
    /// The initial <c>SelectionVersion</c>. See the remarks: it is 1, not 0, so a
    /// stale-write test has something to be stale against.
    /// </summary>
    public const int InitialSelectionVersion = 1;

    /// <summary>
    /// A <c>processingSettingsJson</c> blob the API's validator accepts.
    /// </summary>
    /// <remarks>
    /// A seeded project with a null or blank settings blob is rejected at the API
    /// boundary, so a rig that omits it fails every processing-start test with a
    /// 400 naming a JSON parse error rather than the missing seed data. The shape
    /// mirrors <c>ProjectProcessingSettingsValidator</c>: object root, numeric
    /// <c>schemaVersion</c> equal to the supported version, four optional
    /// strings of at most 64 chars, a <c>reviewThreshold</c> in [0, 1], and an
    /// optional glossary array.
    /// </remarks>
    public const string ProcessingSettingsJson =
        """
        {
          "schemaVersion": 1,
          "sourceSeparationPolicy": "auto",
          "outputProfile": "standard",
          "timingStrictness": "balanced",
          "voicePolicy": "preserve",
          "reviewThreshold": 0.8,
          "glossary": [],
          "styleInstructions": "Synthetic harness defaults."
        }
        """;

    /// <summary>Source language of every synthetic project.</summary>
    public const string SourceLanguage = "en";

    /// <summary>Target language of every synthetic project.</summary>
    public const string TargetLanguage = "es";

    /// <summary>
    /// Synthetic transcript text. Matches the deterministic shape the mock
    /// providers produce, so a fixture and a provider output are not two
    /// different vocabularies - which matters because a test that asserts on the
    /// text then fails identically whether the provider or the fixture is wrong.
    /// </summary>
    public const string TranscriptText = "synthetic transcript segment";

    /// <summary>Synthetic translation text, same reasoning as <see cref="TranscriptText"/>.</summary>
    public const string TranslationText = "synthetic translation segment";

    /// <summary>Confidence attached to synthetic provider output.</summary>
    public const double ProviderConfidence = 0.95;

    /// <summary>
    /// The provider name on synthetic versions. The rig runs mock providers only;
    /// a fixture naming a real one would let a test accidentally assert that a
    /// real provider was called.
    /// </summary>
    public const string ProviderName = "mock";

    /// <summary>Duration of the synthetic source media, in milliseconds.</summary>
    public const int SourceDurationMs = 2000;

    /// <summary>
    /// The exact bytes the export seam writes to object storage and reads back
    /// through a signed URL.
    /// </summary>
    /// <remarks>
    /// A byte count plus a matching content hash, kept together in one place so
    /// the fixture's <c>ContentObject.SizeBytes</c> and the bytes a spec uploads
    /// cannot drift. The hash is <em>not</em> derived from these bytes: it is
    /// derived from the project id, because the hash column is unique per tenant
    /// and two projects sharing real bytes would fail the second insert.
    /// </remarks>
    public static ReadOnlyMemory<byte> ExportFixtureBytes { get; } =
        System.Text.Encoding.UTF8.GetBytes(
            "{\"schemaVersion\":1,\"format\":\"transcript-json\",\"segments\":[{\"sequence\":1,\"text\":\"" +
            TranscriptText + "\"}]}\n");

    /// <summary>
    /// The completeness document a completed export carries.
    /// </summary>
    /// <remarks>
    /// <c>complete: true</c> is the point: a partial export is downloadable too,
    /// and a fixture that seeded <c>false</c> would let a download spec pass
    /// against an export the UI renders as incomplete.
    /// </remarks>
    public const string ExportCompletenessJson =
        "{\"complete\":true,\"missingStages\":[],\"segmentCount\":1,\"includedArtifacts\":[\"transcript\"]}";
}
