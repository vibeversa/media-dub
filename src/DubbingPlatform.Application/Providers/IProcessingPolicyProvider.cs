using DubbingPlatform.Domain.Entities;

namespace DubbingPlatform.Application.Providers;

/// <summary>
/// Tenant policy source. Production implementation loads the tenant's
/// <see cref="ProcessingPolicy"/> row; null means no row (fail closed to
/// mock-only in <see cref="PolicyChecker"/>).
/// </summary>
public interface IProcessingPolicyProvider
{
    Task<ProcessingPolicy?> GetAsync(Guid tenantId, CancellationToken cancellationToken = default);
}
