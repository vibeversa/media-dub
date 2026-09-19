using DubbingPlatform.Application.Abstractions.Providers;
using DubbingPlatform.Domain.Enums;
using DubbingPlatform.Infrastructure.Orchestration;

namespace DubbingPlatform.Infrastructure.Providers;

/// <summary>
/// Bridges the Application routing cost gate to the frozen
/// <see cref="ICostGate"/> contract (single unit per resolve).
/// </summary>
public sealed class ProviderCostGateAdapter : IProviderCostGate
{
    private readonly ICostGate _inner;

    public ProviderCostGateAdapter(ICostGate inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
    }

    public Task<bool> CanProceedAsync(Guid tenantId, ProviderCapability capability, CancellationToken cancellationToken = default)
    {
        return _inner.CanProceedAsync(tenantId, Guid.Empty, capability.ToString(), 1, cancellationToken);
    }
}
