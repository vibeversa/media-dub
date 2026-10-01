using System.Globalization;

using DubbingPlatform.Domain.Entities;

namespace DubbingPlatform.TestFixtures;

/// <summary>
/// The frozen clock every synthetic fixture is stamped with (Task 046 R3).
/// </summary>
/// <remarks>
/// <para>
/// Deterministic fixtures require deterministic timestamps, so every date in
/// this library comes from one captured instant rather than the wall clock. It
/// is also deliberately NOT "now": a fixture whose timestamps move makes any
/// snapshot, any visual baseline and any ordering assertion fail for a reason
/// that has nothing to do with the code under test.
/// </para>
/// <para>
/// 2026-01-15T12:00:00Z is arbitrary but fixed. It is a Thursday, at midday UTC,
/// which keeps derived weekday/month formatting away from the boundary cases
/// (a 23:59 or a 31st would make a locale-formatting assertion depend on the
/// day the suite runs).
/// </para>
/// </remarks>
public static class FixtureClock
{
    /// <summary>The frozen instant every fixture is created at.</summary>
    public static DateTimeOffset Now { get; } =
        new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// An instant exactly one hour <em>before</em> <see cref="Now"/>.
    /// </summary>
    /// <remarks>
    /// Exists because <c>DubbingProject</c> rows sort by
    /// <c>OrderByDescending(CreatedAt)</c> with no tiebreaker. Two seeded
    /// projects sharing an instant come back in whatever order the query plan
    /// happens to produce that day, which 041B measured as one project-table row
    /// moving between runs and ten of twelve <c>projects</c> visual cells failing
    /// verification while baseline *generation* stayed perfectly deterministic. A
    /// tie in a sort key is a data defect, not a rendering one.
    /// </remarks>
    public static DateTimeOffset OneHourEarlier { get; } = Now.AddHours(-1);

    /// <summary>A distinct, later instant, for rows ordered after <see cref="Now"/>.</summary>
    public static DateTimeOffset OneHourLater { get; } = Now.AddHours(1);

    /// <summary>Formats an instant the way the wire and the logs render it (ISO-8601, UTC).</summary>
    /// <param name="instant">The instant to format.</param>
    public static string Format(DateTimeOffset instant) =>
        instant.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}

/// <summary>
/// Builds synthetic tenants (Task 046 instruction 3, R3).
/// </summary>
/// <remarks>
/// <para>
/// One tenant is the unit of isolation: every fixture in this library carries a
/// <c>TenantId</c>, and <see cref="SyntheticProjectGraph"/> never mixes two.
/// That is what lets two Playwright workers build graphs concurrently without
/// coordinating - 046's edge case "Parallel workers share tenant" is prevented by
/// construction rather than by a lock.
/// </para>
/// <para>
/// Scaffolding only: the tenant is a row, never a deployment. The fixture
/// creates no bucket, sends no email and grants no access outside the database.
/// </para>
/// </remarks>
public static class SyntheticTenants
{
    /// <summary>
    /// The reserved top-level domain (RFC 2606) every synthetic email uses.
    /// </summary>
    /// <remarks>
    /// <c>.invalid</c> is guaranteed never to resolve, so a seeded address can
    /// never reach a real mailbox even if a future fixture sends mail. A
    /// plausible-looking domain such as <c>example.com</c> is reserved too but
    /// is owned by a real party; <c>.invalid</c> has no owner at all.
    /// </remarks>
    public const string ReservedEmailDomain = "fixtures.invalid";

    /// <summary>
    /// The email local part used by every synthetic user. Combined with
    /// <see cref="ReservedEmailDomain"/> it makes an accidental real address
    /// structurally impossible.
    /// </summary>
    public const string ReservedEmailLocalPart = "synthetic";

    /// <summary>The role a tenant's administrator user is granted.</summary>
    public const string TenantAdminRole = "TenantAdmin";

    /// <summary>
    /// Builds the canonical single-tenant graph: one tenant plus its
    /// administrator.
    /// </summary>
    /// <param name="tenantSeed">
    /// Seed for id derivation. Two workers pass different seeds and get
    /// disjoint tenants.
    /// </param>
    /// <param name="slug">
    /// Tenant slug. Defaults to a deterministic slug derived from the seed. Must
    /// be URL-safe: it is what <c>POST /auth/login</c> and the tenant switcher
    /// key on.
    /// </param>
    public static SyntheticTenant Build(Guid tenantSeed, string? slug = null)
    {
        var id = FixtureIds.Derive(tenantSeed, "tenant");
        var resolvedSlug = slug ?? DefaultSlug(tenantSeed);

        // `harness-` keeps the slug recognisable in logs and in the tenant
        // switcher, which matters when someone is reading a failed run's output
        // and needs to know the row is synthetic rather than a developer's own
        // tenant left over from an earlier experiment.
        var tenant = new Tenant(
            id,
            string.Create(CultureInfo.InvariantCulture, $"Harness tenant {id:N}"),
            resolvedSlug,
            FixtureClock.Now);

        return new SyntheticTenant(tenant, new[] { SyntheticUsers.BuildAdmin(tenantSeed, id) });
    }

    /// <summary>
    /// The slug used when a caller does not supply one. Derived from the seed so
    /// two workers never collide on the unique slug index.
    /// </summary>
    /// <param name="tenantSeed">Seed for the derivation.</param>
    public static string DefaultSlug(Guid tenantSeed)
    {
        // Truncated to 24 characters: a slug is a display string, a URL path
        // segment and a column with a length limit, and the full 32-hex form is
        // both longer than any of those want and no more identifying. The prefix
        // is the seed, so two workers still get different slugs.
        var slug = string.Create(CultureInfo.InvariantCulture, $"harness-{tenantSeed:N}");
        return slug[..Math.Min(slug.Length, 24)];
    }
}

/// <summary>
/// A synthetic tenant and the users it owns.
/// </summary>
/// <param name="Tenant">The tenant row.</param>
/// <param name="Users">Every user in the tenant, including the administrator.</param>
public sealed record SyntheticTenant(Tenant Tenant, IReadOnlyList<TenantUser> Users)
{
    /// <summary>The tenant's identifier.</summary>
    public Guid TenantId => Tenant.Id;

    /// <summary>The administrator user, which every role must be able to audit.</summary>
    public TenantUser Admin =>
        Users.Single(user => user.ExternalSubject == SyntheticUsers.AdminSubject);
}