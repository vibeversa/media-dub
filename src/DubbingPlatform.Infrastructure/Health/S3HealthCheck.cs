using Amazon.S3;
using DubbingPlatform.Application.Options;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace DubbingPlatform.Infrastructure.Health;

/// <summary>
/// S3-compatible storage readiness probe (MinIO locally). Lists buckets with a
/// short timeout. Unhealthy when storage is unreachable. Tagged <c>ready</c> only.
/// </summary>
public sealed class S3HealthCheck : IHealthCheck
{
    public const string Name = "storage";

    private readonly IOptions<StorageOptions> _options;

    public S3HealthCheck(IOptions<StorageOptions> options)
    {
        _options = options;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var storage = _options.Value;
        if (string.IsNullOrWhiteSpace(storage.Endpoint))
        {
            return HealthCheckResult.Unhealthy("Storage endpoint is not configured.");
        }

        try
        {
            var config = new AmazonS3Config
            {
                ServiceURL = NormalizeEndpoint(storage.Endpoint, storage.UseSsl),
                ForcePathStyle = true,
                Timeout = TimeSpan.FromSeconds(5),
            };

            using var client = new AmazonS3Client(storage.AccessKey, storage.SecretKey, config);
            using var timeoutSource = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
            await client.ListBucketsAsync(linked.Token).ConfigureAwait(false);
            return HealthCheckResult.Healthy("Storage is reachable.");
        }
        catch (Exception exception)
        {
            return HealthCheckResult.Unhealthy($"Storage is unreachable: {exception.GetType().Name}.");
        }
    }

    internal static string NormalizeEndpoint(string endpoint, bool useSsl)
    {
        if (endpoint.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            endpoint.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return endpoint;
        }

        return string.Concat(useSsl ? "https://" : "http://", endpoint);
    }
}
