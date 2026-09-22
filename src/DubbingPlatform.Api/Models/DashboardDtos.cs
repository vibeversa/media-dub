using DubbingPlatform.Application.Dashboard;
using DubbingPlatform.Api.Models;

namespace DubbingPlatform.Api.Models;

/// <summary>
/// Dashboard summary response (seven sections). Example:
/// <c>{ projectCounts: { active: 1, archived: 0, total: 1 }, recentOutputs: [],
/// storage: { usedBytes: 0, quotaBytes: 107374182400 },
/// cost: { monthToDate: 0, currency: "USD" },
/// quota: { remaining: 50, resetsAt: "2026-09-23T00:00:00Z" },
/// warnings: [], backlog: { pendingReviews: 0, runningJobs: 0 } }</c>.
/// No per-invoice or storage-key detail is exposed here.
/// </summary>
public sealed record DashboardSummaryResponse(
    DashboardProjectCounts ProjectCounts,
    IReadOnlyList<DashboardRecentOutputResponse> RecentOutputs,
    DashboardStorage Storage,
    DashboardCost Cost,
    DashboardQuota Quota,
    IReadOnlyList<DashboardWarning> Warnings,
    DashboardBacklog Backlog)
{
    public static DashboardSummaryResponse From(DashboardSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        return new DashboardSummaryResponse(
            summary.ProjectCounts,
            summary.RecentOutputs.Select(o => new DashboardRecentOutputResponse(
                o.Id.ToString("D"),
                PublicIdParser.ToProjectId(o.ProjectId),
                o.MediaKind,
                o.Container,
                o.CreatedAt)).ToList(),
            summary.Storage,
            summary.Cost,
            summary.Quota,
            summary.Warnings,
            summary.Backlog);
    }
}

/// <summary>
/// Recent output row: raw output GUID plus <c>prj_</c> project id.
/// </summary>
public sealed record DashboardRecentOutputResponse(
    string Id,
    string ProjectId,
    string MediaKind,
    string Container,
    DateTimeOffset CreatedAt);
