using DubbingPlatform.Domain.Enums;

namespace DubbingPlatform.Application.Abstractions.Providers;

/// <summary>
/// Routing cost gate. Application-owned so the resolver never references
/// Infrastructure; the Infrastructure adapter delegates to the frozen
/// <c>ICostGate</c> contract.
/// </summary>
public interface IProviderCostGate
{
    Task<bool> CanProceedAsync(Guid tenantId, ProviderCapability capability, CancellationToken cancellationToken = default);
}
