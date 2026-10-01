using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace DubbingPlatform.TestFixtures;

/// <summary>
/// Deterministic identity derivation for every synthetic fixture (Task 046 R3).
/// </summary>
/// <remarks>
/// <para>
/// Every id in this library is derived from a <em>stable label</em> through
/// SHA-256 rather than from <see cref="Guid.NewGuid"/>. Three things depend on
/// that, and all three were learned the hard way by 041B:
/// </para>
/// <list type="bullet">
/// <item>
/// <description>
/// <b>Reproducibility.</b> A rig failure found on one run cannot be re-examined
/// on the next if every row moved. With derived ids the same seed produces the
/// same graph every time.
/// </description>
/// </item>
/// <item>
/// <description>
/// <b>Visual baselines.</b> Screens render entity ids as visible text (the
/// exports screen shows <c>exp_&lt;32 hex&gt;</c>). With random ids those
/// baselines could never match and the visual matrix was unrunnable.
/// </description>
/// </item>
/// <item>
/// <description>
/// <b>Per-worker isolation.</b> Task 046's edge cases require "factories issue
/// isolated tenant per worker (no cross-test leakage)". Deriving from a
/// caller-supplied tenant id is what makes that possible: two workers pass
/// different tenant seeds and therefore never collide, without any coordination.
/// </description>
/// </item>
/// </list>
/// <para>
/// The derivation is a pure function of <c>(tenantId, scope, label)</c>, so
/// namespacing by tenant and scope is what keeps two tenants (and two projects
/// inside one tenant) from producing the same primary key.
/// </para>
/// </remarks>
public static class FixtureIds
{
    /// <summary>
    /// Derives a stable GUID from a tenant seed and a label.
    /// </summary>
    /// <param name="tenantSeed">Tenant identifier, or an arbitrary worker seed.</param>
    /// <param name="scope">
    /// Optional namespace (typically a project id). Two fixtures that share a
    /// label but not a scope MUST NOT share an id - they would share a primary key.
    /// </param>
    /// <param name="label">Stable, human-readable fixture name.</param>
    public static Guid Derive(Guid tenantSeed, string label, Guid? scope = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);

        var builder = new StringBuilder(96);
        builder.Append(tenantSeed.ToString("D", CultureInfo.InvariantCulture));
        builder.Append(':');
        builder.Append(scope?.ToString("D", CultureInfo.InvariantCulture) ?? "-");
        builder.Append(':');
        builder.Append(label);

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return new Guid(digest.AsSpan(0, 16));
    }

    /// <summary>
    /// Derives a stable 64-character lowercase hex hash, for the columns that
    /// require a content hash shape (<c>ContentObject.ContentHash</c>,
    /// <c>DubbingProject.ConfigurationHash</c>, and the run hash triple).
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="Derive"/> on purpose: the first 16 bytes of a
    /// digest are not uniformly distributed across the string space, and a value
    /// that must *look* like a SHA-256 should be produced by hashing something
    /// rather than by truncating a different kind of derivation.
    /// </remarks>
    /// <param name="tenantSeed">Tenant identifier, or an arbitrary worker seed.</param>
    /// <param name="label">Stable, human-readable fixture name.</param>
    public static string DeriveHexHash(Guid tenantSeed, string label)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);

        var material = string.Concat(
            tenantSeed.ToString("D", CultureInfo.InvariantCulture),
            ":",
            label);

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)))
            .ToLowerInvariant();
    }

    /// <summary>
    /// A zero GUID, for optional foreign keys a fixture deliberately leaves
    /// empty. A named constant rather than <c>default</c> so the intent at the
    /// call site is readable.
    /// </summary>
    public static Guid None => Guid.Empty;
}