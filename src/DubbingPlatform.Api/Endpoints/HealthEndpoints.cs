using DubbingPlatform.Api.Auth;
using DubbingPlatform.Infrastructure.Health;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace DubbingPlatform.Api.Endpoints;

/// <summary>One readiness dependency's verdict, in a shape a monitor can read.</summary>
/// <param name="Name">Registered health-check name.</param>
/// <param name="Status">Healthy / Degraded / Unhealthy.</param>
/// <param name="Description">The check's own message. Never contains a connection string.</param>
/// <param name="DurationMs">How long the check took.</param>
public sealed record HealthCheckEntry(string Name, string Status, string Description, double DurationMs);

/// <summary>The <c>GET /version</c> body. Field names are the contract with the CDN's <c>/version.json</c>.</summary>
/// <param name="Version">Build version, matching <c>version.json</c>.</param>
/// <param name="Release">Release tag, matching <c>version.json</c>.</param>
/// <param name="Commit">Full commit sha, matching <c>version.json</c>.</param>
/// <param name="OpenApiVersion">The <c>info.version</c> this build serves.</param>
/// <param name="BuiltAtUtc">UTC build timestamp.</param>
/// <param name="MigrationHead">Newest migration applied in the database, or <c>none</c>.</param>
/// <param name="MigrationTarget">Newest migration this build defines.</param>
/// <param name="MigrationsPending">How many defined migrations the database has not applied.</param>
public sealed record VersionResponse(
    string Version,
    string Release,
    string Commit,
    string OpenApiVersion,
    string BuiltAtUtc,
    string MigrationHead,
    string MigrationTarget,
    int MigrationsPending);

/// <summary>The <c>GET /health</c> body.</summary>
/// <param name="Status">Overall verdict: <c>Healthy</c>, <c>Degraded</c> or <c>Unhealthy</c>.</param>
/// <param name="Liveness">Whether the process is up. Never depends on a dependency.</param>
/// <param name="Readiness">Whether the process may take traffic.</param>
/// <param name="Version">Build version, so a failing probe names the build that failed.</param>
/// <param name="Checks">Per-dependency detail. Empty for a healthy liveness-only answer.</param>
public sealed record HealthResponse(
    string Status,
    string Liveness,
    string Readiness,
    string Version,
    IReadOnlyList<HealthCheckEntry> Checks);

/// <summary>
/// <c>GET /health</c> and <c>GET /version</c> (Task 043, instruction 3).
///
/// The three endpoints are deliberately different questions and it matters which
/// is which, because getting it wrong is a production incident either way:
///
/// - <c>/health/live</c> — "is the process running". Process-local only. A
///   dependency failure must NOT fail this, or a database blip restarts every
///   pod at once and turns a degradation into an outage.
/// - <c>/health/ready</c> — "may this pod take traffic". Fails on PostgreSQL,
///   storage, the broker, Redis, and <b>migration currency</b>. Failing it
///   removes the pod from the Service endpoints without killing it, which is why
///   a rollout that is genuinely not ready never serves traffic.
/// - <c>/health</c> — both, in one JSON body, for humans and for the hosting
///   gate's header assertions. Its readiness half is the same set as
///   <c>/health/ready</c>, so the two cannot disagree.
///
/// <c>/version</c> exists because "which build is answering?" is the first
/// question in every deploy incident, and answering it by opening a shell in a
/// pod is not an answer. It is anonymous (see <see cref="AnonymousRoutes"/>)
/// because a probe or a CDN cannot hold a token, and it exposes no secret: the
/// build stamp and the migration ids only.
/// </summary>
public static class HealthEndpoints
{
    /// <summary>Route for the combined liveness+readiness report.</summary>
    public const string HealthPath = "/health";

    /// <summary>Route for the build/version report.</summary>
    public const string VersionPath = "/version";

    /// <summary>Maps <c>GET /health</c> and <c>GET /version</c>.</summary>
    public static IEndpointRouteBuilder MapHostingEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var stamp = BuildInformation.Current;

        endpoints.MapGet(HealthPath, (HealthCheckService health) => Report(health, stamp))
            .AllowAnonymous()
            .WithName("HostingHealth")
            .WithTags("Hosting")
            // Never cached. A cached health answer is a health answer about a
            // process that is no longer running, and the thing being cached here
            // is exactly the thing whose staleness matters.
            .WithMetadata(new ResponseCacheAttribute { NoStore = true });

        endpoints.MapGet(VersionPath, (IMigrationStateReader migrations) => Version(migrations, stamp))
            .AllowAnonymous()
            .WithName("HostingVersion")
            .WithTags("Hosting")
            .WithMetadata(new ResponseCacheAttribute { NoStore = true });

