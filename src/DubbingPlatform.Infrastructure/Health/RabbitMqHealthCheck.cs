using System.Net.Sockets;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace DubbingPlatform.Infrastructure.Health;

/// <summary>
/// RabbitMQ TCP readiness probe (full profile only). Opens a short TCP connection
/// to the broker; no AMQP handshake. Unhealthy when the broker is unreachable.
/// Tagged <c>ready</c> only; not registered in the fast (InMemory) profile.
/// </summary>
public sealed class RabbitMqHealthCheck : IHealthCheck
{
    public const string Name = "rabbitmq";

    private readonly string _host;

    private readonly int _port;

    public RabbitMqHealthCheck(string host, int port)
    {
        _host = string.IsNullOrWhiteSpace(host) ? "localhost" : host;
        _port = port <= 0 ? 5672 : port;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = new TcpClient();
            using var timeoutSource = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
            await client.ConnectAsync(_host, _port, linked.Token).ConfigureAwait(false);
            return HealthCheckResult.Healthy("RabbitMQ TCP is reachable.");
        }
        catch (Exception exception)
        {
            return HealthCheckResult.Unhealthy($"RabbitMQ is unreachable: {exception.GetType().Name}.");
        }
    }
}
