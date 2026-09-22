using DubbingPlatform.Application.MultiTenancy;
using DubbingPlatform.Application.Options;
using DubbingPlatform.Application.Projects;
using DubbingPlatform.Application.Services;
using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DubbingPlatform.Application.Dashboard;

/// <summary>
/// Project counts slice.
/// </summary>
public sealed record DashboardProjectCounts(int Active, int Archived, int Total);

/// <summary>
/// Recent output slice (max 5, newest first).
/// </summary>
public sealed record DashboardRecentOutput(Guid Id, Guid ProjectId, string MediaKind, string Container, DateTimeOffset CreatedAt);

/// <summary>
/// Storage slice.
/// </summary>
public sealed record DashboardStorage(long UsedBytes, long QuotaBytes);

/// <summary>
/// Cost slice (month-to-date, USD only here; no per-invoice detail).
/// </summary>
public sealed record DashboardCost(double MonthToDate, string Currency);

/// <summary>
/// Quota slice (projects-per-day remaining, resets at next UTC midnight).
/// </summary>
public sealed record DashboardQuota(int Remaining, DateTimeOffset ResetsAt);

/// <summary>
/// Warning slice with machine-readable code.
/// </summary>
public sealed record DashboardWarning(string Code, string Message, Guid? ProjectId);

/// <summary>
/// Backlog slice.
/// </summary>
public sealed record DashboardBacklog(int PendingReviews, int RunningJobs);

/// <summary>
/// Full dashboard summary (seven sections).
/// </summary>
public sealed record DashboardSummary(
    DashboardProjectCounts ProjectCounts,
    IReadOnlyList<DashboardRecentOutput> RecentOutputs,
    DashboardStorage Storage,
    DashboardCost Cost,
    DashboardQuota Quota,
    IReadOnlyList<DashboardWarning> Warnings,
    DashboardBacklog Backlog);

/// <summary>
/// Single aggregated tenant-scoped dashboard query service. All reads are
/// <c>AsNoTracking</c> with no writes; soft-deleted projects are excluded
/// everywhere. Warnings carry machine-readable codes
/// (<c>PROJECT_FAILED</c>, <c>PROJECT_MANUAL_REVIEW</c>,
/// <c>STORAGE_NEAR_QUOTA</c>, <c>QUOTA_NEAR_LIMIT</c>).
/// </summary>
public sealed class DashboardService
{
    public const string FailedCode = "PROJECT_FAILED";

    public const string ManualReviewCode = "PROJECT_MANUAL_REVIEW";

    public const string StorageNearQuotaCode = "STORAGE_NEAR_QUOTA";

    public const string QuotaNearLimitCode = "QUOTA_NEAR_LIMIT";

    private readonly IStageExecutionContextFactory _contextFactory;
    private readonly QuotaOptions _quota;

    public DashboardService(IStageExecutionContextFactory contextFactory, IOptions<QuotaOptions> quotaOptions)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        ArgumentNullException.ThrowIfNull(quotaOptions);
        _contextFactory = contextFactory;
        _quota = quotaOptions.Value;
    }

    /// <summary>
    /// Builds the seven-section summary for the tenant.
    /// </summary>
    public async Task<DashboardSummary> GetSummaryAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        if (tenantId == Guid.Empty)
        {
            throw new Domain.Exceptions.DomainException("TenantId must not be empty.");
        }

        var now = DateTimeOffset.UtcNow;
        var monthStart = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);
        var dayStart = now.Date;
        var resetsAt = new DateTimeOffset(dayStart.AddDays(1), TimeSpan.Zero);

        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();

            var projects = db.Set<DubbingProject>().AsNoTracking()
                .Where(p => EF.Property<bool>(p, "IsDeleted") == false);

            var active = await projects.CountAsync(p => !p.IsArchived, cancellationToken).ConfigureAwait(false);
            var archived = await projects.CountAsync(p => p.IsArchived, cancellationToken).ConfigureAwait(false);

            var recentOutputs = await db.Set<OutputAsset>().AsNoTracking()
                .OrderByDescending(o => o.CreatedAt)
                .Take(5)
                .Select(o => new DashboardRecentOutput(o.Id, o.ProjectId, o.MediaKind, o.Container, o.CreatedAt))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            var usedBytes = await db.Set<ContentObject>().AsNoTracking()
                .Where(c => c.Status == ContentObjectStatus.Committed)
                .SumAsync(c => (long?)c.SizeBytes, cancellationToken).ConfigureAwait(false) ?? 0L;

            var monthCost = await db.Set<CostReservation>().AsNoTracking()
                .Where(r => r.CreatedAt >= monthStart && (r.State == "Reserved" || r.State == "Reconciled"))
                .SumAsync(r => (double?)(r.State == "Reserved" ? r.ReservedAmount : r.ActualAmount), cancellationToken).ConfigureAwait(false) ?? 0.0;

            var todayCount = await projects.CountAsync(p => p.CreatedAt >= dayStart, cancellationToken).ConfigureAwait(false);
            var remaining = Math.Max(0, _quota.MaxProjectsPerDay - todayCount);

            var pendingReviews = await db.Set<ReviewItem>().AsNoTracking()
                .CountAsync(r => r.Status == ReviewStatus.Open, cancellationToken).ConfigureAwait(false);
            var runningJobs = await db.Set<ProcessingRun>().AsNoTracking()
                .CountAsync(r => ProjectSettingsGuard.ActiveRunStatuses.Contains(r.Status), cancellationToken).ConfigureAwait(false);

            var warnings = new List<DashboardWarning>();
            var failedIds = await projects
                .Where(p => p.Status == ProjectStatus.Failed)
                .Select(p => p.Id)
                .Take(10)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            foreach (var id in failedIds)
            {
                warnings.Add(new DashboardWarning(FailedCode, "Project failed.", id));
            }

            var reviewIds = await projects
                .Where(p => p.Status == ProjectStatus.ManualReviewRequired)
                .Select(p => p.Id)
                .Take(10)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            foreach (var id in reviewIds)
            {
                warnings.Add(new DashboardWarning(ManualReviewCode, "Project requires manual review.", id));
            }

            if (_quota.MaxStorageBytes > 0 && usedBytes >= (long)(_quota.MaxStorageBytes * 0.8))
            {
                warnings.Add(new DashboardWarning(StorageNearQuotaCode, "Storage usage is above 80% of quota.", null));
            }

            if (remaining == 0)
            {
                warnings.Add(new DashboardWarning(QuotaNearLimitCode, "Daily project quota is exhausted.", null));
            }

            return new DashboardSummary(
                new DashboardProjectCounts(active, archived, active + archived),
                recentOutputs,
                new DashboardStorage(usedBytes, _quota.MaxStorageBytes),
                new DashboardCost(monthCost, "USD"),
                new DashboardQuota(remaining, resetsAt),
                warnings,
                new DashboardBacklog(pendingReviews, runningJobs));
        }
    }
}