        return endpoints;
    }

    /// <summary>Writes the combined report. 200 when serving, 503 when not.</summary>
    public static async Task<IResult> Report(HealthCheckService health, BuildStamp stamp, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(health);

        var live = await health.CheckHealthAsync(
            (registration) => registration.Tags.Contains("live", StringComparer.Ordinal),
            cancellationToken).ConfigureAwait(false);
        var ready = await health.CheckHealthAsync(
            (registration) => registration.Tags.Contains("ready", StringComparer.Ordinal),
            cancellationToken).ConfigureAwait(false);

        var body = new HealthResponse(
            Status: OverallStatus(live.Status, ready.Status),
            Liveness: live.Status.ToString(),
            Readiness: ready.Status.ToString(),
            Version: stamp.Version,
            Checks: ready.Entries
                .OrderBy(entry => entry.Key, StringComparer.Ordinal)
                .Select(entry => new HealthCheckEntry(
                    entry.Key,
                    entry.Value.Status.ToString(),
                    entry.Value.Description ?? string.Empty,
                    Math.Round(entry.Value.Duration.TotalMilliseconds, 1)))
                .ToArray());

        return Results.Json(body, statusCode: body.Readiness == nameof(HealthStatus.Healthy) ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable);
    }

    /// <summary>Writes the version report. 200 even when migrations are pending.</summary>
    public static async Task<IResult> Version(IMigrationStateReader migrations, BuildStamp stamp, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(migrations);

        // A version answer that fails because the database is unreachable is
        // worse than useless during an incident: it removes the one endpoint
        // that would have said which build is running. Report `unknown` instead.
        // The authoritative migration-currency signal is `/health`, which is
        // allowed to fail - and does.
        MigrationState? state = null;
        try
        {
            state = await migrations.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            state = null;
        }

        var body = new VersionResponse(
            Version: stamp.Version,
            Release: stamp.Release,
            Commit: stamp.Commit,
            OpenApiVersion: stamp.OpenApiVersion,
            BuiltAtUtc: stamp.BuiltAtUtc,
            MigrationHead: state?.AppliedHead ?? BuildInformation.Unknown,
            MigrationTarget: state?.DefinedHead ?? BuildInformation.Unknown,
            MigrationsPending: state?.Pending.Count ?? -1);

        return Results.Json(body);
    }

    /// <summary>
    /// The combined status: the worse of liveness and readiness, with
    /// <c>Unhealthy</c> for either one. <c>Degraded</c> is only reported when
    /// BOTH halves are degraded-but-serving, so a monitor that pages on
    /// <c>Unhealthy</c> pages exactly when a pod has stopped serving and not
    /// when one dependency has merely slowed down. Pure, and pinned by tests.
    /// </summary>
    public static string OverallStatus(HealthStatus liveness, HealthStatus readiness)
    {
        if (liveness == HealthStatus.Healthy && readiness == HealthStatus.Healthy)
        {
            return nameof(HealthStatus.Healthy);
        }

        // `HealthStatus` is declared Unhealthy=0, Degraded=1, Healthy=2, so the
        // WORST status is the LOWEST numeric value. Comparing with `>` here
        // would pick the healthiest of the two, which reports a pod that cannot
        // reach its database as Degraded-and-therefore-tolerable. The severity
        // order is spelled out rather than inferred from the enum for that
        // reason: it is the one comparison in this file whose direction is not
        // obvious, and a wrong guess is a paging threshold.
        if (liveness == HealthStatus.Unhealthy || readiness == HealthStatus.Unhealthy)
        {
            return nameof(HealthStatus.Unhealthy);
        }

        return nameof(HealthStatus.Degraded);
    }

    /// <summary>
    /// Compares this build's <c>info.version</c> with the one the API actually
    /// serves. A mismatch means the committed bundle, the generated client and
    /// the running server disagree, which is a contract-provenance failure rather
    /// than a deploy failure. Pure, and used by the hosting gate.
    /// </summary>
    public static bool OpenApiVersionAgrees(string? servedDocumentVersion, string? buildStampVersion)
    {
        if (string.IsNullOrWhiteSpace(servedDocumentVersion) || string.IsNullOrWhiteSpace(buildStampVersion))
        {
            return false;
        }

        return string.Equals(servedDocumentVersion.Trim(), buildStampVersion.Trim(), StringComparison.Ordinal);
    }

    /// <summary>Formats the migration section of the version report for logs.</summary>
    public static string DescribeMigrationState(MigrationState? state) => state is null
        ? "migration state unreadable"
        : state.Describe();
}
