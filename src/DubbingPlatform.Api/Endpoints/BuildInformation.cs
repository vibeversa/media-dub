using System.Reflection;

namespace DubbingPlatform.Api.Endpoints;

/// <summary>
/// What this build is, read from assembly metadata rather than from
/// configuration (Task 043, instruction 3). A build that reports its own version
/// is the only version statement that cannot drift from the artefact: it is
/// stamped by the same compile that produced the code, so there is no second
/// place to forget to update.
///
/// Every value has a defined "unknown" form. A missing stamp must not become a
/// crash, and it must not silently become the *previous* release's stamp either
/// — the sentinel is the literal string <see cref="Unknown"/>, which is visibly
/// wrong rather than plausibly right.
/// </summary>
public static class BuildInformation
{
    /// <summary>The value used when a stamp was not supplied at build time.</summary>
    public const string Unknown = "unknown";

    /// <summary>Assembly metadata key holding the full git commit sha.</summary>
    public const string CommitMetadataKey = "BuildCommit";

    /// <summary>Assembly metadata key holding the release tag, e.g. <c>v1.4.2</c>.</summary>
    public const string ReleaseMetadataKey = "BuildRelease";

    /// <summary>Assembly metadata key holding the OpenAPI document version this build serves.</summary>
    public const string OpenApiVersionMetadataKey = "BuildOpenApiVersion";

    /// <summary>Assembly metadata key holding the UTC build timestamp (ISO-8601).</summary>
    public const string BuiltAtMetadataKey = "BuildTimestampUtc";

    private static readonly Lazy<BuildStamp> Cached = new(Read, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>The stamp for the running API assembly. Read once, then cached.</summary>
    public static BuildStamp Current => Cached.Value;

    private static BuildStamp Read()
    {
        var assembly = typeof(BuildInformation).Assembly;
        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        return new BuildStamp(
            Version: NormalizeVersion(informational),
            Commit: Normalize(ReadMetadata(assembly, CommitMetadataKey)),
            Release: Normalize(ReadMetadata(assembly, ReleaseMetadataKey)),
            OpenApiVersion: Normalize(ReadMetadata(assembly, OpenApiVersionMetadataKey)),
            BuiltAtUtc: Normalize(ReadMetadata(assembly, BuiltAtMetadataKey)));
    }

    private static string? ReadMetadata(Assembly assembly, string key)
    {
        foreach (var attribute in assembly.GetCustomAttributes<AssemblyMetadataAttribute>())
        {
            if (string.Equals(attribute.Key, key, StringComparison.Ordinal))
            {
                return attribute.Value;
            }
        }

        return null;
    }

    /// <summary>
    /// A release tag may be handed to MSBuild as <c>v1.4.2+abc1234</c>, and
    /// <c>AssemblyInformationalVersion</c> carries the source-revision suffix the
    /// SDK appends. Both are build noise for the purpose of "which version is
    /// this", so the version is the part before the first <c>+</c>, and an empty
    /// result becomes <see cref="Unknown"/>.
    /// </summary>
    public static string NormalizeVersion(string? informationalVersion)
    {
        if (string.IsNullOrWhiteSpace(informationalVersion))
        {
            return Unknown;
        }

        var plus = informationalVersion.IndexOf('+', StringComparison.Ordinal);
        var head = plus >= 0 ? informationalVersion[..plus] : informationalVersion;
        return Normalize(head);
    }

    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Unknown;
        }

        // A stamp is displayed in headers, dashboards and support tickets, so it
        // must never carry a newline or a quote: either would let a misconfigured
        // build argument forge a header value or break a log line.
        foreach (var character in value)
        {
            if (char.IsControl(character) || character is '"' or '\\' or '\r' or '\n')
            {
                return Unknown;
            }
        }

        return value;
    }
}

/// <summary>The immutable facts about one build. Pure data, no lookups.</summary>
/// <param name="Version">The informational version, without the source-revision suffix.</param>
/// <param name="Commit">Full git commit sha, or <see cref="BuildInformation.Unknown"/>.</param>
/// <param name="Release">Release tag, or <see cref="BuildInformation.Unknown"/>.</param>
/// <param name="OpenApiVersion">The <c>info.version</c> this build serves.</param>
/// <param name="BuiltAtUtc">UTC build timestamp, or <see cref="BuildInformation.Unknown"/>.</param>
public sealed record BuildStamp(
    string Version,
    string Commit,
    string Release,
    string OpenApiVersion,
    string BuiltAtUtc)
{
    /// <summary>A stamp for a local build that was not given any of the optional arguments.</summary>
    public static BuildStamp Unknown { get; } = new(
        BuildInformation.Unknown,
        BuildInformation.Unknown,
        BuildInformation.Unknown,
        BuildInformation.Unknown,
        BuildInformation.Unknown);

    /// <summary>
    /// The subset a support ticket needs in one line. Never includes the commit
    /// in full inside a user-visible string; the short sha is derived here so
    /// every caller formats it the same way.
    /// </summary>
    public string ShortCommit => Commit.Length > 7 ? Commit[..7] : Commit;

    /// <summary>Formats the stamp for a log line.</summary>
    public override string ToString() =>
        $"release={Release} version={Version} commit={ShortCommit} openapi={OpenApiVersion}";
}
