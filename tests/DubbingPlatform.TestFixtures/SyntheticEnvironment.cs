using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Domain.Enums;

namespace DubbingPlatform.TestFixtures;

/// <summary>
/// The whole synthetic environment for one worker: a tenant, its users, and its
/// projects (Task 046 R3, "tenant-isolated graphs usable by backend + E2E
/// tests").
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the type a test should reach for.</b> The individual builders
/// exist so a caller can assemble something unusual, but the common case - "give
/// me a working tenant with a project, a run and a segment" - is
/// <see cref="BuildForWorker"/>, and using it is what makes two tests written by
/// different people comparable.
/// </para>
/// <para>
/// <b>Why the tenant seed is the only knob.</b> Task 046's edge case is
/// "Parallel workers share tenant -&gt; factories issue isolated tenant per worker
/// (no cross-test leakage)". Passing a per-worker seed is the whole mechanism:
/// every id in the environment derives from it, so two workers cannot collide on
/// a primary key, a storage key, a slug or an email - and neither needs to know
/// the other exists.
/// </para>
/// </remarks>
public sealed record SyntheticEnvironment
{
    /// <summary>The tenant and its users.</summary>
    public required SyntheticTenant Tenant { get; init; }

    /// <summary>
    /// The anchor project: completed run, one segment, full review/voice/export
    /// set. Everything that mutates state uses this one.
    /// </summary>
    public required SyntheticProjectGraph AnchorProject { get; init; }

    /// <summary>
    /// A second project with a free run slot, for processing-start tests.
    /// </summary>
    /// <remarks>
    /// A separate project, therefore separate ids. A start on
    /// <see cref="AnchorProject"/> is a test of whatever the previous spec left
    /// there; a start here is not. It carries the same Completed anchor run as the
    /// anchor project because <c>POST /processing</c> only refuses a start when
    /// an ACTIVE run exists - and because
    /// <c>SpeechSegment.Validate</c> requires a non-empty <c>RunId</c>, so a
    /// genuinely run-less project cannot carry the segment subtree at all.
    /// </remarks>
    public required SyntheticProjectGraph StartableProject { get; init; }

    /// <summary>Every user in the tenant, indexed by external subject.</summary>
    public IReadOnlyDictionary<string, TenantUser> UsersBySubject =>
        Tenant.Users.ToDictionary(user => user.ExternalSubject, StringComparer.Ordinal);

    /// <summary>Every row of every project, in per-project dependency order.</summary>
    public IReadOnlyList<object> AllProjectRows() =>
        [.. AnchorProject.AllRows(), .. StartableProject.AllRows()];
}

/// <summary>
/// Builds <see cref="SyntheticEnvironment"/> values.
/// </summary>
public static class SyntheticEnvironments
{
    /// <summary>
    /// Builds the standard environment for one worker: a tenant with an admin,
    /// an owner/editor/reviewer/viewer set, an anchor project and a run-less
    /// pipeline project.
    /// </summary>
    /// <remarks>
    /// The project names are constants rather than per-run strings for the same
    /// reason the ids are derived: the visual matrix screenshots the project list,
    /// and a name that changed every run would make the baseline unusable. A test
    /// that needs a second project of its own derives one from the same seed with
    /// a distinct label.
    /// </remarks>
    /// <param name="workerSeed">
    /// Per-worker seed. Two workers MUST pass different values; see the type
    /// remarks.
    /// </param>
    public static SyntheticEnvironment BuildForWorker(Guid workerSeed)
    {
        var tenant = SyntheticTenants.Build(workerSeed);

        var users = new List<TenantUser>
        {
            tenant.Admin,
            SyntheticUsers.BuildOwner(workerSeed, tenant.TenantId),
            SyntheticUsers.BuildEditor(workerSeed, tenant.TenantId),
            SyntheticUsers.BuildReviewer(workerSeed, tenant.TenantId),
            SyntheticUsers.BuildViewer(workerSeed, tenant.TenantId),
        };

        var scopedTenant = tenant with { Users = users };
        var owner = users.Single(user => user.ExternalSubject == SyntheticUsers.OwnerSubject);

        // Two projects, two different instants: the project list orders by
        // `CreatedAt` descending with no tiebreaker, so a tie makes the rendered
        // order depend on the query plan. Fixed as data, not worked around in CSS.
        var anchor = SyntheticProjects.BuildGraph(
            workerSeed,
            tenant.TenantId,
            owner.Id,
            "Harness Anchor",
            FixtureClock.Now);

        var startable = SyntheticProjects.BuildStartableGraph(
            workerSeed,
            tenant.TenantId,
            owner.Id,
            "Harness Startable",
            FixtureClock.OneHourEarlier);

        return new SyntheticEnvironment
        {
            Tenant = scopedTenant,
            AnchorProject = anchor,
            StartableProject = startable,
        };
    }

    /// <summary>
    /// Grants <paramref name="role"/> on <paramref name="project"/> to
    /// <paramref name="user"/>.
    /// </summary>
    /// <remarks>
    /// A helper rather than a row the caller constructs, because the membership
    /// id must be derived from (tenant, project, role) and a caller who picks a
    /// fresh <c>Guid.NewGuid()</c> every time creates a duplicate membership on
    /// re-seed - which then shows up as a user holding two roles and a permission
    /// check that passes for the wrong reason.
    /// </remarks>
    /// <param name="graph">The project to grant on.</param>
    /// <param name="userId">The user being granted the role.</param>
    /// <param name="role">The role to grant.</param>
    /// <param name="grantedBy">The user recorded as having granted it.</param>
    public static ProjectMembership Membership(
        SyntheticProjectGraph graph,
        Guid userId,
        ProjectRole role,
        Guid grantedBy) =>
        new(
            FixtureIds.Derive(graph.TenantId, $"membership/{userId:D}/{role}", graph.ProjectId),
            graph.TenantId,
            graph.ProjectId,
            userId,
            role,
            grantedBy,
            FixtureClock.Now);
}