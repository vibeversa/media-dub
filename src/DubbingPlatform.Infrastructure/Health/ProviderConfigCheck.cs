using DubbingPlatform.Application.Options;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace DubbingPlatform.Infrastructure.Health;

/// <summary>
/// Provider-configuration readiness probe for AI workers. Verifies the configured
/// default provider is present and enabled. Tagged <c>ready</c> only and
/// registered only for AI/GPU worker roles.
/// </summary>
public sealed class ProviderConfigCheck : IHealthCheck
{
    public const string Name = "providers";

    private readonly IOptions<ProviderOptions> _options;

    public ProviderConfigCheck(IOptions<ProviderOptions> options)
    {
        _options = options;
    }

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var providers = _options.Value;
        if (string.IsNullOrWhiteSpace(providers.DefaultProvider))
        {
            return Task.FromResult(HealthCheckResult.Unhealthy("Default provider is not configured."));
        }

        if (providers.Enabled.TryGetValue(providers.DefaultProvider, out var enabled) && !enabled)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy($"Default provider '{providers.DefaultProvider}' is disabled."));
        }

        return Task.FromResult(HealthCheckResult.Healthy($"Provider '{providers.DefaultProvider}' is configured."));
    }
}
