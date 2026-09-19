using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace DubbingPlatform.Infrastructure.Persistence;

/// <summary>
/// Model cache key factory that includes the ambient tenant id so EF Core never reuses
/// a cached model (and its tenant query filter) across tenants. Register in composition
/// roots via <c>optionsBuilder.ReplaceService&lt;IModelCacheKeyFactory, TenantModelCacheKeyFactory&gt;()</c>.
/// </summary>
public sealed class TenantModelCacheKeyFactory : IModelCacheKeyFactory
{
    public object Create(DbContext context, bool designTime)
    {
        if (context is AppDbContext app)
        {
            return (context.GetType(), app.TenantIdForCache, designTime);
        }

        return (context.GetType(), Guid.Empty, designTime);
    }
}
