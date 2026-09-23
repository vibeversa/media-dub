using System.Security.Claims;
using DubbingPlatform.Api.Errors;
using DubbingPlatform.Api.Middleware;
using DubbingPlatform.Api.Models;
using DubbingPlatform.Application.Authorization;
using DubbingPlatform.Application.Common;
using DubbingPlatform.Application.Dashboard;
using DubbingPlatform.Application.Diagnostics;
using DubbingPlatform.Application.Diagnostics.Dto;
using DubbingPlatform.Application.Exceptions;
using DubbingPlatform.Application.MultiTenancy;
using DubbingPlatform.Application.Options;
using DubbingPlatform.Application.Services;
using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Domain.Exceptions;
using DubbingPlatform.Domain.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DubbingPlatform.Api.Controllers;

/// <summary>
/// Operator diagnostics (Task 013, read-only). Every call appends an
/// <c>admin.access</c> audit event. Responses never expose secrets:
/// lease tokens, API keys, media bytes, prompt text, and review payloads are
/// omitted; only ids, statuses, counts, hashes, and timestamps are returned.
/// Elevated authz: every route requires <c>admin.manage</c> or
/// <c>diagnostics.view</c> — JWT <c>TenantAdmin</c>/<c>Service</c>/
/// <c>Operator</c> roles pass immediately, otherwise the caller's resolved
/// permissions must contain <c>admin.manage</c> or <c>diagnostics.view</c>
/// (ProjectOwner membership grants <c>diagnostics.view</c>); the Task 005
/// service guard re-checks membership (defense in depth). Non-viewers get 403;
/// cross-tenant per-resource reads get 404 (no existence leak, matching the
/// 013 error-mapping principle). Tenant-level aggregates have no cross-tenant
/// case. New Task 013 routes paginate leases/orphans/DLQ (default 50, max 200);
/// legacy routes keep <c>?page=&amp;pageSize=</c> (max 100). Unknown admin
/// sub-paths return 404 with an <c>ADMIN_ROUTE_UNKNOWN</c> marker on the
/// public <c>NOT_FOUND</c> code (catalog stable). DLQ/leases/orphans empty
/// return 200 zero-shapes, never 404.
/// </summary>
[ApiController]
[Route("api/v1/admin")]
[Authorize]
[ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
[ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status403Forbidden)]
public sealed class AdminController : ControllerBase
{
    private const int DiagnosticsDefaultPageSize = 50;

    private const int DiagnosticsMaxPageSize = 200;

    private readonly AuditService _audit;
    private readonly IStageExecutionContextFactory _contextFactory;
    private readonly QueueDiagnosticsService _queues;
    private readonly LeaseOrphanService _leases;
    private readonly ReviewBacklogService _reviews;
    private readonly ProviderHealthQueryService _providers;
    private readonly IPermissionResolver _permissions;
    private readonly QuotaOptions _quota;
    private readonly DashboardService _dashboard;

    public AdminController(
        AuditService audit,
        IStageExecutionContextFactory contextFactory,
        QueueDiagnosticsService queues,
        LeaseOrphanService leases,
        ReviewBacklogService reviews,
        ProviderHealthQueryService providers,
        IPermissionResolver permissions,
        IOptions<QuotaOptions> quotaOptions,
        DashboardService dashboard)
    {
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(contextFactory);
        ArgumentNullException.ThrowIfNull(queues);
        ArgumentNullException.ThrowIfNull(leases);
        ArgumentNullException.ThrowIfNull(reviews);
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(permissions);
        ArgumentNullException.ThrowIfNull(quotaOptions);
        ArgumentNullException.ThrowIfNull(dashboard);
        _audit = audit;
        _contextFactory = contextFactory;
        _queues = queues;
        _leases = leases;
        _reviews = reviews;
        _providers = providers;
        _permissions = permissions;
        _quota = quotaOptions.Value;
        _dashboard = dashboard;
    }

