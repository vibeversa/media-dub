using System.Diagnostics.Metrics;
using DubbingPlatform.Application.Abstractions;
using DubbingPlatform.Application.Exceptions;
using DubbingPlatform.Application.MultiTenancy;
using DubbingPlatform.Application.Options;
using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Domain.Enums;
using DubbingPlatform.Domain.Exceptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DubbingPlatform.Application.Services;

/// <summary>
/// Quota meters. Counter names are frozen: renaming breaks dashboards
/// (Task 38). The <c>dimension</c> tag names the quota that rejected.
/// </summary>
public static class QuotaMeters
{
    public const string MeterName = "DubbingPlatform.Quota";

    public const string RejectionsMetricName = "quota.rejections";

    private static readonly Meter Meter = new(MeterName);

    public static readonly Counter<long> Rejections =
        Meter.CreateCounter<long>(RejectionsMetricName);
}

/// <summary>
/// Quota dimensions enforced by <see cref="QuotaService.CheckAsync"/>.
/// Wire names are lowercase-hyphenated; parsing is case-insensitive.
/// </summary>
public static class QuotaDimensions
{
    public const string ActiveProjects = "active-projects";

    public const string ProjectsPerDay = "projects-per-day";

    public const string CostPerProject = "cost-per-project";

    public const string CostPerSegment = "cost-per-segment";

    public const string SegmentCount = "segment-count";

    public const string Storage = "storage";

    public const string ConcurrentStages = "concurrent-stages";

    public static string Normalize(string? dimension)
    {
        var normalized = (dimension ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "active-projects" or "activeprojects" or "maxactiveprojects" => ActiveProjects,
            "projects-per-day" or "projectsperday" or "maxprojectsperday" => ProjectsPerDay,
            "cost-per-project" or "costperproject" or "maxcostperproject" => CostPerProject,
            "cost-per-segment" or "costpersegment" or "maxcostpersegment" => CostPerSegment,
            "segment-count" or "segmentcount" or "maxsegmentcount" => SegmentCount,
            "storage" or "maxstoragebytes" => Storage,
            "concurrent-stages" or "concurrentstages" or "maxconcurrentstagespertenant" => ConcurrentStages,
            _ => throw new DomainException($"Unknown quota dimension '{dimension}'."),
        };
    }
}

/// <summary>
/// PG-backed quota enforcement (correctness owner per R3; Redis never decides
/// quotas). Every denial throws <c>QuotaExceededException</c> (429
/// QUOTA_EXCEEDED) with the dimension in the message and increments
/// <c>quota.rejections{dimension}</c>. DB outages fail closed: the
/// storage boolean gate returns deny (false) so callers emit
/// QUOTA_EXCEEDED, while throwing check paths let the outage propagate as
/// 500 INTERNAL_ERROR for alerting — documented per the task edge decision
/// (rate fail-open vs quota/cost fail-closed). Only ids, dimensions, counts,
/// and bytes are logged — never content or secrets.
/// </summary>
public sealed class QuotaService : IQuotaGate
{
    private static readonly ProcessingRunStatus[] ActiveRunStatuses =
    [
        ProcessingRunStatus.Pending,
        ProcessingRunStatus.Running,
        ProcessingRunStatus.Cancelling,
        ProcessingRunStatus.ManualReviewRequired,
    ];

    private readonly IStageExecutionContextFactory _contextFactory;
    private readonly QuotaOptions _quota;
    private readonly ILogger<QuotaService> _logger;

