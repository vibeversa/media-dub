using DubbingPlatform.Application.Authorization;
using DubbingPlatform.Application.MultiTenancy;
using DubbingPlatform.Application.Services;
using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Domain.Enums;
using DubbingPlatform.Domain.Exceptions;
using DubbingPlatform.Domain.Identity;
using Microsoft.EntityFrameworkCore;

namespace DubbingPlatform.Application.Workspace;

/// <summary>
/// Workspace aggregate result with the logical query count for the call.
/// </summary>
public sealed record WorkspaceResult(WorkspaceDto Workspace, int QueryCount);

/// <summary>
/// Single-handler workspace aggregate. All reads are tenant-scoped,
/// <c>AsNoTracking</c>, soft-deleted projects excluded; cross-tenant ids yield
/// 403 via the maintenance-scope ownership load (matching project controllers;
/// the SSE stream is the only 404 exception). The call performs a bounded batch
/// of queries (currently 9 logical reads, ceiling 12 asserted in tests) — no
/// per-segment/per-stage N+1. Cost fields are display approximations; billing
/// stays in the Plan A ledger. Permissions are UX hints derived from JWT roles
/// (no extra DB round trip).
/// </summary>
public sealed class WorkspaceService
{
    /// <summary>
    /// Query-count ceiling asserted in tests (bounded batch, no N+1).
    /// </summary>
    public const int QueryCeiling = 12;

    private readonly IStageExecutionContextFactory _contextFactory;

