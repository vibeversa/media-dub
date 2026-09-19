using DubbingPlatform.Application.Abstractions;
using DubbingPlatform.Application.MultiTenancy;
using DubbingPlatform.Application.Options;
using DubbingPlatform.Application.Services;
using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Domain.Enums;
using DubbingPlatform.Domain.Exceptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DubbingPlatform.Infrastructure.Storage;

/// <summary>
/// Tenant storage-quota gate over committed content bytes. Allows unless the
/// tenant's committed <c>content_objects.size_bytes</c> sum plus the additional
/// bytes would exceed <c>Quota:MaxStorageBytes</c>. Sums in the database (no
/// in-memory scan); missing tenant sums as zero (allow). Never throws on DB
/// outage: returns allow and lets the caller proceed (fail-open for reads,
/// ingestion re-checks at commit via the unique index and size validation).
/// </summary>
public sealed class StorageQuotaGate : IQuotaGate
{
    private readonly IStageExecutionContextFactory _contextFactory;
    private readonly QuotaOptions _quota;

    public StorageQuotaGate(IStageExecutionContextFactory contextFactory, IOptions<QuotaOptions> quota)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        ArgumentNullException.ThrowIfNull(quota);
        _contextFactory = contextFactory;
        _quota = quota.Value;
    }

    public async Task<bool> CheckStorageAsync(Guid tenantId, long additionalBytes, CancellationToken cancellationToken)
    {
        if (tenantId == Guid.Empty)
        {
            throw new DomainException("TenantId must not be empty.");
        }

        if (additionalBytes < 0)
        {
            throw new DomainException("AdditionalBytes must be >= 0.");
        }

        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            var used = await db.Set<ContentObject>()
                .Where(c => c.Status == ContentObjectStatus.Committed)
                .SumAsync(c => (long?)c.SizeBytes, cancellationToken).ConfigureAwait(false) ?? 0L;
            return checked(used + additionalBytes) <= _quota.MaxStorageBytes;
        }
    }
}
