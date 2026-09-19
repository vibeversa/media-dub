using DubbingPlatform.Application.MultiTenancy;
using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Domain.Enums;
using DubbingPlatform.Domain.Exceptions;
using Microsoft.EntityFrameworkCore;

namespace DubbingPlatform.Application.Services;

/// <summary>
/// Exactly-once stage barrier over the <c>StageUnitCompletion</c> ledger plus
/// <c>RunStageSummary</c> counters. Duplicate completions hit the ledger unique
/// index <c>(processing_run_id, stage_type, scope_type, scope_id,
/// stage_execution_id)</c> and are ignored without incrementing. Live increments
/// use atomic conditional SQL (<c>... WHERE (completed+skipped) &lt; expected</c>)
/// so exactly one caller observes the barrier crossing even under concurrent
/// completions; that caller alone receives <c>StageComplete=true</c> and may
/// publish the stage-level completion. No <c>SELECT COUNT</c> is issued per
/// event: the single-row summary is read by key only. Expected counts are set
/// once up front via <see cref="EnsureSummaryAsync"/> (typically by the
/// dispatcher, which counts fan-out units a single time).
/// </summary>
public sealed class BarrierService
{
    private static readonly HashSet<string> TerminalStates = new(StringComparer.Ordinal)
    {
        "Completed", "Skipped", "Failed", "ManualReviewRequired", "Cancelled",
    };

    private readonly IStageExecutionContextFactory _contextFactory;

