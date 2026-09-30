using DubbingPlatform.Api.Auth;
using DubbingPlatform.Api.Endpoints;
using DubbingPlatform.Infrastructure.Health;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace DubbingPlatform.UnitTests.Api;

/// <summary>
/// <c>GET /health</c> and <c>GET /version</c> (Task 043, instruction 3): the
/// liveness/readiness split, the combined status, the build stamp's sanitising,
/// and the two routes being anonymous.
///
/// The failure these rules exist to prevent: a liveness probe that depends on a
/// database, so a brief PostgreSQL blip restarts every pod simultaneously and
/// turns a degradation into an outage. That is why the liveness half is asserted
/// structurally rather than only through the endpoint.
/// </summary>
public sealed class HostingEndpointsTests
{
    private static BuildStamp Stamp() => new("1.4.2", "abc1234def5678", "v1.4.2", "v1", "2026-09-30T10:00:00Z");

    [Fact]
    public void Both_New_Routes_Are_Anonymous()
    {
        // A monitoring endpoint that 401s reports nothing. The callers that need
        // these during an incident are the ones least likely to hold a session.
        Assert.True(AnonymousRoutes.IsAnonymous("GET", "/health"));
        Assert.True(AnonymousRoutes.IsAnonymous("GET", "/version"));
    }

    [Fact]
    public void The_New_Routes_Are_In_The_All_List()
    {
        // `All` is what the auth filter consults, so a constant that is not in
        // the list is a constant that does nothing.
        Assert.Contains("GET /health", AnonymousRoutes.All);
        Assert.Contains("GET /version", AnonymousRoutes.All);
        Assert.Equal(8, AnonymousRoutes.All.Length);
    }

    [Fact]
    public void The_New_Routes_Are_Not_Product_Routes()
    {
        // The prefix boundary matters: /healthz and /version-history are
        // different paths and must not inherit the allowlist.
        Assert.False(AnonymousRoutes.IsAnonymous("GET", "/healthz"));
        Assert.False(AnonymousRoutes.IsAnonymous("GET", "/api/v1/health"));
        Assert.False(AnonymousRoutes.IsAnonymous("POST", "/version"));
    }

    [Fact]
    public void Route_Paths_Are_The_Documented_Ones()
    {
        Assert.Equal("/health", HealthEndpoints.HealthPath);
        Assert.Equal("/version", HealthEndpoints.VersionPath);
    }

    [Theory]
    [InlineData(HealthStatus.Healthy, HealthStatus.Healthy, "Healthy")]
    [InlineData(HealthStatus.Healthy, HealthStatus.Degraded, "Degraded")]
    [InlineData(HealthStatus.Healthy, HealthStatus.Unhealthy, "Unhealthy")]
    [InlineData(HealthStatus.Degraded, HealthStatus.Degraded, "Degraded")]
    [InlineData(HealthStatus.Unhealthy, HealthStatus.Degraded, "Unhealthy")]
    [InlineData(HealthStatus.Degraded, HealthStatus.Unhealthy, "Unhealthy")]
    [InlineData(HealthStatus.Unhealthy, HealthStatus.Unhealthy, "Unhealthy")]
    public void Overall_Status_Is_The_Worst_Of_The_Two(HealthStatus liveness, HealthStatus readiness, string expected)
    {
        Assert.Equal(expected, HealthEndpoints.OverallStatus(liveness, readiness));
    }

