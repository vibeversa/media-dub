using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace DubbingPlatform.Infrastructure.Health;

/// <summary>
/// Process-local liveness probe. Always healthy and never depends on PostgreSQL,
/// RabbitMQ, Redis, storage, providers, or FFmpeg.
/// </summary>
public sealed class LiveCheck : IHealthCheck
{
    public const string Name = "live";

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(HealthCheckResult.Healthy("Process is running."));
    }
}
