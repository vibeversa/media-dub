namespace DubbingPlatform.Application.Abstractions;

/// <summary>
/// Tenant storage-quota gate. The stub implementation allows unless the
/// tenant's committed bytes plus <paramref name="additionalBytes"/> would
/// exceed <c>Quota:MaxStorageBytes</c>. Duration quotas are enforced via
/// <c>Media:MaxDurationMs</c> in validation (not via this gate).
/// </summary>
public interface IQuotaGate
{
    Task<bool> CheckStorageAsync(Guid tenantId, long additionalBytes, CancellationToken cancellationToken);
}
