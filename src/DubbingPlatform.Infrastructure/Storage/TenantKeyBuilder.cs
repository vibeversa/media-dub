using DubbingPlatform.Application.Storage;
using DubbingPlatform.Domain.Exceptions;

namespace DubbingPlatform.Infrastructure.Storage;

/// <summary>
/// Tenant-scoped storage key builder (Task 037, R2).
/// Thin facade over <see cref="StorageKeyBuilder"/> guaranteeing the
/// <c>tenant/{tenantId}/...</c> prefix convention: every key starts with the
/// caller's tenant id in <c>N</c> form, and <see cref="AssertOwnedBy"/>
/// rejects cross-tenant keys fail-closed before any storage call.
/// </summary>
public static class TenantKeyBuilder
{
    /// <summary>
    /// Builds a tenant-prefixed storage key for the caller's tenant. Pure.
    /// Delegates to <see cref="StorageKeyBuilder.BuildKey"/> so key shape,
    /// traversal rejection, and hash rules stay in one place.
    /// </summary>
    public static string BuildTenantKey(
        Guid tenantId,
        Guid projectId,
        Guid processingRunId,
        string stageType,
        string artifactType,
        string contentHash,
        string? extension)
    {
        if (tenantId == Guid.Empty)
        {
            throw new DomainException("TenantId must not be empty.");
        }

        return StorageKeyBuilder.BuildKey(
            tenantId, projectId, processingRunId, stageType, artifactType, contentHash, extension);
    }

    /// <summary>
    /// Whether <paramref name="storageKey"/> is owned by
    /// <paramref name="tenantId"/> (prefix <c>{tenant:N}/</c>). Pure.
    /// Empty tenants or keys never match (fail closed).
    /// </summary>
    public static bool IsOwnedBy(Guid tenantId, string? storageKey)
    {
        if (tenantId == Guid.Empty || string.IsNullOrEmpty(storageKey))
        {
            return false;
        }

        var prefix = string.Concat(tenantId.ToString("N"), "/");
        return storageKey.StartsWith(prefix, StringComparison.Ordinal);
    }

    /// <summary>
    /// Throws <see cref="DomainException"/> unless
    /// <paramref name="storageKey"/> is owned by <paramref name="tenantId"/>.
    /// Validates key shape first via <see cref="StorageKeyBuilder.ValidateKey"/>
    /// so traversal/absolute keys fail closed even with a matching prefix.
    /// </summary>
    public static void AssertOwnedBy(Guid tenantId, string storageKey)
    {
        if (tenantId == Guid.Empty)
        {
            throw new DomainException("TenantId must not be empty.");
        }

        StorageKeyBuilder.ValidateKey(storageKey);
        if (!IsOwnedBy(tenantId, storageKey))
        {
            throw new DomainException("StorageKey does not belong to the current tenant.");
        }
    }
}
