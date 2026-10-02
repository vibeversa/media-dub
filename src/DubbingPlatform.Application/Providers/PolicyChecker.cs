using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Domain.Enums;

namespace DubbingPlatform.Application.Providers;

/// <summary>
/// Tenant-policy gate. Enforces the machine-checkable subset of
/// <see cref="ProcessingPolicy"/>: external allowance, allowed-provider list,
/// residency constraint, local-inference allowance, plus restrictive
/// <c>SensitivePolicy</c>/<c>VoicePolicy</c>/<c>RetentionOverride</c> values
/// (GAP-006). Restrictive values (case-insensitive: restricted, local-only,
/// no-external, deny-external, private, confidential, no-clone, deny,
/// block-external) block every external provider so routing stays local-only;
/// permissive values (allow, default, empty) preserve existing behavior and
/// are hashed into the route snapshot for audit. Workers enforce call-site
/// semantics (consent, retention).
/// Local-only routing (Task 043, optional) reuses
/// <c>ExternalProvidersAllowed=false + LocalInferenceAllowed=true</c> with
/// <c>AllowedProviders</c> containing <c>local</c>/<c>LocalInference</c>; this
/// blocks every external provider so <c>ProviderResolver</c> restricts to
/// local-only. No new <c>LocalInferenceOnly</c> column was added.
/// </summary>
public static class PolicyChecker
{
    private static readonly HashSet<string> RestrictivePolicies = new(StringComparer.OrdinalIgnoreCase)
    {
        "restricted",
        "local-only",
        "localonly",
        "local_only",
        "no-external",
        "noexternal",
        "deny-external",
        "deny",
        "private",
        "confidential",
        "no-clone",
        "noclone",
        "block-external",
    };
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

        if (isExternal && IsRestrictive(policy.SensitivePolicy))
        {
            return false;
        }

        if (isExternal && IsRestrictive(policy.VoicePolicy))
        {
            return false;
        }

        if (isExternal && !string.IsNullOrWhiteSpace(policy.RetentionOverride) && IsRestrictive(policy.RetentionOverride))
        {
            return false;
        }

        return true;
    }

    public static bool IsRestrictive(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value.Trim().ToLowerInvariant();
        if (RestrictivePolicies.Contains(normalized))
        {
            return true;
        }

        return normalized.Contains("local-only", StringComparison.Ordinal)
            || normalized.Contains("localonly", StringComparison.Ordinal)
            || normalized.Contains("no-external", StringComparison.Ordinal)
            || normalized.Contains("noexternal", StringComparison.Ordinal)
            || normalized.Contains("deny-external", StringComparison.Ordinal)
            || normalized.Contains("block-external", StringComparison.Ordinal)
            || normalized.Contains("restricted", StringComparison.Ordinal);
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
