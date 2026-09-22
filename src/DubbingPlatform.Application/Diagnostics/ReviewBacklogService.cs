using DubbingPlatform.Application.Diagnostics.Dto;
using DubbingPlatform.Application.MultiTenancy;
using DubbingPlatform.Application.Services;
using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DubbingPlatform.Application.Diagnostics;

/// <summary>
/// Read-only review backlog aggregation over the review store
/// (<c>ReviewItem</c> plus QC findings): status counts, QC severity counts,
/// the oldest waiting review, and a per-project open-review breakdown.
/// Counts and ids only — never review payload text. All queries are
/// <c>AsNoTracking</c>; no writes.
/// </summary>
public sealed class ReviewBacklogService
{
    private readonly IStageExecutionContextFactory _contextFactory;
    private readonly IDiagnosticsAccessChecker _access;
    private readonly ILogger<ReviewBacklogService> _logger;

    public ReviewBacklogService(
        IStageExecutionContextFactory contextFactory,
        IDiagnosticsAccessChecker access,
        ILogger<ReviewBacklogService> logger)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(logger);
        _contextFactory = contextFactory;
        _access = access;
        _logger = logger;
    }

    /// <summary>
    /// Returns the tenant review backlog.
    /// </summary>
    public async Task<ReviewBacklogDto> GetBacklogAsync(
        Guid tenantId,
        Guid userId,
        string? correlationId = null,
        CancellationToken cancellationToken = default)
    {
        await _access.RequireDiagnosticsViewerAsync(tenantId, userId, cancellationToken).ConfigureAwait(false);
        var correlation = DiagnosticsCorrelation.Normalize(correlationId);

        ReviewBacklogDto backlog;
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            var reviews = await db.Set<ReviewItem>()
                .AsNoTracking()
                .Select(r => new { r.Id, r.ProjectId, r.Status, r.CreatedAt })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            var findings = await db.Set<QualityResult>()
                .AsNoTracking()
                .Where(q => q.Status != QualityStatus.Pass)
                .Select(q => new { q.Severity })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            backlog = BuildBacklog(reviews.Select(r => (r.Id, r.ProjectId, r.Status, r.CreatedAt)).ToList(), findings.Select(f => f.Severity).ToList(), correlation);
        }

        _logger.LogInformation(
            "Diagnostics review backlog queried. {CorrelationId} {TenantId} {TotalOpen}",
            correlation,
            tenantId,
            backlog.TotalOpen);
        return backlog;
    }

    /// <summary>
    /// Aggregates review and severity rows into a backlog DTO. Pure.
    /// </summary>
    public static ReviewBacklogDto BuildBacklog(
        IReadOnlyList<(Guid Id, Guid ProjectId, ReviewStatus Status, DateTimeOffset CreatedAt)> reviews,
        IReadOnlyList<string> severities,
        string correlationId)
    {
        ArgumentNullException.ThrowIfNull(reviews);
        ArgumentNullException.ThrowIfNull(severities);
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);

        var byStatus = Enum.GetValues<ReviewStatus>()
            .ToDictionary(s => s.ToString(), _ => 0L, StringComparer.Ordinal);
        foreach (var review in reviews)
        {
            var key = review.Status.ToString();
            byStatus[key] = byStatus.TryGetValue(key, out var current) ? current + 1 : 1;
        }

        var bySeverity = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var severity in severities)
        {
            var key = string.IsNullOrWhiteSpace(severity) ? "unknown" : severity.Trim();
            bySeverity[key] = bySeverity.TryGetValue(key, out var current) ? current + 1 : 1;
        }

        var open = reviews.Where(r => r.Status == ReviewStatus.Open).OrderBy(r => r.CreatedAt).ToList();

        var perProject = open
            .GroupBy(r => r.ProjectId)
            .Select(g => new ReviewBacklogProjectEntry(g.Key, g.LongCount(), g.Min(r => r.CreatedAt)))
            .OrderBy(p => p.OldestWaitingAt)
            .ThenBy(p => p.ProjectId)
            .ToList();

        return new ReviewBacklogDto(
            correlationId,
            open.Count,
            byStatus,
            bySeverity,
            open.Count == 0 ? null : open[0].CreatedAt,
            open.Count == 0 ? null : open[0].Id,
            perProject);
    }
}
