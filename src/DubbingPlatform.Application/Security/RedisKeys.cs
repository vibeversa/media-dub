using DubbingPlatform.Domain.Exceptions;

namespace DubbingPlatform.Application.Security;

/// <summary>
/// Redis key conventions. Every key is tenant-scoped: interactive progress and
/// coordination keys lead with the tenant (<c>{tenant:N}:{rest}</c>) so a
/// keyspace scan by prefix never crosses tenants. Rate-limit buckets keep a
/// fixed <c>ratelimit:</c> prefix for operability
/// (<c>ratelimit:{tenant:N}:{provider}:{dimension}</c>, see
/// <c>RateLimiter.KeyFor</c>) — still tenant-scoped (the tenant id is always
/// embedded, never shared), only ordered prefix-first so operators can scope
/// eviction and metrics by subsystem. Storage keys follow the same rule via
/// <c>StorageKeyBuilder</c> (<c>{tenant:N}/...</c>).
/// </summary>
public static class RedisKeys
{
    public const string ProgressSegment = "progress";

    /// <summary>
    /// Builds a tenant-first key (<c>{tenant:N}:{rest}</c>). Pure.
    /// </summary>
    public static string TenantKey(Guid tenantId, string rest)
    {
        if (tenantId == Guid.Empty)
        {
            throw new DomainException("TenantId must not be empty.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(rest);
        var suffix = rest.Trim();
        if (suffix.Contains(' ', StringComparison.Ordinal)
            || suffix.Contains('\n', StringComparison.Ordinal)
            || suffix.Contains('\r', StringComparison.Ordinal))
        {
            throw new DomainException("Redis key rest must not contain whitespace.");
        }

        return string.Concat(tenantId.ToString("N"), ":", suffix);
    }

    /// <summary>
    /// Progress channel key for realtime updates
    /// (<c>{tenant:N}:progress:{project:N}</c>). Pure. The SSE endpoint polls
    /// today; a future Redis pub/sub push uses this channel without changing
    /// the event shape.
    /// </summary>
    public static string ProgressKey(Guid tenantId, Guid projectId)
    {
        if (projectId == Guid.Empty)
        {
            throw new DomainException("ProjectId must not be empty.");
        }

        return TenantKey(tenantId, string.Concat(ProgressSegment, ":", projectId.ToString("N")));
    }

    /// <summary>
    /// Whether <paramref name="key"/> is scoped to <paramref name="tenantId"/>
    /// (starts with <c>{tenant:N}:</c>). Pure. Empty ids never match.
    /// </summary>
    public static bool IsTenantScoped(string? key, Guid tenantId)
    {
        if (string.IsNullOrEmpty(key) || tenantId == Guid.Empty)
        {
            return false;
        }

        return key.StartsWith(string.Concat(tenantId.ToString("N"), ":"), StringComparison.Ordinal);
    }
}
