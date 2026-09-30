using DubbingPlatform.Infrastructure.Health;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace DubbingPlatform.UnitTests.Health;

/// <summary>
/// Migration-currency readiness (Task 043, R3). The rule is exercised over two
/// string lists, so every case below runs with no database and no container —
/// which is the point: this is the check that decides whether a pod may take
/// traffic, and it must be testable without the infrastructure it protects.
/// </summary>
public sealed class MigrationCurrencyTests
{
    private static readonly string[] Chain =
    [
        "20260911061428_InitialCreate",
        "20260912075115_AddProjectSoftDelete",
        "20260921115016_AddVoicePreviewJobs",
    ];

    [Fact]
    public void Current_When_Nothing_Pending()
    {
        var state = new MigrationState(Chain, Chain);

        Assert.True(state.IsCurrent);
        Assert.False(state.IsAhead);
        Assert.Empty(state.Pending);
        Assert.Equal("20260921115016_AddVoicePreviewJobs", state.AppliedHead);
        Assert.Equal(state.AppliedHead, state.DefinedHead);
        Assert.Contains("current", state.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void Not_Current_When_The_Build_Defines_More_Than_The_Schema_Has()
    {
        var state = new MigrationState(Chain[..2], Chain);

        Assert.False(state.IsCurrent);
        Assert.Single(state.Pending);
        Assert.Equal("20260921115016_AddVoicePreviewJobs", state.Pending[0]);
        Assert.StartsWith("1 migration(s) pending", state.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void Not_Current_When_The_Schema_Is_Ahead_Of_The_Build()
    {
        // The contract-phase state: a pod rolled back to a build that predates a
        // drop. Treated as not-current rather than as "pending" because the
        // forward fix is different - the column the new build needs is gone.
        var state = new MigrationState(Chain, Chain[..2]);

        Assert.False(state.IsCurrent);
        Assert.True(state.IsAhead);
        Assert.Empty(state.Pending);
        Assert.Contains("AHEAD", state.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void Applied_And_Defined_Empty_Is_Current()
    {
        // A build with no compiled migrations against a database with no history
        // is genuinely current. Reporting it unhealthy would make an in-memory
        // test host permanently unready.
        var state = new MigrationState([], []);

        Assert.True(state.IsCurrent);
        Assert.Null(state.AppliedHead);
        Assert.Null(state.DefinedHead);
    }

    [Fact]
    public async Task Check_Is_Healthy_When_Current()
    {
        var check = new MigrationCurrencyCheck(new StubReader(new MigrationState(Chain, Chain)));

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Equal("20260921115016_AddVoicePreviewJobs", result.Data!["appliedHead"]);
        Assert.Equal(0, result.Data["pending"]);
        Assert.False((bool)result.Data["ahead"]!);
    }

    [Fact]
    public async Task Check_Is_Unhealthy_When_Pending()
    {
        var check = new MigrationCurrencyCheck(new StubReader(new MigrationState(Chain[..1], Chain)));

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Equal(2, result.Data["pending"]);
    }

    [Fact]
    public async Task Check_Is_Unhealthy_When_The_History_Table_Cannot_Be_Read()
    {
        // Fail closed. A probe that cannot tell whether the schema matches is
        // exactly the probe this exists to stop trusting.
        var check = new MigrationCurrencyCheck(new ThrowingReader());

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("could not be read", result.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Check_Does_Not_Leak_The_Connection_String()
    {
        var check = new MigrationCurrencyCheck(new ThrowingReader(new InvalidOperationException("Host=db Password=hunter2")));

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.DoesNotContain("hunter2", result.Description, StringComparison.Ordinal);
        Assert.Equal("Migration currency could not be read: InvalidOperationException.", result.Description);
    }

    [Fact]
    public void Constructor_Rejects_A_Null_Reader()
    {
        Assert.Throws<ArgumentNullException>(() => new MigrationCurrencyCheck(null!));
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