    public BarrierService(IStageExecutionContextFactory contextFactory)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        _contextFactory = contextFactory;
    }

    /// <summary>
    /// Creates the summary row for a stage barrier when missing (idempotent).
    /// An existing row keeps its expected count (first writer wins), except a
    /// placeholder <c>ExpectedUnits=0</c> (written by processing-start for
    /// fan-out stages) which the dispatcher's real count promotes exactly once.
    /// </summary>
    public async Task EnsureSummaryAsync(
        Guid tenantId,
        Guid runId,
        StageType stage,
        int expectedUnits,
        CancellationToken cancellationToken = default)
    {
        RequireTenant(tenantId);
        RequireId(runId, nameof(runId));
        if (expectedUnits < 0)
        {
            throw new DomainException("ExpectedUnits must be >= 0.");
        }

        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            var existing = await db.Set<RunStageSummary>()
                .FirstOrDefaultAsync(
                    s => s.ProcessingRunId == runId && s.StageType == stage,
                    cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                if (existing.ExpectedUnits == 0 && expectedUnits > 0)
                {
                    var promoteNow = DateTimeOffset.UtcNow;
                    await db.Database.ExecuteSqlRawAsync(
                        "UPDATE run_stage_summaries SET expected_units = {0}, updated_at = {1} " +
                        "WHERE tenant_id = {2} AND processing_run_id = {3} AND stage_type = {4} AND expected_units = 0",
                        expectedUnits, promoteNow, tenantId, runId, stage.ToString()).ConfigureAwait(false);
                }

                return;
            }

            var now = DateTimeOffset.UtcNow;
            db.Set<RunStageSummary>().Add(new RunStageSummary(
                Guid.NewGuid(), tenantId, runId, stage, expectedUnits,
                0, 0, 0, 0, 0, now, now));
            try
            {
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsUniqueViolation(ex))
            {
                // Lost the insert race; the winner's row stands.
            }
        }
    }

    /// <summary>
    /// Records one unit completion. Duplicate ledger inserts return
    /// <c>IsDuplicate=true</c> with no counter change and never complete the barrier.
    /// </summary>
    public async Task<BarrierResult> RecordUnitCompletionAsync(
        Guid tenantId,
        Guid runId,
        StageType stage,
        ScopeType scope,
        string scopeId,
        Guid executionId,
        string unitState,
        CancellationToken cancellationToken = default)
    {
        RequireTenant(tenantId);
        RequireId(runId, nameof(runId));
        if (string.IsNullOrWhiteSpace(scopeId))
        {
            throw new DomainException("ScopeId must not be empty.");
        }

        RequireId(executionId, nameof(executionId));
        if (!TerminalStates.Contains(unitState))
        {
            throw new DomainException($"UnitState '{unitState}' is not a terminal unit state.");
        }

        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            var now = DateTimeOffset.UtcNow;
            db.Set<StageUnitCompletion>().Add(new StageUnitCompletion(
                Guid.NewGuid(), tenantId, runId, stage, scope, scopeId, executionId, unitState, now));
            try
            {
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsUniqueViolation(ex))
            {
                db.ChangeTracker.Clear();
                var current = await LoadSummaryAsync(db, runId, stage, cancellationToken).ConfigureAwait(false);
                return new BarrierResult(
                    true, false,
                    current?.ExpectedUnits ?? 0,
                    current?.CompletedUnits ?? 0,
                    current?.SkippedUnits ?? 0);
            }

            var column = CounterColumnFor(unitState);
            var counted = string.Equals(unitState, "Completed", StringComparison.Ordinal)
                || string.Equals(unitState, "Skipped", StringComparison.Ordinal);

            // Guarded increment: only increments while the barrier is still open,
            // so exactly one concurrent caller observes the crossing.
            var guardedSql = counted
                ? $"UPDATE run_stage_summaries SET {column} = {column} + 1, updated_at = {{0}} " +
                  "WHERE tenant_id = {1} AND processing_run_id = {2} AND stage_type = {3} " +
                  "AND expected_units > 0 AND (completed_units + skipped_units) < expected_units"
                : $"UPDATE run_stage_summaries SET {column} = {column} + 1, updated_at = {{0}} " +
                  "WHERE tenant_id = {1} AND processing_run_id = {2} AND stage_type = {3}";

            var rows = await db.Database.ExecuteSqlRawAsync(
                guardedSql, now, tenantId, runId, stage.ToString()).ConfigureAwait(false);

            if (rows == 0)
            {
                var missing = await LoadSummaryAsync(db, runId, stage, cancellationToken).ConfigureAwait(false);
                if (missing is null)
                {
                    await InsertSummaryWithUnitAsync(db, tenantId, runId, stage, unitState, now, cancellationToken).ConfigureAwait(false);
                    return new BarrierResult(false, false, 0, 0, 0);
                }

                // Barrier already crossed (or expected is zero): no publish.
                db.ChangeTracker.Clear();
                var settled = await LoadSummaryAsync(db, runId, stage, cancellationToken).ConfigureAwait(false);
                return new BarrierResult(
                    false, false,
                    settled?.ExpectedUnits ?? 0,
                    settled?.CompletedUnits ?? 0,
                    settled?.SkippedUnits ?? 0);
            }

            db.ChangeTracker.Clear();
            var summary = await LoadSummaryAsync(db, runId, stage, cancellationToken).ConfigureAwait(false);
            if (summary is null)
            {
                return new BarrierResult(false, false, 0, 0, 0);
            }

            var complete = counted
                && summary.ExpectedUnits > 0
                && summary.CompletedUnits + summary.SkippedUnits == summary.ExpectedUnits;
            return new BarrierResult(false, complete, summary.ExpectedUnits, summary.CompletedUnits, summary.SkippedUnits);
        }
    }

    /// <summary>
    /// Loads the summary row for a stage barrier, or null when not initialized.
    /// </summary>
    public async Task<RunStageSummary?> GetSummaryAsync(
        Guid tenantId,
        Guid runId,
        StageType stage,
        CancellationToken cancellationToken = default)
    {
        RequireTenant(tenantId);
        RequireId(runId, nameof(runId));

        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            return await LoadSummaryAsync(db, runId, stage, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Whether every prerequisite stage of <paramref name="stage"/> is barrier-complete
    /// (expected &gt; 0 and completed+skipped covering expected).
    /// </summary>
    public async Task<bool> ArePrerequisitesCompleteAsync(
        Guid tenantId,
        Guid runId,
        StageType stage,
        IReadOnlyList<StageType> prerequisites,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prerequisites);
        RequireTenant(tenantId);
        RequireId(runId, nameof(runId));

        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            foreach (var prerequisite in prerequisites)
            {
                var summary = await LoadSummaryAsync(db, runId, prerequisite, cancellationToken).ConfigureAwait(false);
                if (summary is null
                    || summary.ExpectedUnits <= 0
                    || summary.CompletedUnits + summary.SkippedUnits < summary.ExpectedUnits)
                {
                    return false;
                }
            }

            return true;
        }
    }

    private static async Task<RunStageSummary?> LoadSummaryAsync(
        DbContext db,
        Guid runId,
        StageType stage,
        CancellationToken cancellationToken)
    {
        return await db.Set<RunStageSummary>()
            .AsNoTracking()
            .FirstOrDefaultAsync(
                s => s.ProcessingRunId == runId && s.StageType == stage,
                cancellationToken).ConfigureAwait(false);
    }

    private static async Task InsertSummaryWithUnitAsync(
        DbContext db,
        Guid tenantId,
        Guid runId,
        StageType stage,
        string unitState,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        db.Set<RunStageSummary>().Add(new RunStageSummary(
            Guid.NewGuid(), tenantId, runId, stage, 0,
            string.Equals(unitState, "Completed", StringComparison.Ordinal) ? 1 : 0,
            string.Equals(unitState, "Failed", StringComparison.Ordinal) ? 1 : 0,
            string.Equals(unitState, "Skipped", StringComparison.Ordinal) ? 1 : 0,
            string.Equals(unitState, "ManualReviewRequired", StringComparison.Ordinal) ? 1 : 0,
            string.Equals(unitState, "Cancelled", StringComparison.Ordinal) ? 1 : 0,
            now, now));
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsUniqueViolation(ex))
        {
            // A concurrent initializer won; our ledger row still stands alone and the
            // next completion for this stage repairs the counter. Safe to ignore.
            db.ChangeTracker.Clear();
        }
    }

    private static string CounterColumnFor(string unitState)
    {
        // Allow-listed column names only; unitState is validated against TerminalStates.
        return unitState switch
        {
            "Completed" => "completed_units",
            "Skipped" => "skipped_units",
            "Failed" => "failed_units",
            "ManualReviewRequired" => "review_units",
            "Cancelled" => "cancelled_units",
            _ => throw new DomainException($"UnitState '{unitState}' is not a terminal unit state."),
        };
    }

    private static bool IsUniqueViolation(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is DomainException domain
                && domain.Message.Contains("CONFLICT", StringComparison.Ordinal))
            {
                return true;
            }

            if (current is DbUpdateException)
            {
                return true;
            }
        }

        return false;
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
