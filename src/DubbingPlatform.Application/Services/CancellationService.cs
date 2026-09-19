using DubbingPlatform.Application.Exceptions;
using DubbingPlatform.Application.MultiTenancy;
using DubbingPlatform.Application.StateMachines;
using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Domain.Enums;
using DubbingPlatform.Domain.Exceptions;
using Microsoft.EntityFrameworkCore;

namespace DubbingPlatform.Application.Services;

/// <summary>
/// Result of a cancellation request: the run that is now draining.
/// </summary>
public sealed record CancellationResult(Guid RunId, string Status, bool WasAlreadyCancelling);

/// <summary>
/// Durable cancellation. <see cref="CancelAsync"/> validates the run is
/// cancellable (<c>Pending|Running|ManualReviewRequired</c>; <c>Cancelling</c>
/// is idempotent), flips it to <c>Cancelling</c> with a conditional update so
/// a cancel racing completion yields exactly one terminal state, and returns.
/// The caller publishes <c>RunCancelledRequested</c> best effort; the saga owns
/// the <c>Cancelling</c> state. New scheduling is blocked immediately by the
/// <c>Cancelling</c> status (<see cref="StageExecutionService"/> claim guard
/// plus <c>WorkDispatcher</c> dispatch guard); lease-holding workers observe
/// the flag before commit and abort with <c>LeaseLostException</c>; media jobs
/// share the MassTransit <c>CancellationToken</c> so in-flight FFmpeg work
/// cancels cooperatively. The run reaches <c>Cancelled</c> only via
/// <see cref="FinalizeIfIdleAsync"/> (sweeper-owned) once no
/// <c>Scheduled|Running|RetryPending</c> executions remain — durable clean, no
/// orphaned leases. Auditing stays controller-owned (matching the existing
/// <c>processing.cancel</c> audit); this service never logs the reason text
/// beyond its length class — only ids and statuses.
/// </summary>
public sealed class CancellationService
{
    /// <summary>Audit action emitted by the controller for cancellations.</summary>
    public const string AuditAction = "processing.cancel";

    private static readonly ProcessingRunStatus[] CancellableStatuses =
    [
        ProcessingRunStatus.Pending,
        ProcessingRunStatus.Running,
        ProcessingRunStatus.ManualReviewRequired,
    ];

    private readonly IStageExecutionContextFactory _contextFactory;

