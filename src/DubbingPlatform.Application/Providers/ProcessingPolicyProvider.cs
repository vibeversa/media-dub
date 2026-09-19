using DubbingPlatform.Application.MultiTenancy;
using DubbingPlatform.Application.Services;
using DubbingPlatform.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DubbingPlatform.Application.Providers;

/// <summary>
/// Singleton policy loader. Creates a scope per call and reads the tenant row
/// under that tenant (RLS + filters apply); missing rows return null.
/// DB failures return null (fail closed to mock-only) rather than throwing.
/// </summary>
public sealed class ProcessingPolicyProvider : IProcessingPolicyProvider
{
    private readonly IServiceScopeFactory _scopes;

    public ProcessingPolicyProvider(IServiceScopeFactory scopes)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        _scopes = scopes;
    }

    public async Task<ProcessingPolicy?> GetAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        if (tenantId == Guid.Empty)
        {
            throw new Domain.Exceptions.DomainException("TenantId must not be empty.");
        }

        try
        {
            using var scope = _scopes.CreateScope();
            var factories = scope.ServiceProvider.GetService(typeof(IStageExecutionContextFactory)) as IStageExecutionContextFactory;
            if (factories is null)
            {
                return null;
            }

            using (TenantContext.BeginScope(tenantId))
            {
                using var db = factories.CreateDbContext();
                return await db.Set<ProcessingPolicy>()
                    .AsNoTracking()
                    .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            return null;
        }
    }
}
