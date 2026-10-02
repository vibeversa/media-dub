using DubbingPlatform.Application.Diagnostics.Dto;
using DubbingPlatform.Application.MultiTenancy;
using DubbingPlatform.Application.Options;
using DubbingPlatform.Application.Services;
using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Domain.Enums;
using DubbingPlatform.Domain.Exceptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DubbingPlatform.Application.Diagnostics;

/// <summary>
/// Read-only admin scope reads (GAP-024 / Plan B 12.19): tenants, tenant users
/// with resolved roles, effective retention policies, effective feature flags,
/// and the paged audit trail. Every read is tenant-scoped (RLS plus explicit
/// <c>tenant_id</c> filter), <c>AsNoTracking</c>, and returns ids, names,
/// counts, and timestamps only — never secrets, tokens, or payload bodies.
/// </summary>
public sealed class AdminScopeReadsService
{
    private const int MaxPageSize = 100;

    private readonly IStageExecutionContextFactory _contextFactory;
    private readonly RetentionOptions _retention;
    private readonly FeatureOptions _features;
    private readonly ILogger<AdminScopeReadsService> _logger;

    public AdminScopeReadsService(
        IStageExecutionContextFactory contextFactory,
        IOptions<RetentionOptions> retentionOptions,
        IOptions<FeatureOptions> featureOptions,
        ILogger<AdminScopeReadsService> logger)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        ArgumentNullException.ThrowIfNull(retentionOptions);
        ArgumentNullException.ThrowIfNull(featureOptions);
        ArgumentNullException.ThrowIfNull(logger);
        _contextFactory = contextFactory;
        _retention = retentionOptions.Value;
        _features = featureOptions.Value;
        _logger = logger;
    }

    /// <summary>
    /// The caller's tenant. Admin reads never cross tenants: the route resolves
    /// the tenant from the JWT claim, so this returns the one visible tenant.
    /// </summary>
    public async Task<IReadOnlyList<AdminTenantDto>> GetTenantsAsync(
        Guid tenantId,
        CancellationToken cancellationToken = default)
    {
        if (tenantId == Guid.Empty)
        {
            throw new DomainException("TenantId must not be empty.");
        }

        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            var tenant = await db.Set<Tenant>()
                .AsNoTracking()
                .Where(t => t.Id == tenantId)
                .Select(t => new AdminTenantDto(t.Id, t.Name, t.Slug))
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);

            return tenant is null ? [] : [tenant];
        }
    }

    /// <summary>
    /// Tenant users with their resolved role names (JWT-independent: membership
    /// roles from <c>project_memberships</c>, plus the tenant-admin role stored
    /// on the user row when present). Display names only — no email list, no
    /// external subject, no credential state.
    /// </summary>
    public async Task<IReadOnlyList<AdminUserDto>> GetUsersAsync(
        Guid tenantId,
        CancellationToken cancellationToken = default)
    {
        if (tenantId == Guid.Empty)
        {
            throw new DomainException("TenantId must not be empty.");
        }

        // The tenant scope must be entered before the context is created:
        // AppDbContext snapshots the ambient tenant for its global query filter.
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            var users = await db.Set<TenantUser>()
                .AsNoTracking()
                .Where(u => u.TenantId == tenantId)
                .OrderBy(u => u.DisplayName)
                .ThenBy(u => u.Id)
                .Select(u => new { u.Id, u.DisplayName })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            var membershipRoles = await db.Set<ProjectMembership>()
                .AsNoTracking()
                .Where(m => m.TenantId == tenantId)
                .Select(m => new { m.UserId, m.Role })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            var rolesByUser = new Dictionary<Guid, List<string>>();
            foreach (var membership in membershipRoles)
            {
                var key = membership.UserId;
                if (!rolesByUser.TryGetValue(key, out var list))
                {
                    list = [];
                    rolesByUser[key] = list;
                }

                list.Add(membership.Role.ToString());
            }

            var result = new List<AdminUserDto>(users.Count);
            foreach (var user in users)
            {
                var roles = rolesByUser.TryGetValue(user.Id, out var membership)
                    ? membership
                        .Where(r => !string.IsNullOrWhiteSpace(r))
                        .Distinct(StringComparer.Ordinal)
                        .OrderBy(r => r, StringComparer.Ordinal)
                        .ToList()
                    : [];
                result.Add(new AdminUserDto(user.Id, user.DisplayName, roles));
            }

            return result;
        }
    }

    /// <summary>
    /// Effective retention windows from configuration. The sweeper is the only
    /// writer; this read never exposes hold state or per-row decisions.
    /// </summary>
    public AdminRetentionPoliciesResponse GetRetentionPolicies()
    {
        var policies = new[]
        {
            new AdminRetentionPolicyDto(
                "artifact-intermediate",
                _retention.IntermediateDays,
                "Working artifacts and stage outputs before the final window."),
            new AdminRetentionPolicyDto(
                "artifact-final",
                _retention.FinalDays,
                "Final deliverables after a run completes."),
            new AdminRetentionPolicyDto(
                "audit",
                _retention.AuditDays,
                "Audit events; immutable and sweepable only after this window."),
        };

        _logger.LogDebug("Retention policies read: {Count} scopes.", policies.Length);
        return new AdminRetentionPoliciesResponse(policies);
    }

    /// <summary>
    /// Effective optional-capability flags. Optional capabilities stay disabled
    /// by default; nothing here can enable one, only report it.
    /// <c>Frozen</c> is always false: this deployment has no rollout-freeze
    /// control (and provisions no flag write route), so the panel's toggle
    /// stays a local, non-persisting affordance rather than pretending a write
    /// exists.
    /// </summary>
    public AdminFeatureFlagsResponse GetFeatureFlags()
    {
        var flags = new[]
        {
            new AdminFeatureFlagDto(
                "videoIntelligence",
                _features.VideoIntelligenceEnabled,
                "Video understanding and scene-change analysis.",
                Frozen: false),
            new AdminFeatureFlagDto(
                "lipSync",
                _features.LipSyncEnabled,
                "Lip-sync alignment for generated audio.",
                Frozen: false),
            new AdminFeatureFlagDto(
                "localInference",
                _features.LocalInferenceEnabled,
                "On-premise inference sidecar.",
                Frozen: false),
        };

        return new AdminFeatureFlagsResponse(flags);
    }

    /// <summary>
    /// Paged tenant audit trail, newest first. Payloads (<c>details_json</c>)
    /// are never returned: the row's resource identity plus timestamp is what
    /// an operator needs, and details can carry correlation ids.
    /// </summary>
    public async Task<AdminAuditEventsResponse> GetAuditEventsAsync(
        Guid tenantId,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        if (tenantId == Guid.Empty)
        {
            throw new DomainException("TenantId must not be empty.");
        }

        var safePage = page < 1 ? 1 : page;
        var safeSize = pageSize < 1 ? 20 : Math.Min(pageSize, MaxPageSize);

        // Scope first: AppDbContext snapshots the ambient tenant for its global
        // query filter at construction time.
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            var query = db.Set<AuditEvent>()
                .AsNoTracking()
                .Where(e => e.TenantId == tenantId);

            var total = await query.LongCountAsync(cancellationToken).ConfigureAwait(false);
            var rows = await query
                .OrderByDescending(e => e.CreatedAt)
                .ThenBy(e => e.Id)
                .Skip((safePage - 1) * safeSize)
                .Take(safeSize)
                .Select(e => new AdminAuditEventDto(
                    e.Id.ToString("N"),
                    e.CreatedAt,
                    e.Actor,
                    e.Action,
                    e.ResourceType,
                    e.ResourceId,
                    e.ProjectId == null ? null : e.ProjectId.Value.ToString("N")))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            var hasMore = ((long)(safePage - 1) * safeSize + rows.Count) < total;
            return new AdminAuditEventsResponse(rows, safePage, safeSize, total, hasMore);
        }
    }
}