    public QuotaService(
        IStageExecutionContextFactory contextFactory,
        IOptions<QuotaOptions> quotaOptions,
        ILogger<QuotaService> logger)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        ArgumentNullException.ThrowIfNull(quotaOptions);
        ArgumentNullException.ThrowIfNull(logger);
        _contextFactory = contextFactory;
        _quota = quotaOptions.Value;
        _logger = logger;
    }

    /// <summary>
    /// Pure storage check: <c>used + additional &lt;= limit</c> with overflow
    /// protection. Pure for hermetic tests.
    /// </summary>
    public static bool IsStorageExceeded(long usedBytes, long additionalBytes, long limitBytes)
    {
        if (usedBytes < 0 || additionalBytes < 0 || limitBytes < 0)
        {
            throw new DomainException("Quota byte counts must be >= 0.");
        }

        return checked(usedBytes + additionalBytes) > limitBytes;
    }

    /// <summary>
    /// Pure cost check: <c>current + additional &gt; limit</c>. NaN is
    /// fail-closed (exceeded). Pure for hermetic tests.
    /// </summary>
    public static bool IsCostExceeded(double current, double additional, double limit)
    {
        if (double.IsNaN(current) || double.IsNaN(additional) || double.IsNaN(limit))
        {
            return true;
        }

        if (current < 0.0 || additional < 0.0)
        {
            throw new DomainException("Cost amounts must be >= 0.");
        }

        return current + additional > limit;
    }

    /// <summary>
    /// Dispatches one dimension check. <paramref name="additional"/> carries
    /// bytes for storage, USD for cost dims, units for count dims.
    /// </summary>
    public async Task CheckAsync(
        Guid tenantId,
        Guid? projectId,
        string dimension,
        double additional = 0.0,
        CancellationToken cancellationToken = default)
    {
        RequireTenant(tenantId);
        var normalized = QuotaDimensions.Normalize(dimension);
        switch (normalized)
        {
            case QuotaDimensions.ActiveProjects:
                await CheckActiveProjectsAsync(tenantId, cancellationToken).ConfigureAwait(false);
                return;
            case QuotaDimensions.ProjectsPerDay:
                await CheckProjectsPerDayAsync(tenantId, cancellationToken).ConfigureAwait(false);
                return;
            case QuotaDimensions.CostPerProject:
                RequireProject(projectId);
                await CheckCostPerProjectAsync(tenantId, projectId!.Value, additional, cancellationToken).ConfigureAwait(false);
                return;
            case QuotaDimensions.CostPerSegment:
                CheckCostPerSegment(additional);
                return;
            case QuotaDimensions.SegmentCount:
                RequireProject(projectId);
                await CheckSegmentCountAsync(tenantId, projectId!.Value, (int)Math.Ceiling(additional), cancellationToken).ConfigureAwait(false);
                return;
            case QuotaDimensions.Storage:
                RequireProject(projectId);
                await EnforceStorageOnCommit(tenantId, (long)Math.Ceiling(additional), cancellationToken).ConfigureAwait(false);
                return;
            case QuotaDimensions.ConcurrentStages:
                await CheckConcurrentStagesAsync(tenantId, (int)Math.Ceiling(additional), cancellationToken).ConfigureAwait(false);
                return;
            default:
                throw new DomainException($"Unknown quota dimension '{dimension}'.");
        }
    }

    /// <summary>
    /// Storage boolean gate (frozen <c>IQuotaGate</c> contract). Fail-closed:
    /// DB outages return deny (false) with an error log so artifact commits
    /// refuse rather than overspend.
    /// </summary>
    public async Task<bool> CheckStorageAsync(Guid tenantId, long additionalBytes, CancellationToken cancellationToken)
    {
        if (tenantId == Guid.Empty)
        {
            throw new DomainException("TenantId must not be empty.");
        }

        if (additionalBytes < 0)
        {
            throw new DomainException("AdditionalBytes must be >= 0.");
        }

        try
        {
            using (TenantContext.BeginScope(tenantId))
            {
                using var db = _contextFactory.CreateDbContext();
                var used = await db.Set<ContentObject>()
                    .Where(c => c.Status == ContentObjectStatus.Committed)
                    .SumAsync(c => (long?)c.SizeBytes, cancellationToken).ConfigureAwait(false) ?? 0L;
                return !IsStorageExceeded(used, additionalBytes, _quota.MaxStorageBytes);
            }
        }
#pragma warning disable CA1031 // Fail-closed gate: any DB outage denies storage rather than overspending.
        catch (Exception ex) when (ex is not DomainException && ex is not AppException)
#pragma warning restore CA1031
        {
            _logger.LogError(ex, "Storage quota check failed for tenant {TenantId}; denying.", tenantId);
            return false;
        }
    }

    /// <summary>
    /// Artifact-commit hook (Task 036 §2): throws <c>QuotaExceededException</c>
    /// (dimension <c>storage</c>) when the commit would exceed
    /// <c>Quota:MaxStorageBytes</c>. Called from
    /// <see cref="ArtifactService"/> before new-content commits; dedup-reuse
    /// paths skip it (no new bytes).
    /// </summary>
    public async Task EnforceStorageOnCommit(
        Guid tenantId,
        long additionalBytes,
        CancellationToken cancellationToken = default)
    {
        RequireTenant(tenantId);
        if (additionalBytes < 0)
        {
            throw new DomainException("AdditionalBytes must be >= 0.");
        }

        if (!await CheckStorageAsync(tenantId, additionalBytes, cancellationToken).ConfigureAwait(false))
        {
            QuotaMeters.Rejections.Add(1, new KeyValuePair<string, object?>("dimension", QuotaDimensions.Storage));
            throw new QuotaExceededException(
                $"Storage quota would be exceeded by {additionalBytes.ToString(System.Globalization.CultureInfo.InvariantCulture)} bytes (dimension storage, max {_quota.MaxStorageBytes.ToString(System.Globalization.CultureInfo.InvariantCulture)} bytes).");
        }
    }

    public async Task CheckActiveProjectsAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        RequireTenant(tenantId);
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            var active = await db.Set<ProcessingRun>()
                .Where(r => ActiveRunStatuses.Contains(r.Status))
                .CountAsync(cancellationToken).ConfigureAwait(false);
            if (active >= _quota.MaxActiveProjects)
            {
                QuotaMeters.Rejections.Add(1, new KeyValuePair<string, object?>("dimension", QuotaDimensions.ActiveProjects));
                throw new QuotaExceededException(
                    $"Tenant has {active.ToString(System.Globalization.CultureInfo.InvariantCulture)} active processing runs (max {_quota.MaxActiveProjects.ToString(System.Globalization.CultureInfo.InvariantCulture)}, dimension active-projects).");
            }
        }
    }

    public async Task CheckProjectsPerDayAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        RequireTenant(tenantId);
        var dayStart = DateTimeOffset.UtcNow.Date;
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            var today = await db.Set<DubbingProject>()
                .Where(p => p.CreatedAt >= dayStart)
                .CountAsync(cancellationToken).ConfigureAwait(false);
            if (today >= _quota.MaxProjectsPerDay)
            {
                QuotaMeters.Rejections.Add(1, new KeyValuePair<string, object?>("dimension", QuotaDimensions.ProjectsPerDay));
                throw new QuotaExceededException(
                    $"Tenant created {today.ToString(System.Globalization.CultureInfo.InvariantCulture)} projects today (max {_quota.MaxProjectsPerDay.ToString(System.Globalization.CultureInfo.InvariantCulture)}, dimension projects-per-day).");
            }
        }
    }

    public void CheckCostPerSegment(double amount)
    {
        if (double.IsNaN(amount) || amount < 0.0)
        {
            throw new DomainException("Amount must be >= 0.");
        }

        if (amount > _quota.MaxCostPerSegment)
        {
            QuotaMeters.Rejections.Add(1, new KeyValuePair<string, object?>("dimension", QuotaDimensions.CostPerSegment));
            throw new QuotaExceededException(
                $"Amount {amount.ToString("F6", System.Globalization.CultureInfo.InvariantCulture)} USD exceeds per-segment cap {_quota.MaxCostPerSegment.ToString("F6", System.Globalization.CultureInfo.InvariantCulture)} USD (dimension cost-per-segment).");
        }
    }

    public async Task CheckCostPerProjectAsync(
        Guid tenantId,
        Guid projectId,
        double additional,
        CancellationToken cancellationToken = default)
    {
        RequireTenant(tenantId);
        RequireId(projectId, nameof(projectId));
        if (double.IsNaN(additional) || additional < 0.0)
        {
            throw new DomainException("Additional must be >= 0.");
        }

        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            var current = await db.Set<CostReservation>()
                .Where(r => r.ProjectId == projectId
                    && (r.State == "Reserved" || r.State == "Reconciled"))
                .SumAsync(r => (double?)(r.State == "Reserved" ? r.ReservedAmount : r.ActualAmount), cancellationToken).ConfigureAwait(false) ?? 0.0;
            if (IsCostExceeded(current, additional, _quota.MaxCostPerProject))
            {
                QuotaMeters.Rejections.Add(1, new KeyValuePair<string, object?>("dimension", QuotaDimensions.CostPerProject));
                throw new QuotaExceededException(
                    $"Project cost {current.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)} USD plus {additional.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)} USD exceeds cap {_quota.MaxCostPerProject.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)} USD (dimension cost-per-project).");
            }
        }
    }

    public async Task CheckSegmentCountAsync(
        Guid tenantId,
        Guid projectId,
        int additional,
        CancellationToken cancellationToken = default)
    {
        RequireTenant(tenantId);
        RequireId(projectId, nameof(projectId));
        if (additional < 0)
        {
            throw new DomainException("Additional must be >= 0.");
        }

        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            var count = await db.Set<SpeechSegment>()
                .Where(s => s.ProjectId == projectId)
                .CountAsync(cancellationToken).ConfigureAwait(false);
            if (checked(count + additional) > _quota.MaxSegmentCount)
            {
                QuotaMeters.Rejections.Add(1, new KeyValuePair<string, object?>("dimension", QuotaDimensions.SegmentCount));
                throw new QuotaExceededException(
                    $"Segment count {count.ToString(System.Globalization.CultureInfo.InvariantCulture)} plus {additional.ToString(System.Globalization.CultureInfo.InvariantCulture)} exceeds max {_quota.MaxSegmentCount.ToString(System.Globalization.CultureInfo.InvariantCulture)} (dimension segment-count).");
            }
        }
    }

    public async Task CheckConcurrentStagesAsync(
        Guid tenantId,
        int additional,
        CancellationToken cancellationToken = default)
    {
        RequireTenant(tenantId);
        if (additional < 0)
        {
            throw new DomainException("Additional must be >= 0.");
        }

        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            var active = await db.Set<StageExecution>()
                .Where(e => e.Status == StageStatus.Scheduled
                    || e.Status == StageStatus.Running
                    || e.Status == StageStatus.RetryPending)
                .CountAsync(cancellationToken).ConfigureAwait(false);
            if (checked(active + additional) > _quota.MaxConcurrentStagesPerTenant)
            {
                QuotaMeters.Rejections.Add(1, new KeyValuePair<string, object?>("dimension", QuotaDimensions.ConcurrentStages));
                throw new QuotaExceededException(
                    $"Tenant has {active.ToString(System.Globalization.CultureInfo.InvariantCulture)} concurrent stages (max {_quota.MaxConcurrentStagesPerTenant.ToString(System.Globalization.CultureInfo.InvariantCulture)}, dimension concurrent-stages).");
            }
        }
    }

    private static void RequireTenant(Guid tenantId)
    {
        if (tenantId == Guid.Empty)
        {
            throw new DomainException("TenantId must not be empty.");
        }
    }

    private static void RequireId(Guid id, string name)
    {
        if (id == Guid.Empty)
        {
            throw new DomainException($"{name} must not be empty.");
        }
    }

    private static void RequireProject(Guid? projectId)
    {
        if (!projectId.HasValue || projectId.Value == Guid.Empty)
        {
            throw new DomainException("ProjectId is required for this quota dimension.");
        }
    }
}