    public WorkspaceService(IStageExecutionContextFactory contextFactory)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        _contextFactory = contextFactory;
    }

    /// <summary>
    /// Builds the workspace aggregate for a project in one batched call.
    /// </summary>
    public async Task<WorkspaceResult> GetAsync(
        Guid tenantId,
        Guid projectId,
        IReadOnlyList<string> jwtRoles,
        CancellationToken cancellationToken = default)
    {
        if (tenantId == Guid.Empty)
        {
            throw new DomainException("TenantId must not be empty.");
        }

        if (projectId == Guid.Empty)
        {
            throw new DomainException("ProjectId must not be empty.");
        }

        ArgumentNullException.ThrowIfNull(jwtRoles);

        var queries = 0;
        DubbingProject project;
        bool isDeleted;
        using (TenantContext.BeginMaintenanceScope())
        {
            using var db = _contextFactory.CreateDbContext();
            queries++;
            project = await db.Set<DubbingProject>()
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken).ConfigureAwait(false)
                ?? throw new Application.Exceptions.NotFoundException($"Project '{projectId}' was not found.");
            isDeleted = await db.Set<DubbingProject>()
                .Where(p => p.Id == projectId)
                .Select(p => EF.Property<bool>(p, "IsDeleted"))
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            queries++;
        }

        if (project.TenantId != tenantId)
        {
            throw new Application.Exceptions.ForbiddenException($"Project '{projectId}' does not belong to the current tenant.");
        }

        if (isDeleted)
        {
            throw new Application.Exceptions.NotFoundException($"Project '{projectId}' was not found.");
        }

        MediaAsset? media;
        ProcessingRun? run;
        List<RunStageSummary> summaries;
        int retrying;
        List<ReviewItem> openReviews;
        OutputAsset? output;
        double runCost;
        double monthCost;
        List<ActivityEvent> activity;

        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();

            queries++;
            media = await db.Set<MediaAsset>()
                .AsNoTracking()
                .Where(m => m.ProjectId == projectId)
                .OrderByDescending(m => m.CreatedAt)
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

            queries++;
            run = await db.Set<ProcessingRun>()
                .AsNoTracking()
                .Where(r => r.ProjectId == projectId)
                .OrderByDescending(r => r.CreatedAt)
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

            if (run is not null)
            {
                queries++;
                summaries = await db.Set<RunStageSummary>()
                    .AsNoTracking()
                    .Where(s => s.ProcessingRunId == run.Id)
                    .ToListAsync(cancellationToken).ConfigureAwait(false);

                queries++;
                retrying = await db.Set<StageExecution>()
                    .AsNoTracking()
                    .CountAsync(
                        e => e.ProcessingRunId == run.Id && e.Status == StageStatus.RetryPending,
                        cancellationToken).ConfigureAwait(false);

                queries++;
                var reviewRows = await db.Set<ReviewItem>()
                    .AsNoTracking()
                    .Where(r => r.ProcessingRunId == run.Id && r.Status == ReviewStatus.Open)
                    .OrderBy(r => r.CreatedAt)
                    .Take(20)
                    .ToListAsync(cancellationToken).ConfigureAwait(false);
                openReviews = reviewRows;

                queries++;
                output = await db.Set<OutputAsset>()
                    .AsNoTracking()
                    .Where(o => o.ProjectId == projectId)
                    .OrderByDescending(o => o.CreatedAt)
                    .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

                var monthStart = new DateTimeOffset(DateTimeOffset.UtcNow.Year, DateTimeOffset.UtcNow.Month, 1, 0, 0, 0, TimeSpan.Zero);
                queries++;
                var costs = await db.Set<CostReservation>()
                    .AsNoTracking()
                    .Where(c => c.ProjectId == projectId && (c.State == "Reserved" || c.State == "Reconciled"))
                    .Select(c => new { c.ProcessingRunId, c.State, c.ReservedAmount, c.ActualAmount, c.CreatedAt })
                    .ToListAsync(cancellationToken).ConfigureAwait(false);
                runCost = costs.Where(c => c.ProcessingRunId == run.Id)
                    .Sum(c => c.State == "Reserved" ? c.ReservedAmount : c.ActualAmount);
                monthCost = costs.Where(c => c.CreatedAt >= monthStart)
                    .Sum(c => c.State == "Reserved" ? c.ReservedAmount : c.ActualAmount);
            }
            else
            {
                summaries = [];
                retrying = 0;
                openReviews = [];
                output = null;
                runCost = 0.0;
                monthCost = 0.0;
                queries += 4;
            }

            queries++;
            activity = await db.Set<ActivityEvent>()
                .AsNoTracking()
                .Where(a => a.ProjectId == projectId)
                .OrderByDescending(a => a.OccurredAt)
                .Take(10)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
        }

        var snapshot = run is null
            ? null
            : ProgressService.BuildSnapshot(projectId, run, summaries, retrying, openReviews);

        var percent = snapshot?.PercentageIndicator ?? 0;
        var currentStage = snapshot?.CurrentStage;
        if (summaries.Count == 0)
        {
            currentStage = null;
        }

        var phase = snapshot?.Phase ?? "created";
        var stage = currentStage;
        var updatedAt = snapshot?.GeneratedAt ?? project.UpdatedAt;

        var pendingCount = openReviews.Count;
        DateTimeOffset? oldestWaiting = openReviews.Count == 0
            ? null
            : openReviews.Min(r => r.CreatedAt);

        var warnings = snapshot?.Warnings.ToList() ?? [];
        if (project.IsArchived)
        {
            warnings.Add("project-archived");
        }

        var outputState = output is null ? "pending" : "ready";
        var completeness = output is null ? percent : 100;

        var dto = new WorkspaceDto(
            new WorkspaceProjectDto(
                PublicIdMapper.ToPublic(project.Id, PublicIdMapper.DubbingProjectPrefix),
                project.Name ?? "Untitled project",
                project.Status.ToString(),
                project.SourceLanguage,
                project.TargetLanguage,
                project.IsArchived,
                project.ConfigurationHash,
                project.SettingsVersion),
            new WorkspaceMediaDto(
                media is null ? null : PublicIdMapper.ToPublic(media.Id, PublicIdMapper.MediaAssetPrefix),
                media?.Status.ToString() ?? "none",
                media?.Container,
                media?.SizeBytes ?? 0L,
                media?.DurationMs ?? 0),
            new WorkspaceRunDto(
                run is null ? null : PublicIdMapper.ToPublic(run.Id, PublicIdMapper.ProcessingRunPrefix),
                run?.Status.ToString(),
                run?.ConfigurationHash,
                run?.Attempt ?? 0),
            phase,
            stage,
            new WorkspaceProgressDto(percent, currentStage, updatedAt),
            new WorkspaceReviewDto(pendingCount, oldestWaiting),
            warnings,
            new WorkspaceOutputDto(outputState, Math.Clamp(completeness, 0, 100)),
            new WorkspaceCostDto(runCost, monthCost),
            new WorkspaceActivityDto(activity
                .Select(a => new WorkspaceActivityRowDto(
                    PublicIdMapper.ToPublic(a.Id, PublicIdMapper.ActivityEventPrefix),
                    a.Summary,
                    a.OccurredAt))
                .ToList()),
            new WorkspacePermissionsDto(AllowedActionsFor(jwtRoles)));

        return new WorkspaceResult(dto, queries);
    }

    /// <summary>
    /// Pure role → allowed-actions mapping for workspace UX hints.
    /// TenantAdmin/Service get all 12; Owner gets the owner set (incl.
    /// processing.start/cancel/retry); Editor gets edit/start/retry;
    /// Reviewer gets view/review; Viewer gets view only.
    /// </summary>
    public static IReadOnlyList<string> AllowedActionsFor(IEnumerable<string> jwtRoles)
    {
        ArgumentNullException.ThrowIfNull(jwtRoles);
        var roles = new HashSet<string>(
            jwtRoles.Where(r => !string.IsNullOrWhiteSpace(r)).Select(r => r.Trim()),
            StringComparer.Ordinal);

        if (roles.Contains(Roles.TenantAdmin) || roles.Contains(Roles.Service))
        {
            return Permissions.All;
        }

        var allowed = new HashSet<string>(StringComparer.Ordinal);
        if (roles.Contains(Roles.ProjectOwner))
        {
            allowed.UnionWith([
                Permissions.ProjectView,
                Permissions.ProjectEdit,
                Permissions.ProjectDelete,
                Permissions.ProcessingStart,
                Permissions.ProcessingCancel,
                Permissions.ProcessingRetry,
                Permissions.ReviewView,
                Permissions.ReviewResolve,
                Permissions.ExportCreate,
                Permissions.ExportDownload,
            ]);
        }

        if (roles.Contains(Roles.ProjectEditor))
        {
            allowed.UnionWith([
                Permissions.ProjectView,
                Permissions.ProjectEdit,
                Permissions.ProcessingStart,
                Permissions.ProcessingRetry,
                Permissions.ReviewView,
                Permissions.ReviewResolve,
                Permissions.ExportCreate,
                Permissions.ExportDownload,
            ]);
        }

        if (roles.Contains(Roles.Reviewer))
        {
            allowed.UnionWith([
                Permissions.ProjectView,
                Permissions.ReviewView,
                Permissions.ReviewResolve,
                Permissions.ExportDownload,
            ]);
        }

        if (roles.Contains(Roles.ProjectViewer))
        {
            allowed.UnionWith([
                Permissions.ProjectView,
                Permissions.ReviewView,
                Permissions.ExportDownload,
            ]);
        }

        return allowed.OrderBy(p => p, StringComparer.Ordinal).ToList();
    }
}
