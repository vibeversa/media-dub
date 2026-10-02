namespace DubbingPlatform.Application.Diagnostics.Dto;

/// <summary>
/// Tenant slice for the admin tenants read (GAP-024). Ids, names, and slugs
/// only — never membership, quotas, or settings.
/// </summary>
public sealed record AdminTenantDto(Guid Id, string Name, string Slug);

/// <summary>
/// Tenant user slice for the admin users read: identity plus resolved role
/// names. Never emails as a list-wide column, tokens, or MFA state.
/// </summary>
public sealed record AdminUserDto(
    Guid Id,
    string DisplayName,
    IReadOnlyList<string> Roles);

/// <summary>
/// One effective retention policy (GAP-024). Scope + day count only; the
/// sweeper implementation and hold state are never exposed here.
/// </summary>
public sealed record AdminRetentionPolicyDto(string Scope, int RetentionDays, string Description);

/// <summary>Envelope for the retention policies read.</summary>
public sealed record AdminRetentionPoliciesResponse(IReadOnlyList<AdminRetentionPolicyDto> Policies);

/// <summary>
/// One effective feature flag (GAP-024). Optional capabilities stay disabled by
/// default; <c>Frozen</c> is true when the rollout freeze is active, so a
/// toggle write is refused with 423 instead of silently reverting.
/// </summary>
public sealed record AdminFeatureFlagDto(string Key, bool Enabled, string Description, bool Frozen);

/// <summary>Envelope for the feature flags read.</summary>
public sealed record AdminFeatureFlagsResponse(IReadOnlyList<AdminFeatureFlagDto> Flags);

/// <summary>
/// One audit row for the admin audit read (GAP-024). Actor, action, resource,
/// and timestamp only — <c>detailsJson</c> payloads are never returned wholesale
/// because they can carry correlation ids and resource ids, not secrets.
/// </summary>
public sealed record AdminAuditEventDto(
    string Id,
    DateTimeOffset Timestamp,
    string Actor,
    string Action,
    string ResourceType,
    string ResourceId,
    string? ProjectId);

/// <summary>
/// Paged envelope for the admin audit read. <c>Total</c>/<c>HasMore</c> mirror
/// the platform pagination envelope (page, pageSize, total, hasMore).
/// </summary>
public sealed record AdminAuditEventsResponse(
    IReadOnlyList<AdminAuditEventDto> Items,
    int Page,
    int PageSize,
    long Total,
    bool HasMore);