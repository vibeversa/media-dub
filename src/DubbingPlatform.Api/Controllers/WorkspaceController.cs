using DubbingPlatform.Api.Middleware;
using DubbingPlatform.Api.Models;
using DubbingPlatform.Application.Authorization;
using DubbingPlatform.Application.Common;
using DubbingPlatform.Application.Exceptions;
using DubbingPlatform.Application.MultiTenancy;
using DubbingPlatform.Application.Services;
using DubbingPlatform.Application.Workspace;
using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DubbingPlatform.Api.Controllers;

/// <summary>
/// Workspace aggregate plus thin projections nested under projects:
/// <c>GET /api/v1/projects/{projectId}/workspace</c> (aggregate, single handler
/// call, bounded queries ≤ <see cref="WorkspaceService.QueryCeiling"/>),
/// <c>GET .../activity</c> (thin projection over Task 002 activity, paginated),
/// <c>GET .../output</c> (thin projection over Task 012 outputs, latest first),
/// <c>GET .../quality</c> (thin projection over quality results, counts only).
/// Progress (<c>GET .../progress</c>) and SSE (<c>GET .../progress/stream</c>)
/// live in <see cref="ProcessingController"/> to avoid duplicate routes; this
/// controller delegates activity/output/quality to the same tenant-scoped reads
/// without duplicating Task 002/012/quality services. All GETs require
/// <c>project.view</c> (role <see cref="AuthPolicies.RequireProjectViewer"/>).
/// </summary>
[ApiController]
[Route("api/v1/projects/{projectId}")]
[Authorize]
[ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
[ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status403Forbidden)]
public sealed class WorkspaceController : ControllerBase
{
    private readonly WorkspaceService _workspace;
    private readonly IStageExecutionContextFactory _contextFactory;

