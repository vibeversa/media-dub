using DubbingPlatform.Application.Abstractions.Providers;
using DubbingPlatform.Application.Errors;
using DubbingPlatform.Application.Exceptions;
using DubbingPlatform.Application.Options;
using DubbingPlatform.Domain.Enums;
using DubbingPlatform.Domain.Exceptions;
using Microsoft.Extensions.Options;

namespace DubbingPlatform.Application.Providers;

/// <summary>
/// Privacy-aware provider router. Order is fixed:
/// capability → policy → route-priority → health → cost.
/// No provider precedence is hardcoded: ordering comes solely from
/// <c>Providers:RoutePriority[capability]</c> (case-insensitive capability key,
/// ordered provider names); providers absent from the list sort last by name
/// as a deterministic tiebreak, never as precedence. Compatibility
/// (language/size/duration via <see cref="IDescriptorStore"/>) runs before any
/// routing. Unhealthy providers are skipped within
/// <c>Retry:FallbackMaxAttempts</c> (initial + fallbacks); cost-blocked
/// providers are skipped the same way. Throws <c>POLICY_DENIED</c> when tenant
/// policy blocks every compatible candidate (metric
/// <c>provider.policy_denied_total</c>), <c>PROVIDER_CONFIGURATION_ERROR</c>
/// when nothing supports the capability, <c>PROVIDER_FAILED</c> when health
/// exhausts the budget, and <c>QUOTA_EXCEEDED</c> when cost blocks the rest.
/// </summary>
public sealed class ProviderResolver
{
    private readonly IOptions<ProviderOptions> _providers;
    private readonly IOptions<PrivacyOptions> _privacy;
    private readonly IOptions<RetryOptions> _retry;
    private readonly IDescriptorStore _descriptors;
    private readonly IProviderHealthTracker _health;
    private readonly IProviderCostGate _costGate;
    private readonly IProcessingPolicyProvider _policies;

    public ProviderResolver(
        IOptions<ProviderOptions> providers,
        IOptions<PrivacyOptions> privacy,
        IOptions<RetryOptions> retry,
        IDescriptorStore descriptors,
        IProviderHealthTracker health,
        IProviderCostGate costGate,
        IProcessingPolicyProvider policies)
    {
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(privacy);
        ArgumentNullException.ThrowIfNull(retry);
        ArgumentNullException.ThrowIfNull(descriptors);
        ArgumentNullException.ThrowIfNull(health);
        ArgumentNullException.ThrowIfNull(costGate);
        ArgumentNullException.ThrowIfNull(policies);
        _providers = providers;
        _privacy = privacy;
        _retry = retry;
        _descriptors = descriptors;
        _health = health;
        _costGate = costGate;
        _policies = policies;
    }

