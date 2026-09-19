using DubbingPlatform.Infrastructure.Health;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace DubbingPlatform.UnitTests.Health;

/// <summary>
/// Verifies health separation: liveness never depends on dependencies, readiness
/// reflects configuration, and fast profile excludes RabbitMQ/Redis.
/// </summary>
public sealed class HealthRegistrationTests
{
    [Fact]
    public async Task LiveCheck_Is_Always_Healthy()
    {
        var check = new LiveCheck();
        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task NpgSqlCheck_Is_Unhealthy_Without_ConnectionString()
    {
        var check = new NpgSqlHealthCheck(string.Empty);
        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }

    [Fact]
    public async Task NpgSqlCheck_Is_Unhealthy_When_Down()
    {
        var check = new NpgSqlHealthCheck("Host=127.0.0.1;Port=1;Database=down;Username=down;Password=down");
        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }

    [Fact]
    public void IsFullProfile_Only_For_RabbitMq()
    {
        Assert.False(HealthRegistration.IsFullProfile(ConfigWithTransport("InMemory")));
        Assert.False(HealthRegistration.IsFullProfile(ConfigWithTransport("inmemory")));
        Assert.False(HealthRegistration.IsFullProfile(ConfigWithTransport(string.Empty)));
        Assert.True(HealthRegistration.IsFullProfile(ConfigWithTransport("RabbitMq")));
        Assert.True(HealthRegistration.IsFullProfile(ConfigWithTransport("rabbitmq")));
    }

    [Fact]
    public void Worker_Roles_Classified()
    {
        Assert.True(HealthRegistration.IsMediaRole("media-preparation"));
        Assert.True(HealthRegistration.IsMediaRole("media-render"));
        Assert.False(HealthRegistration.IsMediaRole("ai"));
        Assert.False(HealthRegistration.IsMediaRole("control"));

        Assert.True(HealthRegistration.IsAiRole("ai"));
        Assert.True(HealthRegistration.IsAiRole("gpu"));
        Assert.False(HealthRegistration.IsAiRole("media-preparation"));
    }

    private static IConfiguration ConfigWithTransport(string provider)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Transport:Provider"] = provider })
            .Build();
    }
}