    public WorkspaceController(WorkspaceService workspace, IStageExecutionContextFactory contextFactory)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(contextFactory);
        _workspace = workspace;
        _contextFactory = contextFactory;
    }

    /// <summary>
    /// Gets the workspace aggregate in one call (all ten sections: project,
    /// media, run, phase, stage, progress, review, warnings, output, cost,
    /// activity, permissions). Example: <c>{ project: { id: "prj_...", status:
    /// "Processing" }, run: { id: "run_...", status: "Running" }, progress: {
    /// percentApproximate: 42, currentStage: "Translation" }, ... }</c>.
    /// </summary>
    [HttpGet("workspace")]
    [Authorize(Policy = AuthPolicies.RequireProjectViewer)]
    [ProducesResponseType(typeof(WorkspaceDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetWorkspace(
        [FromRoute] string projectId,
        CancellationToken cancellationToken)
    {
        var tenantId = User.GetTenantId();
        var projectGuid = PublicIdParser.ParseProjectId(projectId);
        var result = await _workspace.GetAsync(tenantId, projectGuid, User.GetRoles(), cancellationToken).ConfigureAwait(false);
        Response.Headers["X-Workspace-Queries"] = result.QueryCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return Ok(result.Workspace);
    }

    /// <summary>
    /// Thin activity projection (Task 002): recent activity rows, paginated.
    /// </summary>
    [HttpGet("activity")]
    [Authorize(Policy = AuthPolicies.RequireProjectViewer)]
    [ProducesResponseType(typeof(PaginatedResult<WorkspaceActivityResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetActivity(
        [FromRoute] string projectId,
        [FromQuery] int? page,
        [FromQuery] int? pageSize,
        CancellationToken cancellationToken)
    {
        var tenantId = User.GetTenantId();
        var projectGuid = PublicIdParser.ParseProjectId(projectId);
        await RequireProjectAsync(tenantId, projectGuid, cancellationToken).ConfigureAwait(false);

        var query = new PaginationParams { Page = page ?? 1, PageSize = pageSize ?? PaginationParams.DefaultPageSize };
        query.Normalize();
        var (safePage, safeSize) = query.Normalized();

        List<ActivityEvent> rows;
        long total;
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            var scoped = db.Set<ActivityEvent>()
                .AsNoTracking()
                .Where(a => a.ProjectId == projectGuid);
            total = await scoped.LongCountAsync(cancellationToken).ConfigureAwait(false);
            rows = await scoped
                .OrderByDescending(a => a.OccurredAt)
                .Skip((safePage - 1) * safeSize)
                .Take(safeSize)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
        }

        var items = rows
            .Select(a => new WorkspaceActivityResponse(
                Domain.Identity.PublicIdMapper.ToPublic(a.Id, Domain.Identity.PublicIdMapper.ActivityEventPrefix),
                a.Summary,
                a.OccurredAt))
            .ToList();
        return Ok(PaginatedResult<WorkspaceActivityResponse>.Create(items, safePage, safeSize, total));
    }

    /// <summary>
    /// Thin output projection (Task 012): latest outputs first, paginated.
    /// </summary>
    [HttpGet("output")]
    [Authorize(Policy = AuthPolicies.RequireProjectViewer)]
    [ProducesResponseType(typeof(PaginatedResult<WorkspaceOutputResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetOutput(
        [FromRoute] string projectId,
        [FromQuery] int? page,
        [FromQuery] int? pageSize,
        CancellationToken cancellationToken)
    {
        var tenantId = User.GetTenantId();
        var projectGuid = PublicIdParser.ParseProjectId(projectId);
        await RequireProjectAsync(tenantId, projectGuid, cancellationToken).ConfigureAwait(false);

        var query = new PaginationParams { Page = page ?? 1, PageSize = pageSize ?? PaginationParams.DefaultPageSize };
        query.Normalize();
        var (safePage, safeSize) = query.Normalized();

        List<OutputAsset> rows;
        long total;
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            var scoped = db.Set<OutputAsset>()
                .AsNoTracking()
                .Where(o => o.ProjectId == projectGuid);
            total = await scoped.LongCountAsync(cancellationToken).ConfigureAwait(false);
            rows = await scoped
                .OrderByDescending(o => o.CreatedAt)
                .Skip((safePage - 1) * safeSize)
                .Take(safeSize)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
        }

        var items = rows
            .Select(o => new WorkspaceOutputResponse("ready", 100, o.Id.ToString("D")))
            .ToList();
        return Ok(PaginatedResult<WorkspaceOutputResponse>.Create(items, safePage, safeSize, total));
    }

    /// <summary>
    /// Thin quality projection: failed/blocked counts plus distinct codes
    /// (never payload text).
    /// </summary>
    [HttpGet("quality")]
    [Authorize(Policy = AuthPolicies.RequireProjectViewer)]
    [ProducesResponseType(typeof(WorkspaceQualityResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetQuality(
        [FromRoute] string projectId,
        CancellationToken cancellationToken)
    {
        var tenantId = User.GetTenantId();
        var projectGuid = PublicIdParser.ParseProjectId(projectId);
        await RequireProjectAsync(tenantId, projectGuid, cancellationToken).ConfigureAwait(false);

        List<QualityResult> rows;
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            rows = await db.Set<QualityResult>()
                .AsNoTracking()
                .Where(q => q.ProjectId == projectGuid)
                .OrderByDescending(q => q.CreatedAt)
                .Take(200)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
        }

        var failed = rows.Count(r => r.Status == QualityStatus.RetryRequired || r.Status == QualityStatus.ManualReviewRequired);
        var blocked = rows.Count(r => r.Status == QualityStatus.Blocked);
        var codes = rows.Select(r => r.Code).Distinct(StringComparer.Ordinal).OrderBy(c => c, StringComparer.Ordinal).ToList();
        return Ok(new WorkspaceQualityResponse(failed, blocked, codes));
    }

    private async Task RequireProjectAsync(Guid tenantId, Guid projectId, CancellationToken cancellationToken)
    {
        DubbingProject? project;
        bool isDeleted;
        using (TenantContext.BeginMaintenanceScope())
        {
            using var db = _contextFactory.CreateDbContext();
            project = await db.Set<DubbingProject>()
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken).ConfigureAwait(false);
            if (project is null)
            {
                throw new NotFoundException($"Project '{projectId}' was not found.");
            }

            isDeleted = await db.Set<DubbingProject>()
                .Where(p => p.Id == projectId)
                .Select(p => EF.Property<bool>(p, "IsDeleted"))
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        }

        if (project.TenantId != tenantId)
        {
            throw new ForbiddenException($"Project '{projectId}' does not belong to the current tenant.");
        }

        if (isDeleted)
        {
            throw new NotFoundException($"Project '{projectId}' was not found.");
        }
    }
}