    public async Task<(ProviderType Provider, string Model)> ResolveAsync(
        ProviderCapability capability,
        Guid tenantId,
        string language,
        long inputBytes,
        int durationMs,
        CancellationToken cancellationToken = default)
    {
        if (tenantId == Guid.Empty)
        {
            throw new DomainException("TenantId must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(language))
        {
            throw new DomainException("Language must not be empty.");
        }

        if (inputBytes < 0)
        {
            throw new DomainException("InputBytes must be >= 0.");
        }

        if (durationMs < 0)
        {
            throw new DomainException("DurationMs must be >= 0.");
        }

        var request = new ProviderRoutingRequest(
            language.Trim(), inputBytes, durationMs, null, false, false, false);

        var candidates = await _descriptors.GetCandidatesAsync(capability, tenantId, cancellationToken).ConfigureAwait(false);
        var compatible = candidates.Where(d => _descriptors.IsCompatible(d, request)).ToList();
        if (compatible.Count == 0)
        {
            throw new ErrorCodeException(
                ErrorCodes.ProviderConfigurationError,
                $"No provider supports capability '{capability}' for the requested input (UnsupportedCapability).");
        }

        var policy = await _policies.GetAsync(tenantId, cancellationToken).ConfigureAwait(false)
            ?? SyntheticPolicy(tenantId);
        var allowed = compatible
            .Where(d => PolicyChecker.CanUseProvider(policy, d.Provider, d.Region))
            .ToList();
        if (allowed.Count == 0)
        {
            ProviderMeters.PolicyDenied.Add(1);
            throw new ErrorCodeException(
                ErrorCodes.PolicyDenied,
                $"Tenant policy blocks all providers for capability '{capability}'.");
        }

        var enabled = allowed.Where(IsEnabled).ToList();
        if (enabled.Count == 0)
        {
            throw new ErrorCodeException(
                ErrorCodes.ProviderConfigurationError,
                $"All providers for capability '{capability}' are disabled.");
        }

        var ordered = OrderByPriority(enabled, capability);
        var maxAttempts = Math.Max(1, _retry.Value.FallbackMaxAttempts + 1);
        var examined = 0;
        var healthSkips = 0;
        var costSkips = 0;

        foreach (var descriptor in ordered)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (examined >= maxAttempts)
            {
                break;
            }

            examined++;
            if (!_health.IsHealthy(descriptor.Provider))
            {
                healthSkips++;
                continue;
            }

            bool canProceed;
            try
            {
                canProceed = await _costGate.CanProceedAsync(tenantId, capability, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception)
            {
                costSkips++;
                continue;
            }

            if (!canProceed)
            {
                costSkips++;
                continue;
            }

            var model = ResolveModel(descriptor.Provider, capability);
            ProviderMeters.RouteSelected.Add(1);
            return (descriptor.Provider, model);
        }

        if (costSkips > 0 && healthSkips == 0)
        {
            throw new ErrorCodeException(
                ErrorCodes.QuotaExceeded,
                $"Cost guard blocks all routes for capability '{capability}'.");
        }

        throw new ErrorCodeException(
            ErrorCodes.ProviderFailed,
            $"No healthy provider route for capability '{capability}' within fallback budget ({maxAttempts} attempt(s)).");
    }

    private bool IsEnabled(Domain.Entities.ProviderCapabilityDescriptor descriptor)
    {
        var enabled = _providers.Value.Enabled;
        if (enabled is null || enabled.Count == 0)
        {
            return true;
        }

        var name = ProviderOptionNames.NormalizeProvider(descriptor.Provider);
        foreach (var pair in enabled)
        {
            if (string.Equals(pair.Key?.Trim(), name, StringComparison.OrdinalIgnoreCase)
                || string.Equals(pair.Key?.Trim(), descriptor.Provider.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                return pair.Value;
            }
        }

        if (descriptor.Provider == ProviderType.LocalInference)
        {
            foreach (var pair in enabled)
            {
                if (string.Equals(pair.Key?.Trim(), "local", StringComparison.OrdinalIgnoreCase))
                {
                    return pair.Value;
                }
            }
        }

        return true;
    }

    private List<Domain.Entities.ProviderCapabilityDescriptor> OrderByPriority(
        List<Domain.Entities.ProviderCapabilityDescriptor> enabled,
        ProviderCapability capability)
    {
        var priority = FindPriorityList(capability);
        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < priority.Count; i++)
        {
            if (!index.ContainsKey(priority[i]))
            {
                index[priority[i]] = i;
            }
        }

        return enabled
            .OrderBy(d => Rank(d, index))
            .ThenBy(d => d.Provider.ToString(), StringComparer.Ordinal)
            .ToList();
    }

    private IReadOnlyList<string> FindPriorityList(ProviderCapability capability)
    {
        var routes = _providers.Value.RoutePriority;
        if (routes is null)
        {
            return [];
        }

        foreach (var pair in routes)
        {
            if (string.Equals(pair.Key?.Trim(), capability.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                return pair.Value ?? [];
            }
        }

        return [];
    }

    private static int Rank(Domain.Entities.ProviderCapabilityDescriptor descriptor, Dictionary<string, int> index)
    {
        var names = CandidateNames(descriptor.Provider);
        var best = int.MaxValue;
        foreach (var name in names)
        {
            if (index.TryGetValue(name, out var position) && position < best)
            {
                best = position;
            }
        }

        return best == int.MaxValue ? int.MaxValue : best;
    }

    private static string[] CandidateNames(ProviderType provider)
    {
        return provider switch
        {
            ProviderType.LocalInference => ["localinference", "local"],
            ProviderType.Google => ["google", "gemini"],
            _ => [provider.ToString().ToLowerInvariant(), provider.ToString()],
        };
    }

    private string ResolveModel(ProviderType provider, ProviderCapability capability)
    {
        foreach (var option in _providers.Value.Descriptors ?? [])
        {
            if (option is null || string.IsNullOrWhiteSpace(option.Model))
            {
                continue;
            }

            if (!ProviderOptionNames.TryParseProvider(option.Provider, out var parsed) || parsed != provider)
            {
                continue;
            }

            if (Enum.TryParse<ProviderCapability>(option.Capability, ignoreCase: true, out var parsedCap)
                && parsedCap == capability)
            {
                return option.Model;
            }
        }

        return string.Concat(ProviderOptionNames.NormalizeProvider(provider), "-default");
    }

    private Domain.Entities.ProcessingPolicy SyntheticPolicy(Guid tenantId)
    {
        var privacy = _privacy.Value;
        var now = DateTimeOffset.UtcNow;
        return new Domain.Entities.ProcessingPolicy(
            Guid.NewGuid(),
            tenantId,
            privacy?.ExternalProvidersAllowed ?? true,
            privacy?.AllowedProviders ?? ["mock"],
            privacy?.ResidencyConstraint,
            "allow",
            "allow",
            true,
            null,
            now,
            now);
    }
}