    [Fact]
    public void Combined_Report_Is_200_Only_When_Readiness_Is_Healthy()
    {
        // 503 on anything else, so an external check needs no JSON parsing to
        // decide whether this pod is allowed to serve.
        Assert.Equal(StatusCodes.Status200OK, StatusCodeFor(HealthStatus.Healthy, HealthStatus.Healthy));
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, StatusCodeFor(HealthStatus.Healthy, HealthStatus.Degraded));
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, StatusCodeFor(HealthStatus.Healthy, HealthStatus.Unhealthy));
    }

    [Fact]
    public void Liveness_Alone_Being_Healthy_Never_Makes_A_Report_Servable()
    {
        // The single most important line in the file: a process that is up and
        // cannot reach its database must not pass a traffic-gating check.
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, StatusCodeFor(HealthStatus.Healthy, HealthStatus.Unhealthy));
    }

    [Fact]
    public void OpenApi_Version_Agrees_Only_On_An_Exact_Non_Empty_Match()
    {
        Assert.True(HealthEndpoints.OpenApiVersionAgrees("v1", "v1"));
        Assert.True(HealthEndpoints.OpenApiVersionAgrees(" v1 ", "v1"));
        Assert.False(HealthEndpoints.OpenApiVersionAgrees("v1", "v2"));
        Assert.False(HealthEndpoints.OpenApiVersionAgrees("v1", ""));
        Assert.False(HealthEndpoints.OpenApiVersionAgrees(null, "v1"));
        // An unstamped build reports `unknown`, and `unknown` is never a version
        // that agrees with a real document.
        Assert.False(HealthEndpoints.OpenApiVersionAgrees("v1", BuildInformation.Unknown));
    }

    [Theory]
    [InlineData("1.4.2+abc1234", "1.4.2")]
    [InlineData("1.4.2", "1.4.2")]
    [InlineData("1.4.2+9e107d9", "1.4.2")]
    [InlineData("", "unknown")]
    [InlineData("   ", "unknown")]
    [InlineData(null, "unknown")]
    public void Version_Strips_The_Sdk_Source_Revision_Suffix(string? informational, string expected)
    {
        Assert.Equal(expected, BuildInformation.NormalizeVersion(informational));
    }

    [Fact]
    public void An_Unstamped_Build_Reports_Unknown_And_Nothing_Else()
    {
        // The sentinel has to be visible. A build that silently reported the
        // previous release's sha is worse than one that admits it does not know.
        var stamp = BuildInformation.Current;

        Assert.NotNull(stamp.Version);
        Assert.NotNull(stamp.Commit);
        Assert.True(
            stamp.Commit is BuildInformation.Unknown or not null,
            "commit is either a real sha or the explicit unknown sentinel");
    }

    [Fact]
    public void Unknown_Stamp_Describes_Itself_Uniformly()
    {
        Assert.Equal(BuildInformation.Unknown, BuildStamp.Unknown.Version);
        Assert.Equal(BuildInformation.Unknown, BuildStamp.Unknown.Commit);
        Assert.Equal(BuildInformation.Unknown, BuildStamp.Unknown.OpenApiVersion);
    }

    [Fact]
    public void Short_Commit_Is_Seven_Characters_For_A_Full_Sha()
    {
        Assert.Equal("abc1234", Stamp().ShortCommit);
        // A short stamp is returned whole rather than truncated to nothing.
        Assert.Equal("abc", new BuildStamp("1", "abc", "r", "v1", "t").ShortCommit);
    }

    [Fact]
    public async Task Version_Reports_Unknown_Migration_State_Rather_Than_Failing()
    {
        // A version answer that 500s during a database incident removes the one
        // endpoint that could have said which build is running.
        var (status, body) = await Execute<VersionResponse>(HealthEndpoints.Version(new ThrowingReader(), Stamp()));

        Assert.Equal(StatusCodes.Status200OK, status);
        Assert.NotNull(body);
        Assert.Equal(BuildInformation.Unknown, body!.MigrationHead);
        Assert.Equal(-1, body.MigrationsPending);
    }

    [Fact]
    public async Task Version_Reports_Real_Migration_State_When_Readable()
    {
        var state = new MigrationState(
            ["20260911061428_InitialCreate", "20260921115016_AddVoicePreviewJobs"],
            ["20260911061428_InitialCreate", "20260921115016_AddVoicePreviewJobs", "20260922082522_AddRefreshSessions"]);

        var (status, body) = await Execute<VersionResponse>(HealthEndpoints.Version(new StubReader(state), Stamp()));

        Assert.Equal(StatusCodes.Status200OK, status);
        Assert.NotNull(body);
        Assert.Equal("20260921115016_AddVoicePreviewJobs", body!.MigrationHead);
        Assert.Equal("20260922082522_AddRefreshSessions", body.MigrationTarget);
        Assert.Equal(1, body.MigrationsPending);
    }

    [Fact]
    public async Task Version_Ignores_Cancellation_As_A_Migration_Read_Failure()
    {
        // A cancelled request must propagate, not be swallowed into a 200 that
        // claims `unknown`. Only non-cancellation exceptions are absorbed.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => HealthEndpoints.Version(new ThrowingReader(new OperationCanceledException()), Stamp()));
    }

    [Fact]
    public async Task Version_Rejects_A_Null_Reader()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(async () => await HealthEndpoints.Version(null!, Stamp()));
    }

    [Fact]
    public async Task Report_Rejects_A_Null_Health_Service()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(async () => await HealthEndpoints.Report(null!, Stamp()));
    }

    [Fact]
    public void Mapping_Rejects_A_Null_Endpoint_Route_Builder()
    {
        Assert.Throws<ArgumentNullException>(() => HealthEndpoints.MapHostingEndpoints(null!));
    }

    [Fact]
    public void DescribeMigrationState_Distinguishes_Unreadable_From_Current()
    {
        Assert.Equal("migration state unreadable", HealthEndpoints.DescribeMigrationState(null));
        Assert.Contains("current", HealthEndpoints.DescribeMigrationState(new MigrationState(["a"], ["a"])), StringComparison.Ordinal);
    }

    private static int StatusCodeFor(HealthStatus liveness, HealthStatus readiness) =>
        HealthEndpoints.OverallStatus(liveness, readiness) == nameof(HealthStatus.Healthy)
            ? StatusCodes.Status200OK
            : StatusCodes.Status503ServiceUnavailable;

    /// <summary>
    /// Runs an <see cref="IResult"/> against a real <see cref="HttpContext"/> and
    /// reads back the status code and the JSON body. Executing the result is what
    /// makes these assertions about the response a client actually receives,
    /// rather than about the return type - a `Results.Json(body)` with the wrong
    /// status code still satisfies a shape-only test.
    /// </summary>
    private static readonly System.Text.Json.JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// A request scope with the JSON options the framework would have configured.
    /// <c>Results.Json</c> resolves its serializer from
    /// <c>IOptions&lt;Microsoft.AspNetCore.Http.Json.JsonOptions&gt;</c>, and a
    /// <c>DefaultHttpContext</c> with no <c>RequestServices</c> makes it throw
    /// <c>ArgumentNullException(provider)</c> before writing anything - which is a
    /// test that passes for the wrong reason if you only assert the body.
    /// </summary>
    private static DefaultHttpContext NewContext()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.Configure<Microsoft.AspNetCore.Http.Json.JsonOptions>(_ => { });
        var context = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static async Task<(int Status, T Body)> Execute<T>(Task<IResult> pending)
    {
        var context = NewContext();
        var result = await pending;
        await result.ExecuteAsync(context);

        context.Response.Body.Position = 0;
        var body = await new StreamReader(context.Response.Body).ReadToEndAsync();
        return (context.Response.StatusCode, System.Text.Json.JsonSerializer.Deserialize<T>(body, JsonOptions)!);
    }

    private sealed class StubReader(MigrationState state) : IMigrationStateReader
    {
        public Task<MigrationState> ReadAsync(CancellationToken cancellationToken = default) => Task.FromResult(state);
    }

    private sealed class ThrowingReader(Exception? failure = null) : IMigrationStateReader
    {
        public Task<MigrationState> ReadAsync(CancellationToken cancellationToken = default) =>
            throw failure ?? new InvalidOperationException("boom");
    }
}
