using Microsoft.Extensions.Diagnostics.HealthChecks;
using StackExchange.Redis;

namespace DubbingPlatform.Infrastructure.Health;

/// <summary>
/// Redis readiness probe (full profile only). Pings the server with a short
/// timeout. Unhealthy when the connection string is missing or Redis is
/// unreachable. Tagged <c>ready</c> only; not registered in fast profile.
/// </summary>
public sealed class RedisHealthCheck : IHealthCheck
{
    public const string Name = "redis";

    private readonly string _connection;

    public RedisHealthCheck(string connection)
    {
        _connection = connection;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_connection))
        {
            return HealthCheckResult.Unhealthy("Redis connection is not configured.");
        }

        try
        {
            var options = ConfigurationOptions.Parse(_connection);
            options.ConnectTimeout = 5000;
            options.SyncTimeout = 5000;
            options.AbortOnConnectFail = true;
            using var multiplexer = await ConnectionMultiplexer.ConnectAsync(options).ConfigureAwait(false);
            var database = multiplexer.GetDatabase();
            await database.PingAsync().ConfigureAwait(false);
            return HealthCheckResult.Healthy("Redis is reachable.");
        }
        catch (Exception exception)
        {
            return HealthCheckResult.Unhealthy($"Redis is unreachable: {exception.GetType().Name}.");
        }
    }
}
