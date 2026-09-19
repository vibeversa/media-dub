using Amazon.S3;
using DubbingPlatform.Application.Abstractions;
using DubbingPlatform.Application.Options;
using DubbingPlatform.Application.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace DubbingPlatform.Infrastructure.Storage;

/// <summary>
/// Storage wiring: S3 client from <c>Storage</c> options (endpoint/bucket/ssl
/// plus access/secret keys, never hardcoded), artifact services, and
/// Development-only bucket auto-creation. Production buckets are provisioned
/// out of band; auto-create never runs outside Development.
/// </summary>
public static class StorageRegistration
{
    /// <summary>
    /// Registers the S3 client, storage adapter, inventory, and publication services.
    /// </summary>
    public static void AddDubbingStorage(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddSingleton<IAmazonS3>(provider =>
        {
            var options = provider.GetRequiredService<IOptions<StorageOptions>>().Value;
            var config = new AmazonS3Config
            {
                ServiceURL = NormalizeEndpoint(options.Endpoint, options.UseSsl),
                ForcePathStyle = true,
                Timeout = TimeSpan.FromSeconds(30),
            };
            return new AmazonS3Client(options.AccessKey, options.SecretKey, config);
        });

        services.AddSingleton<S3ArtifactStorage>();
        services.AddSingleton<IArtifactStorage>(provider => provider.GetRequiredService<S3ArtifactStorage>());
        services.AddSingleton<IStorageInventory>(provider => provider.GetRequiredService<S3ArtifactStorage>());
        services.AddScoped<ContentObjectService>();
        services.AddScoped<ArtifactService>(provider => new ArtifactService(
            provider.GetRequiredService<IStageExecutionContextFactory>(),
            provider.GetRequiredService<IArtifactStorage>(),
            provider.GetService<IQuotaGate>()));
        services.AddHostedService<StorageBucketInitializer>();
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

/// <summary>
/// Ensures the configured bucket exists on startup in Development only.
/// Production buckets are managed out of band and must never be auto-created.
/// </summary>
public sealed class StorageBucketInitializer : IHostedService
{
    private readonly IAmazonS3 _client;
    private readonly IOptions<StorageOptions> _options;
    private readonly IHostEnvironment _environment;

    public StorageBucketInitializer(
        IAmazonS3 client,
        IOptions<StorageOptions> options,
        IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(environment);
        _client = client;
        _options = options;
        _environment = environment;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_environment.IsDevelopment())
        {
            return;
        }

        var bucket = _options.Value.Bucket;
        try
        {
            var buckets = await _client.ListBucketsAsync(cancellationToken).ConfigureAwait(false);
            if (buckets.Buckets.Any(b => string.Equals(b.BucketName, bucket, StringComparison.Ordinal)))
            {
                return;
            }

            await _client.PutBucketAsync(bucket, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Startup probe only: health checks report storage readiness.
            // Never fail host startup on storage unavailability.
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
