using DubbingPlatform.Application.Diagnostics.Dto;
using DubbingPlatform.Application.MultiTenancy;
using DubbingPlatform.Application.Services;
using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DubbingPlatform.Application.Diagnostics;

/// <summary>
/// Read-only worker health derived from Plan A runtime lease state
/// (<c>StageExecution</c> rows grouped by lease owner). There is no dedicated
/// worker heartbeat table: a worker holding at least one unexpired running
/// lease reads <c>Active</c>; a worker holding only expired running leases
/// reads <c>Stale</c>; roster entries (<c>Diagnostics:KnownWorkers</c>) with
/// no runtime state read as <c>Unknown</c> with a null heartbeat (never 404).
/// <c>UpdatedAt</c> is the last-heartbeat proxy: lease renewals persist
/// through the conditional-update path, which bumps the row. <c>Version</c>
/// has no runtime source yet and reads null; the metrics pipeline (Task 038)
/// owns version reporting. All queries are <c>AsNoTracking</c>; no writes.
/// </summary>
public sealed class WorkerHealthService
{
    /// <summary>Worker holds at least one unexpired running lease.</summary>
    public const string StatusActive = "Active";

    /// <summary>Worker holds running leases but every lease expired.</summary>
    public const string StatusStale = "Stale";

    /// <summary>Worker has no runtime lease state.</summary>
    public const string StatusUnknown = "Unknown";

    private readonly IStageExecutionContextFactory _contextFactory;
    private readonly DiagnosticsOptions _options;
    private readonly IDiagnosticsAccessChecker _access;
    private readonly ILogger<WorkerHealthService> _logger;
    private readonly TimeProvider _time;

    public WorkerHealthService(
        IStageExecutionContextFactory contextFactory,
        IOptions<DiagnosticsOptions> diagnosticsOptions,
        IDiagnosticsAccessChecker access,
        ILogger<WorkerHealthService> logger,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        ArgumentNullException.ThrowIfNull(diagnosticsOptions);
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(logger);
        _contextFactory = contextFactory;
        _options = diagnosticsOptions.Value;
        _access = access;
        _logger = logger;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Resolves one worker's status from its running leases. Pure. Workers
    /// with no running leases read <c>Unknown</c> (idle or gone); only workers
    /// holding expired running leases read <c>Stale</c>.
    /// </summary>
    public static string ResolveStatus(int runningLeases, int unexpiredRunningLeases)
    {
        if (unexpiredRunningLeases > 0)
        {
            return StatusActive;
        }

        if (runningLeases > 0)
        {
            return StatusStale;
        }

        return StatusUnknown;
    }

    /// <summary>
    /// Returns per-worker health for the tenant, ordered by worker name.
    /// </summary>
    public async Task<IReadOnlyList<WorkerHealthDto>> GetWorkerHealthAsync(
        Guid tenantId,
        Guid userId,
        string? correlationId = null,
        CancellationToken cancellationToken = default)
    {
        await _access.RequireDiagnosticsViewerAsync(tenantId, userId, cancellationToken).ConfigureAwait(false);
        var correlation = DiagnosticsCorrelation.Normalize(correlationId);
        var now = _time.GetUtcNow();

        IReadOnlyList<WorkerHealthDto> workers;
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            var executions = await db.Set<StageExecution>()
                .AsNoTracking()
                .Select(e => new { e.LeaseOwner, e.Status, e.LeaseExpiresAt, e.UpdatedAt })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            workers = BuildWorkers(executions.Select(e => (e.LeaseOwner, e.Status, e.LeaseExpiresAt, e.UpdatedAt)).ToList(), _options.KnownWorkers ?? [], correlation, now);
        }

        _logger.LogInformation(
            "Diagnostics worker health queried. {CorrelationId} {TenantId} {WorkerCount}",
            correlation,
            tenantId,
            workers.Count);
        return workers;
    }

    /// <summary>
    /// Aggregates lease rows plus the known-worker roster into health DTOs.
    /// Pure. Empty owner names are skipped; roster entries with no runtime
    /// state read <c>Unknown</c> with a null heartbeat.
    /// </summary>
    public static IReadOnlyList<WorkerHealthDto> BuildWorkers(
        IReadOnlyList<(string LeaseOwner, StageStatus Status, DateTimeOffset LeaseExpiresAt, DateTimeOffset UpdatedAt)> executions,
        IReadOnlyList<string> knownWorkers,
        string correlationId,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(executions);
        ArgumentNullException.ThrowIfNull(knownWorkers);
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);

        var workers = new List<WorkerHealthDto>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in executions
            .Where(e => !string.IsNullOrWhiteSpace(e.LeaseOwner))
            .GroupBy(e => e.LeaseOwner.Trim(), StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            seen.Add(group.Key);
            var running = group.Count(e => e.Status == StageStatus.Running);
            var unexpired = group.Count(e => e.Status == StageStatus.Running && e.LeaseExpiresAt > now);
            workers.Add(new WorkerHealthDto(
                correlationId,
                group.Key,
                ResolveStatus(running, unexpired),
                group.Max(e => (DateTimeOffset?)e.UpdatedAt),
                running,
                Version: null));
        }

        foreach (var roster in knownWorkers
            .Where(w => !string.IsNullOrWhiteSpace(w))
            .Select(w => w.Trim())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(w => w, StringComparer.Ordinal))
        {
            if (seen.Contains(roster))
            {
                continue;
            }

            workers.Add(new WorkerHealthDto(correlationId, roster, StatusUnknown, null, 0, Version: null));
        }

        return workers;
    }
}