    [HttpGet("status")]
    [Authorize(Policy = AuthPolicies.RequireTenantAdmin)]
    [ProducesResponseType(typeof(AdminStatusResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> Status(CancellationToken cancellationToken)
    {
        await _audit.LogAsync(
            User.GetTenantId(), null, User.GetSubject(), "admin.access",
            "admin", "status", null, cancellationToken).ConfigureAwait(false);
        return Ok(new AdminStatusResponse("ok", DateTimeOffset.UtcNow));
    }

    /// <summary>
    /// Stage execution detail (no lease token).
    /// </summary>
    [HttpGet("stages/{execId}")]
    [Authorize(Policy = AuthPolicies.RequireTenantAdmin)]
    [ProducesResponseType(typeof(AdminStageResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetStage([FromRoute] string execId, CancellationToken cancellationToken)
    {
        var tenantId = User.GetTenantId();
        var id = ParseExecutionId(execId, PublicIdMapper.StageExecutionPrefix, "stage execution");
        var execution = await LoadOwnedAsync<StageExecution>(id, tenantId, cancellationToken).ConfigureAwait(false);
        await _audit.LogAsync(
            tenantId, execution.ProjectId, User.GetSubject(), "admin.access",
            "admin", string.Concat("stages/", id.ToString("N")), null, cancellationToken).ConfigureAwait(false);
        return Ok(AdminStageResponse.From(execution));
    }

    /// <summary>
    /// Provider execution detail (hashes and ids only, no secrets).
    /// </summary>
    [HttpGet("provider-executions/{id}")]
    [Authorize(Policy = AuthPolicies.RequireTenantAdmin)]
    [ProducesResponseType(typeof(AdminProviderExecutionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetProviderExecution([FromRoute] string id, CancellationToken cancellationToken)
    {
        var tenantId = User.GetTenantId();
        var executionId = ParseExecutionId(id, PublicIdMapper.ProviderExecutionPrefix, "provider execution");
        var execution = await LoadOwnedAsync<ProviderExecution>(executionId, tenantId, cancellationToken).ConfigureAwait(false);
        await _audit.LogAsync(
            tenantId, execution.ProjectId, User.GetSubject(), "admin.access",
            "admin", string.Concat("provider-executions/", executionId.ToString("N")), null, cancellationToken).ConfigureAwait(false);
        return Ok(AdminProviderExecutionResponse.From(execution));
    }

    /// <summary>
    /// DLQ summary. Broker queue depth remains the alerting source of truth;
    /// this endpoint is DB-free (always 200 for authorized operators) and
    /// documents queues, metric names, and SLO thresholds for runbook checks.
    /// </summary>
    [HttpGet("dlq/summary")]
    [Authorize(Policy = AuthPolicies.RequireTenantAdmin)]
    [ProducesResponseType(typeof(AdminDlqSummaryResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetDlqSummary(CancellationToken cancellationToken)
    {
        var tenantId = User.GetTenantId();
        await _audit.LogAsync(
            tenantId, null, User.GetSubject(), "admin.access",
            "admin", "dlq/summary", null, cancellationToken).ConfigureAwait(false);
        return Ok(AdminDlqSummaryResponse.Default());
    }

    /// <summary>
    /// Lease status: Running executions with lease owner/expiry (no tokens).
    /// Paginated, max 100 per page.
    /// </summary>
    [HttpGet("leases/status")]
    [Authorize(Policy = AuthPolicies.RequireTenantAdmin)]
    [ProducesResponseType(typeof(PaginatedResult<AdminLeaseResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetLeases([FromQuery] PaginationParams? query, CancellationToken cancellationToken)
    {
        var tenantId = User.GetTenantId();
        query ??= new PaginationParams();
        query.Normalize();
        var (page, pageSize) = query.Normalized();
        await _audit.LogAsync(
            tenantId, null, User.GetSubject(), "admin.access",
            "admin", "leases/status", null, cancellationToken).ConfigureAwait(false);
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            var scoped = db.Set<StageExecution>().AsNoTracking().Where(e => e.TenantId == tenantId);
            var total = await scoped.LongCountAsync(cancellationToken).ConfigureAwait(false);
            var items = await scoped
                .OrderBy(e => e.LeaseExpiresAt)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            var responses = items.Select(AdminLeaseResponse.From).ToList();
            return Ok(PaginatedResult<AdminLeaseResponse>.Create(responses, page, pageSize, total));
        }
    }

    /// <summary>
    /// Review backlog: Open reviews oldest-first (no payload text). Paginated,
    /// max 100 per page.
    /// </summary>
    [HttpGet("reviews/backlog")]
    [Authorize(Policy = AuthPolicies.RequireTenantAdmin)]
    [ProducesResponseType(typeof(PaginatedResult<AdminReviewBacklogResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetReviewBacklog([FromQuery] PaginationParams? query, CancellationToken cancellationToken)
    {
        var tenantId = User.GetTenantId();
        query ??= new PaginationParams();
        query.Normalize();
        var (page, pageSize) = query.Normalized();
        await _audit.LogAsync(
            tenantId, null, User.GetSubject(), "admin.access",
            "admin", "reviews/backlog", null, cancellationToken).ConfigureAwait(false);
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            var scoped = db.Set<ReviewItem>().AsNoTracking().Where(r => r.TenantId == tenantId && r.Status == Domain.Enums.ReviewStatus.Open);
            var total = await scoped.LongCountAsync(cancellationToken).ConfigureAwait(false);
            var items = await scoped
                .OrderBy(r => r.CreatedAt)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            var responses = items.Select(AdminReviewBacklogResponse.From).ToList();
            return Ok(PaginatedResult<AdminReviewBacklogResponse>.Create(responses, page, pageSize, total));
        }
    }

    /// <summary>
    /// Tenant usage aggregate (Task 013): storage, month cost, daily quota
    /// remaining, active runs, pending reviews. Thin wrapper over the
    /// dashboard summary; counts and bytes only, never bodies or secrets.
    /// </summary>
    [HttpGet("usage")]
    [ProducesResponseType(typeof(AdminUsageResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetUsage(CancellationToken cancellationToken)
    {
        var tenantId = User.GetTenantId();
        var userId = RequireUserId(User);
        await RequireElevatedAsync(tenantId, userId, cancellationToken).ConfigureAwait(false);
        var correlationId = CorrelationIdMiddleware.GetCorrelationId(HttpContext);
        var summary = await _dashboard.GetSummaryAsync(tenantId, cancellationToken).ConfigureAwait(false);
        await _audit.LogAsync(
            tenantId, null, User.GetSubject(), "admin.access",
            "admin", "usage", null, cancellationToken).ConfigureAwait(false);
        return Ok(new AdminUsageResponse(
            correlationId,
            summary.Storage.UsedBytes,
            summary.Storage.QuotaBytes,
            summary.Cost.MonthToDate,
            summary.Quota.Remaining,
            summary.Backlog.RunningJobs,
            summary.Backlog.PendingReviews,
            summary.ProjectCounts.Total));
    }

    /// <summary>
    /// Tenant quota limits (Task 013): frozen <c>Quota</c> options. Limits only,
    /// never secrets.
    /// </summary>
    [HttpGet("quotas")]
    [ProducesResponseType(typeof(AdminQuotasResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetQuotas(CancellationToken cancellationToken)
    {
        var tenantId = User.GetTenantId();
        var userId = RequireUserId(User);
        await RequireElevatedAsync(tenantId, userId, cancellationToken).ConfigureAwait(false);
        var correlationId = CorrelationIdMiddleware.GetCorrelationId(HttpContext);
        await _audit.LogAsync(
            tenantId, null, User.GetSubject(), "admin.access",
            "admin", "quotas", null, cancellationToken).ConfigureAwait(false);
        return Ok(new AdminQuotasResponse(
            correlationId,
            _quota.MaxActiveProjects,
            _quota.MaxProjectsPerDay,
            _quota.MaxCostPerProject,
            _quota.MaxCostPerSegment,
            _quota.MaxSegmentCount,
            _quota.MaxStorageBytes,
            _quota.MaxConcurrentStagesPerTenant));
    }

    /// <summary>
    /// Provider health snapshots (Task 013): per-provider status, p95 latency,
    /// error rate, last success, routes, circuit state. Secret-free.
    /// </summary>
    [HttpGet("provider-health")]
    [ProducesResponseType(typeof(IReadOnlyList<ProviderHealthDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetProviderHealth(CancellationToken cancellationToken)
    {
        var tenantId = User.GetTenantId();
        var userId = RequireUserId(User);
        await RequireElevatedAsync(tenantId, userId, cancellationToken).ConfigureAwait(false);
        var correlationId = CorrelationIdMiddleware.GetCorrelationId(HttpContext);
        var snapshots = await _providers.GetProviderHealthAsync(tenantId, userId, correlationId, cancellationToken).ConfigureAwait(false);
        await _audit.LogAsync(
            tenantId, null, User.GetSubject(), "admin.access",
            "admin", "provider-health", null, cancellationToken).ConfigureAwait(false);
        return Ok(snapshots);
    }

    /// <summary>
    /// Provider routes (Task 013): capability to provider with priority and
    /// enablement. Route names only, never keys.
    /// </summary>
    [HttpGet("provider-routes")]
    [ProducesResponseType(typeof(IReadOnlyList<ProviderRouteDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetProviderRoutes(CancellationToken cancellationToken)
    {
        var tenantId = User.GetTenantId();
        var userId = RequireUserId(User);
        await RequireElevatedAsync(tenantId, userId, cancellationToken).ConfigureAwait(false);
        var correlationId = CorrelationIdMiddleware.GetCorrelationId(HttpContext);
        var routes = await _providers.GetProviderRoutesAsync(tenantId, userId, correlationId, cancellationToken).ConfigureAwait(false);
        await _audit.LogAsync(
            tenantId, null, User.GetSubject(), "admin.access",
            "admin", "provider-routes", null, cancellationToken).ConfigureAwait(false);
        return Ok(routes);
    }

    /// <summary>
    /// Queue depths (Task 013): pending depth per queue across the frozen
    /// taxonomy. Counts only, never bodies.
    /// </summary>
    [HttpGet("diagnostics/queues")]
    [ProducesResponseType(typeof(IReadOnlyList<QueueDepthDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetQueueDepths(CancellationToken cancellationToken)
    {
        var tenantId = User.GetTenantId();
        var userId = RequireUserId(User);
        await RequireElevatedAsync(tenantId, userId, cancellationToken).ConfigureAwait(false);
        var correlationId = CorrelationIdMiddleware.GetCorrelationId(HttpContext);
        var depths = await _queues.GetQueueDepthsAsync(tenantId, userId, correlationId, cancellationToken).ConfigureAwait(false);
        await _audit.LogAsync(
            tenantId, null, User.GetSubject(), "admin.access",
            "admin", "diagnostics/queues", null, cancellationToken).ConfigureAwait(false);
        return Ok(depths);
    }

    /// <summary>
    /// DLQ summary (Task 013): depth, oldest age, top reasons. Empty DLQ is a
    /// 200 zero-shape. Reasons paginated (<c>?page=&amp;pageSize=</c>, default 50,
    /// max 200); depth/oldest stay totals.
    /// </summary>
    [HttpGet("diagnostics/dlq")]
    [ProducesResponseType(typeof(DlqSummaryDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetDiagnosticsDlq(
        [FromQuery] int? page,
        [FromQuery] int? pageSize,
        CancellationToken cancellationToken)
    {
        var tenantId = User.GetTenantId();
        var userId = RequireUserId(User);
        await RequireElevatedAsync(tenantId, userId, cancellationToken).ConfigureAwait(false);
        var correlationId = CorrelationIdMiddleware.GetCorrelationId(HttpContext);
        var summary = await _queues.GetDlqSummaryAsync(tenantId, userId, correlationId, cancellationToken).ConfigureAwait(false);
        await _audit.LogAsync(
            tenantId, null, User.GetSubject(), "admin.access",
            "admin", "diagnostics/dlq", null, cancellationToken).ConfigureAwait(false);
        var (safePage, safeSize) = NormalizeDiagnosticsPage(page, pageSize);
        var sliced = summary.TopReasons
            .Skip((safePage - 1) * safeSize)
            .Take(safeSize)
            .ToList();
        return Ok(new DlqSummaryDto(
            summary.CorrelationId,
            summary.Depth,
            summary.OldestEnqueuedAt,
            summary.OldestEntryAge,
            sliced));
    }

    /// <summary>
    /// Stale leases (Task 013): running leases past TTL with expired heartbeat.
    /// Paginated (<c>?page=&amp;pageSize=</c>, default 50, max 200). Empty is a
    /// 200 zero page. Never lease tokens.
    /// </summary>
    [HttpGet("diagnostics/leases")]
    [ProducesResponseType(typeof(PaginatedResult<StaleLeaseDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetDiagnosticsLeases(
        [FromQuery] int? page,
        [FromQuery] int? pageSize,
        CancellationToken cancellationToken)
    {
        var tenantId = User.GetTenantId();
        var userId = RequireUserId(User);
        await RequireElevatedAsync(tenantId, userId, cancellationToken).ConfigureAwait(false);
        var correlationId = CorrelationIdMiddleware.GetCorrelationId(HttpContext);
        var stale = await _leases.GetStaleLeasesAsync(tenantId, userId, correlationId, cancellationToken).ConfigureAwait(false);
        await _audit.LogAsync(
            tenantId, null, User.GetSubject(), "admin.access",
            "admin", "diagnostics/leases", null, cancellationToken).ConfigureAwait(false);
        var (safePage, safeSize) = NormalizeDiagnosticsPage(page, pageSize);
        var items = stale
            .Skip((safePage - 1) * safeSize)
            .Take(safeSize)
            .ToList();
        return Ok(PaginatedResult<StaleLeaseDto>.Create(items, safePage, safeSize, stale.Count));
    }

    /// <summary>
    /// Orphan artifacts (Task 013): content objects with no owning artifact,
    /// media asset, or generated-audio reference. Cursor pagination
    /// (<c>?pageSize=</c> default 50 max 200, opaque <c>?cursor=</c>); empty is
    /// a 200 zero page. Never storage keys or bytes.
    /// </summary>
    [HttpGet("diagnostics/orphans")]
    [ProducesResponseType(typeof(OrphanArtifactPage), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetDiagnosticsOrphans(
        [FromQuery] int? pageSize,
        [FromQuery] string? cursor,
        CancellationToken cancellationToken)
    {
        var tenantId = User.GetTenantId();
        var userId = RequireUserId(User);
        await RequireElevatedAsync(tenantId, userId, cancellationToken).ConfigureAwait(false);
        var correlationId = CorrelationIdMiddleware.GetCorrelationId(HttpContext);
        var take = NormalizeDiagnosticsPageSize(pageSize);
        var page = await _leases.GetOrphanArtifactsAsync(tenantId, userId, take, cursor, correlationId, cancellationToken).ConfigureAwait(false);
        await _audit.LogAsync(
            tenantId, null, User.GetSubject(), "admin.access",
            "admin", "diagnostics/orphans", null, cancellationToken).ConfigureAwait(false);
        return Ok(page);
    }

    /// <summary>
    /// Review backlog (Task 013): status/severity counts, oldest wait, per-project
    /// open breakdown. Counts and ids only, never payload text.
    /// </summary>
    [HttpGet("diagnostics/review-backlog")]
    [ProducesResponseType(typeof(ReviewBacklogDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetDiagnosticsReviewBacklog(CancellationToken cancellationToken)
    {
        var tenantId = User.GetTenantId();
        var userId = RequireUserId(User);
        await RequireElevatedAsync(tenantId, userId, cancellationToken).ConfigureAwait(false);
        var correlationId = CorrelationIdMiddleware.GetCorrelationId(HttpContext);
        var backlog = await _reviews.GetBacklogAsync(tenantId, userId, correlationId, cancellationToken).ConfigureAwait(false);
        await _audit.LogAsync(
            tenantId, null, User.GetSubject(), "admin.access",
            "admin", "diagnostics/review-backlog", null, cancellationToken).ConfigureAwait(false);
        return Ok(backlog);
    }

    /// <summary>
    /// Unknown admin sub-paths return 404 with an <c>ADMIN_ROUTE_UNKNOWN</c>
    /// marker on the public <c>NOT_FOUND</c> code (never a generic page).
    /// </summary>
    [HttpGet("{*unmatched}")]
    [HttpPost("{*unmatched}")]
    [HttpPut("{*unmatched}")]
    [HttpDelete("{*unmatched}")]
    [HttpPatch("{*unmatched}")]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public IActionResult UnknownRoute([FromRoute] string? unmatched)
    {
        throw new NotFoundException($"{ApiError.AdminRouteUnknownMarker}: admin route '{unmatched}' was not found.");
    }

    private async Task RequireElevatedAsync(Guid tenantId, Guid userId, CancellationToken cancellationToken)
    {
        if (User.HasAnyRole([Roles.TenantAdmin, Roles.Service, DiagnosticsAccessPolicy.OperatorRole]))
        {
            return;
        }

        var resolved = await _permissions.ResolveAsync(tenantId, userId, cancellationToken).ConfigureAwait(false);
        if (resolved.Contains(Permissions.AdminManage) || resolved.Contains(Permissions.DiagnosticsView))
        {
            return;
        }

        throw new ForbiddenException($"{DiagnosticsAccessPolicy.ForbiddenMarker}: user '{userId:D}' is not authorized for diagnostics in tenant '{tenantId:D}'.");
    }

    private static Guid RequireUserId(ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(user);
        var raw = user.FindFirst(Application.Authorization.ClaimTypes.Subject)?.Value;
        if (string.IsNullOrWhiteSpace(raw) || !Guid.TryParse(raw.Trim(), out var userId) || userId == Guid.Empty)
        {
            throw new UnauthorizedAccessException("The 'sub' claim must be a user id.");
        }

        return userId;
    }

    private static (int Page, int PageSize) NormalizeDiagnosticsPage(int? page, int? pageSize)
    {
        var safePage = page.HasValue && page.Value >= 1 ? page.Value : 1;
        return (safePage, NormalizeDiagnosticsPageSize(pageSize));
    }

    private static int NormalizeDiagnosticsPageSize(int? pageSize)
    {
        if (!pageSize.HasValue || pageSize.Value <= 0)
        {
            return DiagnosticsDefaultPageSize;
        }

        return Math.Min(pageSize.Value, DiagnosticsMaxPageSize);
    }

    private async Task<T> LoadOwnedAsync<T>(Guid id, Guid tenantId, CancellationToken cancellationToken)
        where T : class
    {
        T? entity;
        using (TenantContext.BeginMaintenanceScope())
        {
            using var db = _contextFactory.CreateDbContext();
            entity = await db.Set<T>().AsNoTracking().FirstOrDefaultAsync(e => EF.Property<Guid>(e, "Id") == id, cancellationToken).ConfigureAwait(false);
        }

        if (entity is null)
        {
            throw new NotFoundException($"Resource '{id}' was not found.");
        }

        var tenantProp = typeof(T).GetProperty("TenantId");
        var actual = tenantProp is null ? Guid.Empty : (Guid)(tenantProp.GetValue(entity) ?? Guid.Empty);
        if (actual != tenantId)
        {
            throw new NotFoundException($"Resource '{id}' was not found.");
        }

        return entity;
    }

    private static Guid ParseExecutionId(string? raw, string expectedPrefix, string kind)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new DomainException($"{kind} id must not be empty.");
        }

        var trimmed = raw.Trim();
        if (Guid.TryParse(trimmed, out var guid) && guid != Guid.Empty)
        {
            return guid;
        }

        var compact = trimmed.Replace("-", string.Empty, StringComparison.Ordinal);
        if (Guid.TryParseExact(compact, "N", out var compactGuid) && compactGuid != Guid.Empty)
        {
            return compactGuid;
        }

        try
        {
            var (prefix, id) = PublicIdMapper.FromPublic(trimmed);
            if (!string.Equals(prefix, expectedPrefix, StringComparison.Ordinal))
            {
                throw new DomainException($"{kind} id has an unexpected prefix '{prefix}'.");
            }

            return id;
        }
        catch (DomainException ex) when (ex.Message.Contains("Unknown", StringComparison.Ordinal) || ex.Message.Contains("unknown", StringComparison.Ordinal))
        {
            throw new DomainException($"{kind} id '{trimmed}' is not a valid identifier.");
        }
    }
}

/// <summary>
/// Admin status response body.
/// </summary>
public sealed record AdminStatusResponse(string Status, DateTimeOffset Time);

/// <summary>
/// Tenant usage aggregate (counts and bytes only).
/// </summary>
public sealed record AdminUsageResponse(
    string CorrelationId,
    long StorageUsedBytes,
    long StorageQuotaBytes,
    double MonthCostUsd,
    int ProjectsTodayRemaining,
    int ActiveRuns,
    int PendingReviews,
    int TotalProjects);

/// <summary>
/// Tenant quota limits (frozen Quota options).
/// </summary>
public sealed record AdminQuotasResponse(
    string CorrelationId,
    int MaxActiveProjects,
    int MaxProjectsPerDay,
    double MaxCostPerProject,
    double MaxCostPerSegment,
    int MaxSegmentCount,
    long MaxStorageBytes,
    int MaxConcurrentStagesPerTenant);

/// <summary>
/// Stage execution diagnostics (lease token omitted).
/// </summary>
public sealed record AdminStageResponse(
    string Id,
    string ProjectId,
    string RunId,
    string StageType,
    string ScopeType,
    string ScopeId,
    int Attempt,
    string Status,
    string LeaseOwner,
    DateTimeOffset LeaseExpiresAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    string? ErrorCode)
{
    public static AdminStageResponse From(StageExecution execution)
    {
        ArgumentNullException.ThrowIfNull(execution);
        return new AdminStageResponse(
            PublicIdMapper.ToPublic(execution.Id, PublicIdMapper.StageExecutionPrefix),
            PublicIdMapper.ToPublic(execution.ProjectId, PublicIdMapper.DubbingProjectPrefix),
            PublicIdMapper.ToPublic(execution.ProcessingRunId, PublicIdMapper.ProcessingRunPrefix),
            execution.StageType.ToString(),
            execution.ScopeType.ToString(),
            execution.ScopeId,
            execution.Attempt,
            execution.Status.ToString(),
            execution.LeaseOwner,
            execution.LeaseExpiresAt,
            execution.StartedAt,
            execution.CompletedAt,
            execution.ErrorCode);
    }
}

/// <summary>
/// Provider execution diagnostics (ids/hashes only).
/// </summary>
public sealed record AdminProviderExecutionResponse(
    string Id,
    string ProjectId,
    string RunId,
    string Provider,
    string Capability,
    string Model,
    int Attempt,
    long LatencyMs,
    string Outcome,
    DateTimeOffset CreatedAt)
{
    public static AdminProviderExecutionResponse From(ProviderExecution execution)
    {
        ArgumentNullException.ThrowIfNull(execution);
        return new AdminProviderExecutionResponse(
            PublicIdMapper.ToPublic(execution.Id, PublicIdMapper.ProviderExecutionPrefix),
            PublicIdMapper.ToPublic(execution.ProjectId, PublicIdMapper.DubbingProjectPrefix),
            PublicIdMapper.ToPublic(execution.ProcessingRunId, PublicIdMapper.ProcessingRunPrefix),
            execution.Provider.ToString(),
            execution.Capability.ToString(),
            execution.Model,
            execution.Attempt,
            execution.LatencyMs,
            execution.Outcome.ToString(),
            execution.CreatedAt);
    }
}

/// <summary>
/// DLQ summary (DB-free operator guidance).
/// </summary>
public sealed record AdminDlqSummaryResponse(
    IReadOnlyList<string> Queues,
    IReadOnlyList<string> Metrics,
    string Slo,
    string Guidance)
{
    public static AdminDlqSummaryResponse Default()
    {
        return new AdminDlqSummaryResponse(
            ["_skipped", "_error"],
            ["dlq.depth", "messaging.schema_mismatch_total", "messaging.cross_tenant_reject_total"],
            "DLQ depth 0 sustained; alert pages when dlq.depth > 0 for 5m.",
            "Check broker _skipped/_error depth, then GET leases/status and reviews/backlog; follow docs/runbooks/dlq.md.");
    }
}

/// <summary>
/// Lease status row (no token).
/// </summary>
public sealed record AdminLeaseResponse(
    string Id,
    string StageType,
    string Status,
    string LeaseOwner,
    DateTimeOffset LeaseExpiresAt,
    int Attempt)
{
    public static AdminLeaseResponse From(StageExecution execution)
    {
        ArgumentNullException.ThrowIfNull(execution);
        return new AdminLeaseResponse(
            PublicIdMapper.ToPublic(execution.Id, PublicIdMapper.StageExecutionPrefix),
            execution.StageType.ToString(),
            execution.Status.ToString(),
            execution.LeaseOwner,
            execution.LeaseExpiresAt,
            execution.Attempt);
    }
}

/// <summary>
/// Review backlog row (no payload text).
/// </summary>
public sealed record AdminReviewBacklogResponse(
    string Id,
    string ProjectId,
    string Reason,
    string Status,
    DateTimeOffset CreatedAt)
{
    public static AdminReviewBacklogResponse From(ReviewItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return new AdminReviewBacklogResponse(
            PublicIdMapper.ToPublic(item.Id, PublicIdMapper.ReviewItemPrefix),
            PublicIdMapper.ToPublic(item.ProjectId, PublicIdMapper.DubbingProjectPrefix),
            item.Reason,
            item.Status.ToString(),
            item.CreatedAt);
    }
}