    public CancellationService(IStageExecutionContextFactory contextFactory)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        _contextFactory = contextFactory;
    }

    /// <summary>
    /// Whether a run status accepts cancellation. Pure.
    /// <c>Cancelling</c> is accepted for idempotency (returns the draining run).
    /// </summary>
    public static bool IsCancellableStatus(ProcessingRunStatus status)
    {
        return status is ProcessingRunStatus.Pending
            or ProcessingRunStatus.Running
            or ProcessingRunStatus.ManualReviewRequired
            or ProcessingRunStatus.Cancelling;
    }

    /// <summary>
    /// Marks the active run <c>Cancelling</c>. Throws <c>NotFoundException</c>
    /// when no active run exists and <c>ConflictException</c> (409) when the
    /// latest run is already terminal. A lost conditional-update race reloads:
    /// terminal wins (409), <c>Cancelling</c> returns idempotently — exactly
    /// one terminal state, never both Completed and Cancelled.
    /// </summary>
    public async Task<CancellationResult> CancelAsync(
        Guid tenantId,
        Guid projectId,
        string? reason,
        CancellationToken cancellationToken = default)
    {
        RequireTenant(tenantId);
        RequireId(projectId, nameof(projectId));

        await RequireProjectAsync(tenantId, projectId, cancellationToken).ConfigureAwait(false);
        var run = await FindActiveRunAsync(tenantId, projectId, cancellationToken).ConfigureAwait(false);
        if (run is null)
        {
            var latest = await FindLatestRunAsync(tenantId, projectId, cancellationToken).ConfigureAwait(false);
            if (latest is null)
            {
                throw new NotFoundException($"Project '{projectId}' has no active processing run to cancel.");
            }

            throw new ConflictException($"Run '{latest.Id}' is already {latest.Status} and cannot be cancelled.");
        }

        if (!IsCancellableStatus(run.Status))
        {
            throw new ConflictException($"Run '{run.Id}' is {run.Status} and cannot be cancelled.");
        }

        if (run.Status == ProcessingRunStatus.Cancelling)
        {
            return new CancellationResult(run.Id, run.Status.ToString(), true);
        }

        RunStateMachine.EnsureCanTransition(run.Status, ProcessingRunStatus.Cancelling);

        var now = DateTimeOffset.UtcNow;
        int rows;
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            rows = await db.Database.ExecuteSqlRawAsync(
                "UPDATE processing_runs SET status = {0}, updated_at = {1} " +
                "WHERE id = {2} AND tenant_id = {3} AND status IN ('Pending', 'Running', 'ManualReviewRequired', 'Cancelling')",
                ProcessingRunStatus.Cancelling.ToString(), now,
                run.Id, tenantId).ConfigureAwait(false);
        }

        if (rows == 0)
        {
            var current = await FindRunAsync(tenantId, run.Id, cancellationToken).ConfigureAwait(false);
            if (current is null)
            {
                throw new NotFoundException($"Processing run '{run.Id}' was not found.");
            }

            if (current.Status == ProcessingRunStatus.Cancelling)
            {
                return new CancellationResult(current.Id, current.Status.ToString(), true);
            }

            throw new ConflictException($"Run '{run.Id}' is now {current.Status}; cancel lost the race with completion.");
        }

        return new CancellationResult(run.Id, ProcessingRunStatus.Cancelling.ToString(), false);
    }

    /// <summary>
    /// Sweeper-owned durable completion: flips a <c>Cancelling</c> run to
    /// <c>Cancelled</c> only when zero <c>Scheduled|Running|RetryPending</c>
    /// executions remain for the run. Returns true when this call finalized.
    /// Safe to call on every sweep (conditional SQL, idempotent).
    /// </summary>
    public async Task<bool> FinalizeIfIdleAsync(
        Guid tenantId,
        Guid runId,
        CancellationToken cancellationToken = default)
    {
        RequireTenant(tenantId);
        RequireId(runId, nameof(runId));

        var now = DateTimeOffset.UtcNow;
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            var rows = await db.Database.ExecuteSqlRawAsync(
                "UPDATE processing_runs SET status = {0}, updated_at = {1}, completed_at = {1} " +
                "WHERE id = {2} AND tenant_id = {3} AND status = 'Cancelling' " +
                "AND NOT EXISTS (SELECT 1 FROM stage_executions WHERE processing_run_id = {2} AND status IN ('Scheduled', 'Running', 'RetryPending'))",
                ProcessingRunStatus.Cancelled.ToString(), now,
                runId, tenantId).ConfigureAwait(false);
            return rows > 0;
        }
    }

    private async Task<ProcessingRun?> FindActiveRunAsync(Guid tenantId, Guid projectId, CancellationToken cancellationToken)
    {
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            return await db.Set<ProcessingRun>()
                .AsNoTracking()
                .Where(r => r.ProjectId == projectId
                    && (r.Status == ProcessingRunStatus.Pending
                        || r.Status == ProcessingRunStatus.Running
                        || r.Status == ProcessingRunStatus.Cancelling
                        || r.Status == ProcessingRunStatus.ManualReviewRequired))
                .OrderByDescending(r => r.CreatedAt)
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<ProcessingRun?> FindLatestRunAsync(Guid tenantId, Guid projectId, CancellationToken cancellationToken)
    {
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            return await db.Set<ProcessingRun>()
                .AsNoTracking()
                .Where(r => r.ProjectId == projectId)
                .OrderByDescending(r => r.CreatedAt)
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<ProcessingRun?> FindRunAsync(Guid tenantId, Guid runId, CancellationToken cancellationToken)
    {
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            return await db.Set<ProcessingRun>()
                .AsNoTracking()
                .FirstOrDefaultAsync(r => r.Id == runId, cancellationToken).ConfigureAwait(false);
        }
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
