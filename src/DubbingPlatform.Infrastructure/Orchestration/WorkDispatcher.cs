using DubbingPlatform.Application.Exceptions;
using DubbingPlatform.Application.MultiTenancy;
using DubbingPlatform.Application.Options;
using DubbingPlatform.Application.Services;
using DubbingPlatform.Contracts.Messages;
using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Domain.Enums;
using DubbingPlatform.Domain.Exceptions;
using DubbingPlatform.Infrastructure.Messaging;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DubbingPlatform.Infrastructure.Orchestration;

/// <summary>
/// Outcome of one dispatch call. <see cref="Reason"/> is a stable machine-readable
/// token: <c>ok</c>, <c>run-cancelling</c>, <c>concurrency-exhausted</c>,
/// <c>rate-limited</c>, <c>cost-blocked</c>, <c>no-pending-units</c>,
/// <c>scheduler-unavailable</c>.
/// </summary>
public sealed record DispatchResult(int Dispatched, int Skipped, string Reason);

/// <summary>
/// Bounded work dispatcher. Workers execute; this class (invoked by the saga and
/// recovery paths) decides global progression: it enumerates pending fan-out
/// units, caps bulk batches by tenant concurrency, consults the rate/cost gates,
/// and sends <see cref="StageWorkRequested"/> point-to-point to the stage's workload
/// queue. Single-unit sends (initials, retries, resumes, requeues) bypass the bulk
/// concurrency cap but still honor gates. Cancellation blocks all scheduling: a
/// Cancelling/Cancelled run yields zero dispatches without publishing. Execution
/// rows are created at claim time by workers, never here, so dispatch is safe to
/// retry (duplicate deliveries collapse on the claim unique index). Partial bulk
/// batches are completed by re-pumping on each unit completion (the saga
/// re-dispatches the same stage while its barrier stays open).
/// </summary>
public sealed class WorkDispatcher
{
    private readonly IStageExecutionContextFactory _contextFactory;
    private readonly ISendEndpointProvider _sendEndpoints;
    private readonly IDeferredSender _deferredSender;
    private readonly QuotaOptions _quota;
    private readonly IRateGate _rateGate;
    private readonly ICostGate _costGate;
    private readonly ContextBuilderService? _contexts;
    private readonly Redis.TenantFairnessGate? _fairness;
    private readonly ILogger<WorkDispatcher> _logger;

