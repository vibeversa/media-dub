using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Domain.Enums;

namespace DubbingPlatform.Application.Providers;

/// <summary>
/// Tenant-policy gate. Enforces the machine-checkable subset of
/// <see cref="ProcessingPolicy"/>: external allowance, allowed-provider list,
/// residency constraint, and local-inference allowance. Free-form
/// <c>SensitivePolicy</c>/<c>VoicePolicy</c>/<c>RetentionOverride</c> strings
/// carry no block semantics in the domain and are hashed into the route
/// snapshot for audit instead of blocking here; workers enforce their
/// call-site semantics (consent, retention).
/// Local-only routing (Task 043, optional) reuses
/// <c>ExternalProvidersAllowed=false + LocalInferenceAllowed=true</c> with
/// <c>AllowedProviders</c> containing <c>local</c>/<c>LocalInference</c>; this
/// blocks every external provider so <c>ProviderResolver</c> restricts to
/// local-only. No new <c>LocalInferenceOnly</c> column was added.
/// </summary>
public static class PolicyChecker
{
    /// <summary>
    /// Whether <paramref name="provider"/> may serve the tenant.
    /// Null policy fails closed to mock-only (defense in depth when no row exists).
    /// Local-only tenants (<c>ExternalProvidersAllowed=false</c>,
    /// <c>LocalInferenceAllowed=true</c>) admit only
    /// <c>Mock</c>/<c>LocalInference</c> subject to <c>AllowedProviders</c>.
    /// </summary>
    public static bool CanUseProvider(ProcessingPolicy? policy, ProviderType provider, string? providerRegion)
    {
        if (policy is null)
        {
            return provider == ProviderType.Mock;
        }

        var isExternal = provider is not ProviderType.Mock and not ProviderType.LocalInference;
        if (isExternal && !policy.ExternalProvidersAllowed)
        {
            return false;
        }

        if (policy.AllowedProviders.Length > 0 && !IsListed(policy.AllowedProviders, provider))
        {
            return false;
        }

        if (provider == ProviderType.LocalInference && !policy.LocalInferenceAllowed)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(policy.ResidencyConstraint))
        {
            if (string.IsNullOrWhiteSpace(providerRegion))
            {
                return false;
            }

            if (!string.Equals(policy.ResidencyConstraint.Trim(), providerRegion.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsListed(string[] allowed, ProviderType provider)
    {
        foreach (var entry in allowed)
        {
            if (string.IsNullOrWhiteSpace(entry))
            {
                continue;
            }

            var trimmed = entry.Trim();
            if (string.Equals(trimmed, provider.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (provider == ProviderType.LocalInference && string.Equals(trimmed, "local", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (provider == ProviderType.Google && string.Equals(trimmed, "gemini", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
