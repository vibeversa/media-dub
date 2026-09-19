using DubbingPlatform.Api.Middleware;
using DubbingPlatform.Api.Models;
using DubbingPlatform.Application.Authorization;
using DubbingPlatform.Application.Common;
using DubbingPlatform.Application.Exceptions;
using DubbingPlatform.Application.MultiTenancy;
using DubbingPlatform.Application.Services;
using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Domain.Exceptions;
using DubbingPlatform.Domain.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DubbingPlatform.Api.Controllers;

/// <summary>
/// Operator diagnostics (Service/TenantAdmin only, read-only). Every call
/// appends an <c>admin.access</c> audit event. Responses never expose secrets:
/// lease tokens, API keys, media bytes, prompt text, and review payloads are
/// omitted; only ids, statuses, counts, hashes, and timestamps are returned.
/// Tenant isolation is enforced by a maintenance-scope load followed by an
/// explicit tenant check (404 missing vs 403 cross-tenant, matching project
/// controllers). List endpoints honor <c>?page=&amp;pageSize=</c> (max 100) and
/// cap at 100 rows per page for large DLQ/lease/review backlogs.
/// </summary>
[ApiController]
[Route("api/v1/admin")]
[Authorize]
[ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
[ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status403Forbidden)]
public sealed class AdminController : ControllerBase
{
    private readonly AuditService _audit;
    private readonly IStageExecutionContextFactory _contextFactory;

    public AdminController(AuditService audit, IStageExecutionContextFactory contextFactory)
    {
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(contextFactory);
        _audit = audit;
        _contextFactory = contextFactory;
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
            throw new ForbiddenException($"Resource '{id}' does not belong to the current tenant.");
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
