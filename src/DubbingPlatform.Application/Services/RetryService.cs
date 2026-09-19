using DubbingPlatform.Application.Exceptions;
using DubbingPlatform.Application.MultiTenancy;
using DubbingPlatform.Application.Options;
using DubbingPlatform.Application.Orchestration;
using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Domain.Enums;
using DubbingPlatform.Domain.Exceptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DubbingPlatform.Application.Services;

/// <summary>
/// Descriptor for one manual retry: what to re-dispatch and at which attempt.
/// The controller publishes <c>StageWorkRequested</c> best effort from this
/// descriptor; the claim unique index collapses duplicate deliveries.
/// </summary>
public sealed record RetryDescriptor(
    Guid RunId,
    StageType Stage,
    ScopeType Scope,
    string ScopeId,
    Guid? SegmentId,
    int Attempt,
    IReadOnlyList<string> InvalidatedStages);

/// <summary>
/// Dependency-aware manual retry. <c>scope</c> is <c>stage|segment</c>:
/// <c>stage</c> re-runs every unit of the stage, <c>segment</c> re-runs one
/// segment unit; both bump to a fresh stage-execution <c>Attempt</c>
/// (<c>max+1</c>, capped by <c>Retry:ManualRetryMaxAttempts</c>, else 429
/// <c>QUOTA_EXCEEDED</c>) and invalidate only transitive downstream dependents
/// per <see cref="StageGraph.GetSuccessors"/> — upstream stages are never
/// touched. Invalidation resets the <c>RunStageSummary</c> counters
/// (Completed/Failed/Skipped/Review/Cancelled → 0, <c>ExpectedUnits</c>
/// preserved) for the retried stage plus dependents so fresh completions
/// re-cross the barrier; old <c>StageUnitCompletion</c> rows and artifacts stay
/// immutable history, and selected transcript/translation refs change only when
/// the new attempt succeeds (worker-owned selection). Cost/rate enforcement
/// stays dispatch-time (<c>WorkDispatcher</c> gates, Task 036-owned); this
/// service enforces only the manual-attempt budget plus cancellation guards.
/// Retry on <c>Cancelling/Cancelled</c> → 409; unknown segment → 404.
/// Auditing stays controller-owned (<c>processing.retry</c>); only ids, stages,
/// scopes, and attempts are logged — never media or text.
/// </summary>
public sealed class RetryService
{
    /// <summary>Audit action emitted by the controller for retries.</summary>
    public const string AuditAction = "processing.retry";

    private static readonly ProcessingRunStatus[] RetriableStatuses =
    [
        ProcessingRunStatus.Running,
        ProcessingRunStatus.ManualReviewRequired,
        ProcessingRunStatus.Failed,
    ];

    private readonly IStageExecutionContextFactory _contextFactory;
    private readonly RetryOptions _retry;

