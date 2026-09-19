using DubbingPlatform.Application.Options;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace DubbingPlatform.Infrastructure.Health;

/// <summary>
/// Local-inference sidecar warmup readiness probe for GPU workers (Task 043).
/// When <c>Features:LocalInferenceEnabled=false</c> (default) it is Healthy
/// without touching the network so core behavior is unchanged and no sidecar
/// is required. When enabled it probes GET <c>{Endpoint}/health</c> with a
/// short timeout; failure is Unhealthy and the worker takes no work.
/// Tagged <c>ready</c> only and registered only for the <c>gpu</c> worker role.
/// </summary>
public sealed class LocalInferenceWarmupCheck : IHealthCheck
{
    public const string Name = "local-inference-warmup";

    private readonly IOptions<FeatureOptions> _features;
    private readonly IOptions<LocalInferenceOptions> _options;
    private readonly IHttpClientFactory _httpFactory;

    public LocalInferenceWarmupCheck(
        IOptions<FeatureOptions> features,
        IOptions<LocalInferenceOptions> options,
        IHttpClientFactory httpFactory)
    {
        ArgumentNullException.ThrowIfNull(features);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(httpFactory);
        _features = features;
        _options = options;
        _httpFactory = httpFactory;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (!_features.Value.LocalInferenceEnabled)
        {
            return HealthCheckResult.Healthy("Local inference is disabled.");
        }

        var endpoint = _options.Value.Endpoint;
        if (!LocalInferenceOptions.IsSidecarEndpointAllowed(endpoint))
        {
            return HealthCheckResult.Unhealthy("LocalInference:Endpoint is not allowed.");
        }

        string baseUrl;
        try
        {
            baseUrl = LocalInferenceOptions.RequireSidecarEndpoint(endpoint, nameof(LocalInferenceOptions.Endpoint));
        }
        catch (ArgumentException ex)
        {
            return HealthCheckResult.Unhealthy($"LocalInference endpoint invalid: {ex.Message}");
        }

        try
        {
            var client = _httpFactory.CreateClient(Providers.LocalInference.LocalInferenceProvider.HttpClientName);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linked.CancelAfter(TimeSpan.FromSeconds(5));
            using var response = await client.GetAsync(
                string.Concat(baseUrl, "/health"),
                HttpCompletionOption.ResponseHeadersRead,
                linked.Token).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                return HealthCheckResult.Healthy("Local sidecar is warmed.");
            }

            return HealthCheckResult.Unhealthy($"Local sidecar warmup failed ({(int)response.StatusCode}).");
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            return HealthCheckResult.Unhealthy($"Local sidecar warmup timed out: {ex.Message}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return HealthCheckResult.Unhealthy($"Local sidecar unreachable: {ex.GetType().Name}.");
        }
    }
}
