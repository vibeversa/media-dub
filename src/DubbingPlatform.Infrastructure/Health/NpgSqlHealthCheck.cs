using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace DubbingPlatform.Infrastructure.Health;

/// <summary>
/// PostgreSQL readiness probe. Opens a short-lived <c>NpgsqlConnection</c> with a
/// 5-second timeout. Unhealthy when the connection string is missing or the
/// database is unreachable. Tagged <c>ready</c> only; never part of liveness.
/// </summary>
public sealed class NpgSqlHealthCheck : IHealthCheck
{
    public const string Name = "postgres";

    private readonly string _connectionString;

    public NpgSqlHealthCheck(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_connectionString))
        {
            return HealthCheckResult.Unhealthy("PostgreSQL connection string is not configured.");
        }

        try
        {
            var builder = new NpgsqlConnectionStringBuilder(_connectionString)
            {
                Timeout = 5,
                CommandTimeout = 5,
            };

            using var connection = new NpgsqlConnection(builder.ConnectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            using var command = new NpgsqlCommand("SELECT 1;", connection) { CommandTimeout = 5 };
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return HealthCheckResult.Healthy("PostgreSQL is reachable.");
        }
        catch (Exception exception)
        {
            return HealthCheckResult.Unhealthy($"PostgreSQL is unreachable: {exception.GetType().Name}.");
        }
    }
}
