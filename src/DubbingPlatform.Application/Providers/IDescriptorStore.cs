using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Domain.Enums;

namespace DubbingPlatform.Application.Providers;

/// <summary>
/// Versioned descriptor source: global <c>Providers:Descriptors[]</c> config
/// merged with tenant DB rows. Highest <c>Version</c> per
/// (provider, capability) wins; DB rows win version ties as tenant overrides.
/// </summary>
public interface IDescriptorStore
{
    Task<IReadOnlyList<ProviderCapabilityDescriptor>> GetCandidatesAsync(
        ProviderCapability capability,
        Guid tenantId,
        CancellationToken cancellationToken = default);

    bool IsCompatible(ProviderCapabilityDescriptor descriptor, ProviderRoutingRequest request);
}