    public RetryService(
        IStageExecutionContextFactory contextFactory,
        IOptions<RetryOptions> retryOptions)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        ArgumentNullException.ThrowIfNull(retryOptions);
        _contextFactory = contextFactory;
        _retry = retryOptions.Value;
    }

    /// <summary>
    /// Plans and records a manual retry, returning the dispatch descriptor.
    /// </summary>
    public async Task<RetryDescriptor> RetryAsync(
        Guid tenantId,
        Guid projectId,
        string scope,
        string stageType,
        Guid? segmentId,
        CancellationToken cancellationToken = default)
    {
        RequireTenant(tenantId);
        RequireId(projectId, nameof(projectId));
        var parsedScope = ParseScope(scope);
        var stage = ParseStage(stageType);

        if (parsedScope == RetryScope.Segment && segmentId is null)
        {
            throw new DomainException("SegmentId is required for segment-scope retries.");
        }

        if (parsedScope == RetryScope.Stage && segmentId is not null)
        {
            throw new DomainException("SegmentId must not be set for stage-scope retries.");
        }

        await RequireProjectAsync(tenantId, projectId, cancellationToken).ConfigureAwait(false);
        var run = await FindRunAsync(tenantId, projectId, cancellationToken).ConfigureAwait(false);
        if (run is null)
        {
            throw new NotFoundException($"Project '{projectId}' has no processing run to retry.");
        }

        if (run.Status is ProcessingRunStatus.Cancelling or ProcessingRunStatus.Cancelled)
        {
            throw new ConflictException($"Run '{run.Id}' is {run.Status}; retry is not available on cancelled runs.");
        }

        if (!RetriableStatuses.Contains(run.Status))
        {
            throw new ConflictException($"Run '{run.Id}' is {run.Status} and cannot be retried selectively.");
        }

        var node = StageGraph.NodeOf(stage);
        string scopeId;
        ScopeType dispatchScope;
        Guid? dispatchSegmentId = null;
        if (parsedScope == RetryScope.Segment)
        {
            var segment = await LoadSegmentAsync(tenantId, projectId, run.Id, segmentId!.Value, cancellationToken).ConfigureAwait(false);
            if (!IsSegmentStage(node))
            {
                throw new DomainException($"Stage '{stage}' is not segment-scoped; use scope 'stage'.");
            }

            scopeId = segment.Id.ToString("D");
            dispatchScope = ScopeType.Segment;
            dispatchSegmentId = segment.Id;
        }
        else
        {
            dispatchScope = node.Scope;
            scopeId = node.Scope switch
            {
                ScopeType.Project or ScopeType.Run => projectId.ToString("D"),
                ScopeType.Segment => throw new DomainException($"Stage '{stage}' is segment-scoped; use scope 'segment' with a segment id."),
                ScopeType.Speaker => throw new DomainException($"Stage '{stage}' is speaker-scoped; retry it via the run-level retry after requeue."),
                ScopeType.Window => throw new DomainException($"Stage '{stage}' is window-scoped; retry it via the run-level retry after requeue."),
                _ => projectId.ToString("D"),
            };
        }

        var attempt = await NextAttemptAsync(tenantId, run.Id, stage, cancellationToken).ConfigureAwait(false);
        var maxManual = _retry.ManualRetryMaxAttempts;
        if (attempt > maxManual)
        {
            throw new QuotaExceededException(
                $"Stage '{stage}' manual retry budget exhausted (attempt {attempt} exceeds max {maxManual}).");
        }

        var invalidated = GetTransitiveDependents(stage);
        await ResetSummariesAsync(tenantId, run.Id, stage, invalidated, cancellationToken).ConfigureAwait(false);

        if (run.Status == ProcessingRunStatus.Failed)
        {
            await ResumeFailedRunAsync(tenantId, run.Id, cancellationToken).ConfigureAwait(false);
        }

        return new RetryDescriptor(
            run.Id, stage, dispatchScope, scopeId, dispatchSegmentId, attempt,
            invalidated.Select(s => s.ToString()).ToList());
    }

    /// <summary>
    /// Retry scope values. Pure parsing via <see cref="ParseScope"/>.
    /// </summary>
    public enum RetryScope
    {
        Stage,
        Segment,
    }

    /// <summary>
    /// Parses a retry scope (<c>stage|segment</c>, case-insensitive). Pure.
    /// </summary>
    public static RetryScope ParseScope(string? scope)
    {
        if (string.Equals((scope ?? string.Empty).Trim(), "stage", StringComparison.OrdinalIgnoreCase))
        {
            return RetryScope.Stage;
        }

        if (string.Equals((scope ?? string.Empty).Trim(), "segment", StringComparison.OrdinalIgnoreCase))
        {
            return RetryScope.Segment;
        }

        throw new DomainException("Scope must be 'stage' or 'segment'.");
    }

    /// <summary>
    /// Parses a stage name (case-insensitive, normalized to the ordinal enum
    /// name). Pure.
    /// </summary>
    public static StageType ParseStage(string? stageType)
    {
        if (!string.IsNullOrWhiteSpace(stageType)
            && Enum.TryParse<StageType>((stageType ?? string.Empty).Trim(), ignoreCase: true, out var stage)
            && string.Equals(stage.ToString(), (stageType ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return stage;
        }

        throw new DomainException($"Unknown stage '{stageType}'.");
    }

    /// <summary>
    /// Gets the transitive downstream dependents of a stage in pipeline order
    /// (direct successors first, breadth-first). Pure. Upstream stages are
    /// never included.
    /// </summary>
    public static IReadOnlyList<StageType> GetTransitiveDependents(StageType stage)
    {
        var ordered = new List<StageType>();
        var seen = new HashSet<StageType> { stage };
        var frontier = new Queue<StageType>();
        frontier.Enqueue(stage);
        while (frontier.Count > 0)
        {
            var current = frontier.Dequeue();
            foreach (var successor in StageGraph.GetSuccessors(current))
            {
                if (seen.Add(successor.StageType))
                {
                    ordered.Add(successor.StageType);
                    frontier.Enqueue(successor.StageType);
                }
            }
        }

        return ordered;
    }

    /// <summary>
    /// Whether a stage fans out per segment. Pure.
    /// </summary>
    public static bool IsSegmentStage(StageNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return node.Scope == ScopeType.Segment;
    }

    private async Task<int> NextAttemptAsync(Guid tenantId, Guid runId, StageType stage, CancellationToken cancellationToken)
    {
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            var max = await db.Set<StageExecution>()
                .Where(e => e.ProcessingRunId == runId && e.StageType == stage)
                .MaxAsync(e => (int?)e.Attempt, cancellationToken).ConfigureAwait(false);
            return (max ?? -1) + 1;
        }
    }

    private async Task ResetSummariesAsync(
        Guid tenantId,
        Guid runId,
        StageType stage,
        IReadOnlyList<StageType> dependents,
        CancellationToken cancellationToken)
    {
        var stages = new List<string> { stage.ToString() };
        stages.AddRange(dependents.Select(s => s.ToString()));
        var now = DateTimeOffset.UtcNow;
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            foreach (var name in stages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await db.Database.ExecuteSqlRawAsync(
                    "UPDATE run_stage_summaries SET completed_units = 0, failed_units = 0, skipped_units = 0, review_units = 0, cancelled_units = 0, updated_at = {0} " +
                    "WHERE processing_run_id = {1} AND stage_type = {2}",
                    now, runId, name).ConfigureAwait(false);
            }
        }
    }

    private async Task ResumeFailedRunAsync(Guid tenantId, Guid runId, CancellationToken cancellationToken)
    {
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            var now = DateTimeOffset.UtcNow;
            await db.Database.ExecuteSqlRawAsync(
                "UPDATE processing_runs SET status = {0}, updated_at = {1}, completed_at = NULL " +
                "WHERE id = {2} AND tenant_id = {3} AND status = 'Failed'",
                ProcessingRunStatus.Running.ToString(), now, runId, tenantId).ConfigureAwait(false);
        }
    }

    private async Task<ProcessingRun?> FindRunAsync(Guid tenantId, Guid projectId, CancellationToken cancellationToken)
    {
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            var active = await db.Set<ProcessingRun>()
                .AsNoTracking()
                .Where(r => r.ProjectId == projectId
                    && (r.Status == ProcessingRunStatus.Pending
                        || r.Status == ProcessingRunStatus.Running
                        || r.Status == ProcessingRunStatus.Cancelling
                        || r.Status == ProcessingRunStatus.ManualReviewRequired))
                .OrderByDescending(r => r.CreatedAt)
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (active is not null)
            {
                return active;
            }

            return await db.Set<ProcessingRun>()
                .AsNoTracking()
                .Where(r => r.ProjectId == projectId)
                .OrderByDescending(r => r.CreatedAt)
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<SpeechSegment> LoadSegmentAsync(
        Guid tenantId,
        Guid projectId,
        Guid runId,
        Guid segmentId,
        CancellationToken cancellationToken)
    {
        SpeechSegment? segment;
        using (TenantContext.BeginMaintenanceScope())
        {
            using var db = _contextFactory.CreateDbContext();
            segment = await db.Set<SpeechSegment>()
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.Id == segmentId, cancellationToken).ConfigureAwait(false);
        }

        if (segment is null)
        {
            throw new NotFoundException($"Segment '{segmentId}' was not found.");
        }

        if (segment.TenantId != tenantId || segment.ProjectId != projectId || segment.RunId != runId)
        {
            throw new NotFoundException($"Segment '{segmentId}' was not found.");
        }

        return segment;
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
}