    public WorkDispatcher(
        IStageExecutionContextFactory contextFactory,
        ISendEndpointProvider sendEndpoints,
        IDeferredSender deferredSender,
        IOptions<QuotaOptions> quotaOptions,
        IRateGate rateGate,
        ICostGate costGate,
        ILogger<WorkDispatcher> logger,
        ContextBuilderService? contexts = null,
        Redis.TenantFairnessGate? fairness = null)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        ArgumentNullException.ThrowIfNull(sendEndpoints);
        ArgumentNullException.ThrowIfNull(deferredSender);
        ArgumentNullException.ThrowIfNull(quotaOptions);
        ArgumentNullException.ThrowIfNull(rateGate);
        ArgumentNullException.ThrowIfNull(costGate);
        ArgumentNullException.ThrowIfNull(logger);
        _contextFactory = contextFactory;
        _sendEndpoints = sendEndpoints;
        _deferredSender = deferredSender;
        _quota = quotaOptions.Value;
        _rateGate = rateGate;
        _costGate = costGate;
        _contexts = contexts;
        _fairness = fairness;
        _logger = logger;
    }

    /// <summary>
    /// Dispatches one project-scoped unit (scope id = project id) and initializes
    /// its barrier with <c>ExpectedUnits=1</c>.
    /// </summary>
    public Task<DispatchResult> DispatchSingleWorkAsync(
        Guid tenantId,
        Guid projectId,
        Guid runId,
        StageType stage,
        CancellationToken cancellationToken = default)
    {
        var units = new (ScopeType Scope, string ScopeId, Guid? SegmentId)[]
        {
            (ScopeType.Project, projectId.ToString("D"), null),
        };
        return DispatchCoreAsync(tenantId, projectId, runId, stage, units, expectedUnits: 1, delay: null, cancellationToken);
    }

    /// <summary>
    /// Dispatches a single unit (manual retries, review resumes, recovery requeues).
    /// The barrier must already exist; this method never changes expected counts.
    /// When <paramref name="delay"/> is set (rate-limit retries), delivery is
    /// deferred instead of immediate.
    /// </summary>
    public Task<DispatchResult> DispatchUnitAsync(
        Guid tenantId,
        Guid projectId,
        Guid runId,
        StageType stage,
        ScopeType scope,
        string scopeId,
        Guid? segmentId,
        int attempt,
        TimeSpan? delay = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(scopeId))
        {
            throw new DomainException("ScopeId must not be empty.");
        }

        if (attempt < 0)
        {
            throw new DomainException("Attempt must be >= 0.");
        }

        var units = new (ScopeType Scope, string ScopeId, Guid? SegmentId)[]
        {
            (scope, scopeId, segmentId),
        };
        return DispatchCoreAsync(tenantId, projectId, runId, stage, units, expectedUnits: null, delay, cancellationToken, attempt);
    }

    /// <summary>
    /// Dispatches up to <paramref name="batchSize"/> pending segment units for
    /// <paramref name="attempt"/> and initializes the barrier with the total
    /// segment count on first call.
    /// </summary>
    public async Task<DispatchResult> DispatchSegmentWorkAsync(
        Guid tenantId,
        Guid projectId,
        Guid runId,
        StageType stage,
        int batchSize = 50,
        int attempt = 0,
        CancellationToken cancellationToken = default)
    {
        if (batchSize < 1)
        {
            throw new DomainException("BatchSize must be >= 1.");
        }

        if (attempt < 0)
        {
            throw new DomainException("Attempt must be >= 0.");
        }

        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            var run = await LoadRunAsync(db, tenantId, projectId, runId, cancellationToken).ConfigureAwait(false);
            if (run.Status is ProcessingRunStatus.Cancelling or ProcessingRunStatus.Cancelled)
            {
                return new DispatchResult(0, 0, "run-cancelling");
            }

            var segmentIds = await db.Set<SpeechSegment>()
                .Where(s => s.RunId == runId)
                .OrderBy(s => s.Sequence)
                .Select(s => s.Id)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            var executed = await db.Set<StageExecution>()
                .Where(e => e.ProcessingRunId == runId && e.StageType == stage && e.Attempt == attempt)
                .Select(e => e.SegmentId)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            var executedSet = new HashSet<Guid>(executed.Where(id => id.HasValue).Select(id => id!.Value));

            var barrier = new BarrierService(_contextFactory);
            await barrier.EnsureSummaryAsync(tenantId, runId, stage, segmentIds.Count, cancellationToken).ConfigureAwait(false);

            var pending = segmentIds
                .Where(id => !executedSet.Contains(id))
                .Take(batchSize)
                .Select(id => (ScopeType.Segment, id.ToString("D"), (Guid?)id))
                .ToList();

            if (pending.Count == 0)
            {
                return new DispatchResult(0, 0, "no-pending-units");
            }

            return await DispatchUnitsAsync(
                tenantId, projectId, runId, stage, pending, run,
                enforceCap: true, delay: null, attempt, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Dispatches one unit per project speaker and initializes the barrier with
    /// the speaker count on first call.
    /// </summary>
    public async Task<DispatchResult> DispatchSpeakerWorkAsync(
        Guid tenantId,
        Guid projectId,
        Guid runId,
        StageType stage,
        int attempt = 0,
        CancellationToken cancellationToken = default)
    {
        if (attempt < 0)
        {
            throw new DomainException("Attempt must be >= 0.");
        }

        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            var run = await LoadRunAsync(db, tenantId, projectId, runId, cancellationToken).ConfigureAwait(false);
            if (run.Status is ProcessingRunStatus.Cancelling or ProcessingRunStatus.Cancelled)
            {
                return new DispatchResult(0, 0, "run-cancelling");
            }

            var speakerIds = await db.Set<Speaker>()
                .Where(s => s.ProjectId == projectId)
                .OrderBy(s => s.SpeakerKey)
                .Select(s => s.Id)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            var executedScopeIds = await ExecutedScopeIdsAsync(db, runId, stage, attempt, cancellationToken).ConfigureAwait(false);

            var barrier = new BarrierService(_contextFactory);
            await barrier.EnsureSummaryAsync(tenantId, runId, stage, speakerIds.Count, cancellationToken).ConfigureAwait(false);

            var pending = speakerIds
                .Where(id => !executedScopeIds.Contains(id.ToString("D")))
                .Select(id => (ScopeType.Speaker, id.ToString("D"), (Guid?)null))
                .ToList();

            if (pending.Count == 0)
            {
                return new DispatchResult(0, 0, "no-pending-units");
            }

            return await DispatchUnitsAsync(
                tenantId, projectId, runId, stage, pending, run,
                enforceCap: true, delay: null, attempt, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Dispatches one unit per run window and initializes the barrier with the
    /// window count on first call. For <c>ContextBuild</c>, windows are
    /// materialized lazily on first dispatch (no prior stage creates
    /// <c>ContextWindow</c> rows: segments and transcripts exist, but windows
    /// only exist once <see cref="ContextBuilderService.BuildWindowsAsync"/>
    /// partitions them, so dispatching without this step would always find
    /// zero windows and the pipeline would stall with Translation gated
    /// forever). The lazy build is idempotent and skipped when the service is
    /// not wired (explicit test harnesses); failures propagate so transport
    /// retry (transient) or fail-fast (permanent) applies via the saga.
    /// </summary>
    public async Task<DispatchResult> DispatchWindowWorkAsync(
        Guid tenantId,
        Guid projectId,
        Guid runId,
        StageType stage,
        int attempt = 0,
        CancellationToken cancellationToken = default)
    {
        if (attempt < 0)
        {
            throw new DomainException("Attempt must be >= 0.");
        }

        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            var run = await LoadRunAsync(db, tenantId, projectId, runId, cancellationToken).ConfigureAwait(false);
            if (run.Status is ProcessingRunStatus.Cancelling or ProcessingRunStatus.Cancelled)
            {
                return new DispatchResult(0, 0, "run-cancelling");
            }

            var windowIds = await db.Set<ContextWindow>()
                .Where(w => w.RunId == runId)
                .OrderBy(w => w.Sequence)
                .Select(w => w.Id)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            if (windowIds.Count == 0 && stage == StageType.ContextBuild && _contexts is not null)
            {
                await _contexts.BuildWindowsAsync(tenantId, projectId, runId, cancellationToken).ConfigureAwait(false);
                db.ChangeTracker.Clear();
                windowIds = await db.Set<ContextWindow>()
                    .Where(w => w.RunId == runId)
                    .OrderBy(w => w.Sequence)
                    .Select(w => w.Id)
                    .ToListAsync(cancellationToken).ConfigureAwait(false);
            }

            var executedScopeIds = await ExecutedScopeIdsAsync(db, runId, stage, attempt, cancellationToken).ConfigureAwait(false);

            var barrier = new BarrierService(_contextFactory);
            await barrier.EnsureSummaryAsync(tenantId, runId, stage, windowIds.Count, cancellationToken).ConfigureAwait(false);

            var pending = windowIds
                .Where(id => !executedScopeIds.Contains(id.ToString("D")))
                .Select(id => (ScopeType.Window, id.ToString("D"), (Guid?)null))
                .ToList();

            if (pending.Count == 0)
            {
                return new DispatchResult(0, 0, "no-pending-units");
            }

            return await DispatchUnitsAsync(
                tenantId, projectId, runId, stage, pending, run,
                enforceCap: true, delay: null, attempt, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<DispatchResult> DispatchCoreAsync(
        Guid tenantId,
        Guid projectId,
        Guid runId,
        StageType stage,
        IReadOnlyList<(ScopeType Scope, string ScopeId, Guid? SegmentId)> units,
        int? expectedUnits,
        TimeSpan? delay,
        CancellationToken cancellationToken,
        int attempt = 0)
    {
        if (tenantId == Guid.Empty)
        {
            throw new DomainException("TenantId must not be empty.");
        }

        if (projectId == Guid.Empty)
        {
            throw new DomainException("ProjectId must not be empty.");
        }

        if (runId == Guid.Empty)
        {
            throw new DomainException("ProcessingRunId must not be empty.");
        }

        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            var run = await LoadRunAsync(db, tenantId, projectId, runId, cancellationToken).ConfigureAwait(false);
            if (run.Status is ProcessingRunStatus.Cancelling or ProcessingRunStatus.Cancelled)
            {
                return new DispatchResult(0, units.Count, "run-cancelling");
            }

            if (expectedUnits.HasValue)
            {
                var barrier = new BarrierService(_contextFactory);
                await barrier.EnsureSummaryAsync(tenantId, runId, stage, expectedUnits.Value, cancellationToken).ConfigureAwait(false);
            }

            return await DispatchUnitsAsync(
                tenantId, projectId, runId, stage, units, run,
                enforceCap: false, delay, attempt, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<DispatchResult> DispatchUnitsAsync(
        Guid tenantId,
        Guid projectId,
        Guid runId,
        StageType stage,
        IReadOnlyList<(ScopeType Scope, string ScopeId, Guid? SegmentId)> units,
        ProcessingRun run,
        bool enforceCap,
        TimeSpan? delay,
        int attempt,
        CancellationToken cancellationToken)
    {
        using var db = _contextFactory.CreateDbContext();
        var take = units.Count;
        if (enforceCap)
        {
            var active = await db.Set<StageExecution>()
                .CountAsync(
                    e => e.Status == StageStatus.Scheduled
                        || e.Status == StageStatus.Running
                        || e.Status == StageStatus.RetryPending,
                    cancellationToken).ConfigureAwait(false);
            var capacity = Math.Max(0, _quota.MaxConcurrentStagesPerTenant - active);
            if (capacity == 0)
            {
                _logger.LogDebug(
                    "Dispatch blocked for run {RunId} stage {Stage}: concurrency exhausted ({Active}/{Max}).",
                    runId, stage, active, _quota.MaxConcurrentStagesPerTenant);
                return new DispatchResult(0, units.Count, "concurrency-exhausted");
            }

            take = Math.Min(units.Count, capacity);
        }
        var stageName = stage.ToString();
        if (_fairness is not null
            && !await _fairness.TryAcquireDispatchAsync(tenantId, take, cancellationToken).ConfigureAwait(false))
        {
            return new DispatchResult(0, units.Count, "concurrency-exhausted");
        }

        if (!await _rateGate.CanProceedAsync(tenantId, stageName, take, cancellationToken).ConfigureAwait(false))
        {
            return new DispatchResult(0, units.Count, "rate-limited");
        }

        if (!await _costGate.CanProceedAsync(tenantId, runId, stageName, take, cancellationToken).ConfigureAwait(false))
        {
            return new DispatchResult(0, units.Count, "cost-blocked");
        }

        var queue = WorkQueueRouter.QueueFor(stage);
        var dispatched = 0;
        foreach (var unit in units.Take(take))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var message = BuildWorkRequested(tenantId, projectId, runId, run, stage, unit.Scope, unit.ScopeId, unit.SegmentId, attempt);

            bool sent;
            if (delay.HasValue)
            {
                sent = await _deferredSender.SendDelayedAsync(queue, message, delay.Value, cancellationToken).ConfigureAwait(false);
                if (!sent)
                {
                    return new DispatchResult(dispatched, units.Count - dispatched, "scheduler-unavailable");
                }
            }
            else
            {
                var endpoint = await _sendEndpoints.GetSendEndpoint(new Uri($"queue:{queue}", UriKind.Absolute)).ConfigureAwait(false);
                await endpoint.Send(message, cancellationToken).ConfigureAwait(false);
            }

            dispatched++;
        }

        _logger.LogInformation(
            "Dispatched {Dispatched}/{Total} units of stage {Stage} for run {RunId} to {Queue}.",
            dispatched, units.Count, stage, runId, queue);
        return new DispatchResult(dispatched, units.Count - dispatched, "ok");
    }

    private static StageWorkRequested BuildWorkRequested(
        Guid tenantId,
        Guid projectId,
        Guid runId,
        ProcessingRun run,
        StageType stage,
        ScopeType scope,
        string scopeId,
        Guid? segmentId,
        int attempt)
    {
        var stageName = stage.ToString();
        var scopeName = scope.ToString();
        return new StageWorkRequested(
            Guid.NewGuid(),
            Guid.NewGuid().ToString("N"),
            tenantId,
            projectId,
            runId,
            null,
            stageName,
            scopeName,
            scopeId,
            segmentId,
            MessageVersionPolicy.CurrentVersion,
            DateTimeOffset.UtcNow,
            attempt,
            null,
            run.ConfigurationHash,
            run.ExecutionSnapshotHash,
            stageName,
            scopeName,
            scopeId,
            null);
    }

    private static async Task<ProcessingRun> LoadRunAsync(
        DbContext db,
        Guid tenantId,
        Guid projectId,
        Guid runId,
        CancellationToken cancellationToken)
    {
        var run = await db.Set<ProcessingRun>()
            .FirstOrDefaultAsync(r => r.Id == runId, cancellationToken).ConfigureAwait(false);
        if (run is null)
        {
            throw new NotFoundException($"Processing run '{runId}' was not found.");
        }

        if (run.TenantId != tenantId || run.ProjectId != projectId)
        {
            throw new ForbiddenException($"Processing run '{runId}' does not belong to the current tenant/project.");
        }

        return run;
    }

    private static async Task<HashSet<string>> ExecutedScopeIdsAsync(
        DbContext db,
        Guid runId,
        StageType stage,
        int attempt,
        CancellationToken cancellationToken)
    {
        var scopeIds = await db.Set<StageExecution>()
            .Where(e => e.ProcessingRunId == runId && e.StageType == stage && e.Attempt == attempt)
            .Select(e => e.ScopeId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return new HashSet<string>(scopeIds, StringComparer.Ordinal);
    }
}
