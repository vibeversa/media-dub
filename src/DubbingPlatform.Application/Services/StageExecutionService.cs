using System.Text.Json;
using DubbingPlatform.Application.Exceptions;
using DubbingPlatform.Application.MultiTenancy;
using DubbingPlatform.Application.Options;
using DubbingPlatform.Application.StateMachines;
using DubbingPlatform.Contracts.Messages;
using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Domain.Enums;
using DubbingPlatform.Domain.Exceptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DubbingPlatform.Application.Services;

/// <summary>
/// Atomic stage scheduling, claiming, lease fencing, and stale recovery.
/// All methods are idempotent where noted and use lease-fenced conditional SQL
/// for commits so stale workers affect zero rows (throwing
/// <see cref="LeaseLostException"/>). Claim paths rely on the unique index
/// <c>(processing_run_id, stage_type, scope_type, scope_id, attempt)</c>:
/// a duplicate insert returns the pre-existing row with <c>IsNew=false</c>,
/// taking over its lease when the previous lease already expired (fenced on the
/// old owner+token so concurrent claimants serialize; active leases are never
/// stolen and the dead worker stays fenced out by token rotation).
/// Callers must establish the <c>TenantContext</c> scope before invoking;
/// each method re-asserts its tenant scope and creates short-lived contexts
/// via <see cref="IStageExecutionContextFactory"/> so captured tenant filters
/// and row-level security are always correct.
/// </summary>
public sealed class StageExecutionService
{
    private readonly IStageExecutionContextFactory _contextFactory;
    private readonly RetryOptions _retryOptions;

    public StageExecutionService(
        IStageExecutionContextFactory contextFactory,
        IOptions<RetryOptions> retryOptions)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        ArgumentNullException.ThrowIfNull(retryOptions);
        _contextFactory = contextFactory;
        _retryOptions = retryOptions.Value;
    }

    public Task<StageClaimResult> ScheduleAsync(
        Guid tenantId,
        Guid projectId,
        Guid runId,
        string stageType,
        string scopeType,
        string scopeId,
        Guid? segmentId,
        int attempt,
        string owner,
        TimeSpan leaseTtl,
        CancellationToken cancellationToken = default)
    {
        return ClaimCoreAsync(
            tenantId,
            projectId,
            runId,
            stageType,
            scopeType,
            scopeId,
            segmentId,
            attempt,
            owner,
            leaseTtl,
            StageStatus.Scheduled,
            startImmediately: false,
            cancellationToken);
    }

    public Task<StageClaimResult> ClaimAsync(
        Guid tenantId,
        Guid projectId,
        Guid runId,
        string stageType,
        string scopeType,
        string scopeId,
        Guid? segmentId,
        int attempt,
        string owner,
        TimeSpan leaseTtl,
        CancellationToken cancellationToken = default)
    {
        return ClaimCoreAsync(
            tenantId,
            projectId,
            runId,
            stageType,
            scopeType,
            scopeId,
            segmentId,
            attempt,
            owner,
            leaseTtl,
            StageStatus.Running,
            startImmediately: true,
            cancellationToken);
    }

    public async Task<StageExecution> StartAsync(
        Guid tenantId,
        Guid executionId,
        string owner,
        string token,
        CancellationToken cancellationToken = default)
    {
        RequireTenant(tenantId);
        RequireId(executionId, nameof(executionId));
        RequireOwner(owner);
        RequireToken(token);

        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            var execution = await LoadExecutionAsync(db, executionId, cancellationToken).ConfigureAwait(false);
            EnsureTenant(execution.TenantId, tenantId);
            EnsureLeaseOwner(execution, owner, token);

            StageStateMachine.EnsureCanTransition(execution.Status, StageStatus.Running);

            if (execution.Status == StageStatus.Running)
            {
                return execution;
            }

            var now = DateTimeOffset.UtcNow;
            var rows = await db.Database.ExecuteSqlRawAsync(
                "UPDATE stage_executions SET status = {0}, started_at = {1}, updated_at = {2}, lease_expires_at = {3} " +
                "WHERE id = {4} AND lease_owner = {5} AND lease_token = {6} AND status = 'Scheduled'",
                StageStatus.Running.ToString(),
                now,
                now,
                now.Add(TimeSpan.FromMinutes(5)),
                executionId,
                owner,
                token).ConfigureAwait(false);

            if (rows == 0)
            {
                throw new LeaseLostException($"Lease lost for stage execution '{executionId}'.");
            }

            db.ChangeTracker.Clear();
            return await LoadExecutionAsync(db, executionId, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<StageExecution> CompleteAsync(
        Guid tenantId,
        Guid executionId,
        string owner,
        string token,
        string[] outputArtifactIds,
        CancellationToken cancellationToken = default)
    {
        RequireTenant(tenantId);
        RequireId(executionId, nameof(executionId));
        RequireOwner(owner);
        RequireToken(token);
        ArgumentNullException.ThrowIfNull(outputArtifactIds);

        var outputJson = JsonSerializer.Serialize(outputArtifactIds, MessagingJson.Options);
        var now = DateTimeOffset.UtcNow;

        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            var rows = await db.Database.ExecuteSqlRawAsync(
                StageExecutionSql.CompleteSql,
                StageStatus.Completed.ToString(),
                now,
                now,
                outputJson,
                executionId,
                owner,
                token).ConfigureAwait(false);

            if (rows == 0)
            {
                throw new LeaseLostException($"Lease lost for stage execution '{executionId}'.");
            }

            db.ChangeTracker.Clear();
            return await LoadExecutionAsync(db, executionId, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<StageExecution> FailAsync(
        Guid tenantId,
        Guid executionId,
        string owner,
        string token,
        string errorCode,
        string errorMessage,
        CancellationToken cancellationToken = default)
    {
        RequireTenant(tenantId);
        RequireId(executionId, nameof(executionId));
        RequireOwner(owner);
        RequireToken(token);
        if (string.IsNullOrWhiteSpace(errorCode))
        {
            throw new DomainException("ErrorCode must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(errorMessage))
        {
            throw new DomainException("ErrorMessage must not be empty.");
        }

        var now = DateTimeOffset.UtcNow;

        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            var rows = await db.Database.ExecuteSqlRawAsync(
                StageExecutionSql.FailSql,
                StageStatus.Failed.ToString(),
                now,
                now,
                errorCode,
                errorMessage,
                executionId,
                owner,
                token).ConfigureAwait(false);

            if (rows == 0)
            {
                throw new LeaseLostException($"Lease lost for stage execution '{executionId}'.");
            }

            db.ChangeTracker.Clear();
            return await LoadExecutionAsync(db, executionId, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Skips a Running execution lease-fenced (conditionally-skipped units such
    /// as disabled source separation). Stores the selected output artifacts
    /// (first entry is the downstream-selected id) and the skip reason in
    /// <c>ErrorMessage</c> for audit; <c>ErrorCode</c> stays null (skip is not
    /// a failure). Throws <see cref="LeaseLostException"/> when the lease is
    /// stale.
    /// </summary>
    public async Task<StageExecution> SkipAsync(
        Guid tenantId,
        Guid executionId,
        string owner,
        string token,
        string[] outputArtifactIds,
        string skipReason,
        CancellationToken cancellationToken = default)
    {
        RequireTenant(tenantId);
        RequireId(executionId, nameof(executionId));
        RequireOwner(owner);
        RequireToken(token);
        ArgumentNullException.ThrowIfNull(outputArtifactIds);
        if (string.IsNullOrWhiteSpace(skipReason))
        {
            throw new DomainException("SkipReason must not be empty.");
        }

        var outputJson = JsonSerializer.Serialize(outputArtifactIds, MessagingJson.Options);
        var now = DateTimeOffset.UtcNow;

        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            var rows = await db.Database.ExecuteSqlRawAsync(
                "UPDATE stage_executions SET status = {0}, completed_at = {1}, updated_at = {2}, output_artifact_ids_json = {3}, error_message = {4} " +
                "WHERE id = {5} AND lease_owner = {6} AND lease_token = {7} AND status = 'Running'",
                StageStatus.Skipped.ToString(),
                now,
                now,
                outputJson,
                TruncateMessage(skipReason),
                executionId,
                owner,
                token).ConfigureAwait(false);

            if (rows == 0)
            {
                throw new LeaseLostException($"Lease lost for stage execution '{executionId}'.");
            }

            db.ChangeTracker.Clear();
            return await LoadExecutionAsync(db, executionId, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Records fallback audit on an already-Completed execution (for example
    /// separation low-confidence fallback to canonical). Sets
    /// <c>ErrorCode</c>/<c>ErrorMessage</c> lease-fenced on the Completed
    /// status so stale workers cannot overwrite the winner; the output ids
    /// (first entry is the selected artifact) are left untouched. Throws
    /// <see cref="LeaseLostException"/> when the lease is stale or the
    /// execution is not Completed.
    /// </summary>
    public async Task<StageExecution> NoteFallbackAsync(
        Guid tenantId,
        Guid executionId,
        string owner,
        string token,
        string fallbackCode,
        string fallbackReason,
        CancellationToken cancellationToken = default)
    {
        RequireTenant(tenantId);
        RequireId(executionId, nameof(executionId));
        RequireOwner(owner);
        RequireToken(token);
        if (string.IsNullOrWhiteSpace(fallbackCode))
        {
            throw new DomainException("FallbackCode must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(fallbackReason))
        {
            throw new DomainException("FallbackReason must not be empty.");
        }

        var now = DateTimeOffset.UtcNow;

        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            var rows = await db.Database.ExecuteSqlRawAsync(
                "UPDATE stage_executions SET error_code = {0}, error_message = {1}, updated_at = {2} " +
                "WHERE id = {3} AND lease_owner = {4} AND lease_token = {5} AND status = 'Completed'",
                fallbackCode.Trim(),
                TruncateMessage(fallbackReason),
                now,
                executionId,
                owner,
                token).ConfigureAwait(false);

            if (rows == 0)
            {
                throw new LeaseLostException($"Lease lost for stage execution '{executionId}'.");
            }

            db.ChangeTracker.Clear();
            return await LoadExecutionAsync(db, executionId, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<StageExecution> CancelAsync(
        Guid tenantId,
        Guid executionId,
        CancellationToken cancellationToken = default)
    {
        RequireTenant(tenantId);
        RequireId(executionId, nameof(executionId));

        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            var execution = await LoadExecutionAsync(db, executionId, cancellationToken).ConfigureAwait(false);
            EnsureTenant(execution.TenantId, tenantId);

            if (execution.Status is StageStatus.Completed or StageStatus.Failed or StageStatus.Cancelled or StageStatus.Skipped)
            {
                throw new ConflictException($"Stage execution '{executionId}' is already terminal ('{execution.Status}').");
            }

            var now = DateTimeOffset.UtcNow;
            var rows = await db.Database.ExecuteSqlRawAsync(
                "UPDATE stage_executions SET status = {0}, completed_at = {1}, updated_at = {1} " +
                "WHERE id = {2} AND status <> 'Completed' AND status <> 'Failed' AND status <> 'Cancelled' AND status <> 'Skipped'",
                StageStatus.Cancelled.ToString(),
                now,
                executionId).ConfigureAwait(false);

            if (rows == 0)
            {
                throw new ConflictException($"Stage execution '{executionId}' could not be cancelled.");
            }

            db.ChangeTracker.Clear();
            return await LoadExecutionAsync(db, executionId, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<StageExecution> MarkReviewRequiredAsync(
        Guid tenantId,
        Guid executionId,
        string owner,
        string token,
        CancellationToken cancellationToken = default)
    {
        RequireTenant(tenantId);
        RequireId(executionId, nameof(executionId));
        RequireOwner(owner);
        RequireToken(token);

        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            var execution = await LoadExecutionAsync(db, executionId, cancellationToken).ConfigureAwait(false);
            EnsureTenant(execution.TenantId, tenantId);
            EnsureLeaseOwner(execution, owner, token);
            StageStateMachine.EnsureCanTransition(execution.Status, StageStatus.ManualReviewRequired);

            var now = DateTimeOffset.UtcNow;
            var rows = await db.Database.ExecuteSqlRawAsync(
                "UPDATE stage_executions SET status = {0}, updated_at = {1} " +
                "WHERE id = {2} AND lease_owner = {3} AND lease_token = {4} AND status = 'Running'",
                StageStatus.ManualReviewRequired.ToString(),
                now,
                executionId,
                owner,
                token).ConfigureAwait(false);

            if (rows == 0)
            {
                throw new LeaseLostException($"Lease lost for stage execution '{executionId}'.");
            }

            db.ChangeTracker.Clear();
            return await LoadExecutionAsync(db, executionId, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<StageExecution> RenewLeaseAsync(
        Guid tenantId,
        Guid executionId,
        string owner,
        string token,
        TimeSpan leaseTtl,
        CancellationToken cancellationToken = default)
    {
        RequireTenant(tenantId);
        RequireId(executionId, nameof(executionId));
        RequireOwner(owner);
        RequireToken(token);
        RequirePositiveTtl(leaseTtl);

        var now = DateTimeOffset.UtcNow;
        var newExpiry = now.Add(leaseTtl);

        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            var rows = await db.Database.ExecuteSqlRawAsync(
                StageExecutionSql.RenewLeaseSql,
                newExpiry,
                now,
                executionId,
                owner,
                token).ConfigureAwait(false);

            if (rows == 0)
            {
                throw new LeaseLostException($"Lease lost for stage execution '{executionId}'.");
            }

            db.ChangeTracker.Clear();
            return await LoadExecutionAsync(db, executionId, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task ReleaseLeaseAsync(
        Guid tenantId,
        Guid executionId,
        string owner,
        string token,
        CancellationToken cancellationToken = default)
    {
        RequireTenant(tenantId);
        RequireId(executionId, nameof(executionId));
        RequireOwner(owner);
        RequireToken(token);

        var now = DateTimeOffset.UtcNow;

        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            var rows = await db.Database.ExecuteSqlRawAsync(
                StageExecutionSql.ReleaseLeaseSql,
                now,
                now,
                executionId,
                owner,
                token).ConfigureAwait(false);

            if (rows == 0)
            {
                throw new LeaseLostException($"Lease lost for stage execution '{executionId}'.");
            }
        }
    }

    public async Task<int> RecoverStaleAsync(CancellationToken cancellationToken = default)
    {
        using var db = _contextFactory.CreateDbContext();
        var now = DateTimeOffset.UtcNow;

        var stale = await db.Set<StageExecution>()
            .Where(e => e.Status == StageStatus.Running && e.LeaseExpiresAt < now)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var recovered = 0;
        foreach (var execution in stale)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var maxAttempts = MaxAttemptsFor(execution.StageType.ToString());
            if (execution.Attempt + 1 > maxAttempts)
            {
                var failedRows = await db.Database.ExecuteSqlRawAsync(
                    StageExecutionSql.FailSql,
                    StageStatus.Failed.ToString(),
                    now,
                    now,
                    "INTERNAL_ERROR",
                    "Retry budget exhausted; stale lease recovered as failed.",
                    execution.Id,
                    execution.LeaseOwner,
                    execution.LeaseToken).ConfigureAwait(false);
                recovered += failedRows;
                continue;
            }

            var nextAttempt = execution.Attempt + 1;
            var rows = await db.Database.ExecuteSqlRawAsync(
                "UPDATE stage_executions SET status = {0}, updated_at = {1}, attempt = {2} " +
                "WHERE id = {3} AND status = 'Running' AND lease_expires_at < {4}",
                StageStatus.RetryPending.ToString(),
                now,
                nextAttempt,
                execution.Id,
                now).ConfigureAwait(false);
            recovered += rows;
        }

        return recovered;
    }

    private async Task<StageClaimResult> ClaimCoreAsync(
        Guid tenantId,
        Guid projectId,
        Guid runId,
        string stageType,
        string scopeType,
        string scopeId,
        Guid? segmentId,
        int attempt,
        string owner,
        TimeSpan leaseTtl,
        StageStatus initialStatus,
        bool startImmediately,
        CancellationToken cancellationToken)
    {
        RequireTenant(tenantId);
        RequireId(projectId, nameof(projectId));
        RequireId(runId, nameof(runId));
        if (string.IsNullOrWhiteSpace(stageType))
        {
            throw new DomainException("StageType must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(scopeType))
        {
            throw new DomainException("ScopeType must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(scopeId))
        {
            throw new DomainException("ScopeId must not be empty.");
        }

        if (segmentId.HasValue && segmentId.Value == Guid.Empty)
        {
            throw new DomainException("SegmentId must not be empty when set.");
        }

        if (attempt < 0)
        {
            throw new DomainException("Attempt must be >= 0.");
        }

        RequireOwner(owner);
        RequirePositiveTtl(leaseTtl);

        if (!Enum.TryParse<StageType>(stageType, ignoreCase: true, out var stageTypeEnum))
        {
            throw new DomainException($"Unknown stage type '{stageType}'.");
        }

        if (!Enum.TryParse<ScopeType>(scopeType, ignoreCase: true, out var scopeTypeEnum))
        {
            throw new DomainException($"Unknown scope type '{scopeType}'.");
        }

        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();

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

            if (run.Status is ProcessingRunStatus.Cancelling or ProcessingRunStatus.Cancelled)
            {
                throw new ConflictException($"Processing run '{runId}' is '{run.Status}'; scheduling is blocked.");
            }

            var now = DateTimeOffset.UtcNow;
            var leaseToken = Guid.NewGuid().ToString("N");
            var execution = new StageExecution(
                Guid.NewGuid(),
                tenantId,
                projectId,
                runId,
                stageTypeEnum,
                scopeTypeEnum,
                scopeId,
                segmentId,
                attempt,
                initialStatus,
                owner,
                leaseToken,
                0,
                now.Add(leaseTtl),
                startImmediately ? now : null,
                null,
                null,
                run.ConfigurationHash,
                run.ExecutionSnapshotHash,
                null,
                null,
                null,
                now,
                now);

            db.Set<StageExecution>().Add(execution);
            try
            {
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return new StageClaimResult(execution, true);
            }
            catch (Exception ex) when (IsUniqueViolation(ex))
            {
                db.ChangeTracker.Clear();
                var existing = await db.Set<StageExecution>().FirstOrDefaultAsync(
                    e => e.ProcessingRunId == runId
                        && e.StageType == stageTypeEnum
                        && e.ScopeType == scopeTypeEnum
                        && e.ScopeId == scopeId
                        && e.Attempt == attempt,
                    cancellationToken).ConfigureAwait(false);

                if (existing is null)
                {
                    throw new ConflictException("Stage execution already exists.", ex);
                }

                // Redelivery after worker death: an expired lease may be taken over
                // (fenced on the previous owner+token; active leases are untouched).
                var takenOver = await TryTakeoverExpiredAsync(db, existing, owner, leaseTtl, cancellationToken).ConfigureAwait(false);
                return new StageClaimResult(takenOver ?? existing, false);
            }
        }
    }

    private static async Task<StageExecution?> TryTakeoverExpiredAsync(
        DbContext db,
        StageExecution existing,
        string owner,
        TimeSpan leaseTtl,
        CancellationToken cancellationToken)
    {
        if (existing.Status is not (StageStatus.Running or StageStatus.RetryPending))
        {
            return null;
        }

        var now = DateTimeOffset.UtcNow;
        if (existing.LeaseExpiresAt >= now)
        {
            return null;
        }

        var newToken = Guid.NewGuid().ToString("N");
        await db.Database.ExecuteSqlRawAsync(
            StageExecutionSql.TakeoverSql,
            StageStatus.Running.ToString(),
            owner,
            newToken,
            now.Add(leaseTtl),
            now,
            existing.Id,
            existing.LeaseOwner,
            existing.LeaseToken,
            now).ConfigureAwait(false);

        db.ChangeTracker.Clear();
        return await db.Set<StageExecution>()
            .FirstOrDefaultAsync(e => e.Id == existing.Id, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<StageExecution> LoadExecutionAsync(DbContext db, Guid executionId, CancellationToken cancellationToken)
    {
        var execution = await db.Set<StageExecution>()
            .FirstOrDefaultAsync(e => e.Id == executionId, cancellationToken).ConfigureAwait(false);
        if (execution is null)
        {
            throw new NotFoundException($"Stage execution '{executionId}' was not found.");
        }

        return execution;
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

    private int MaxAttemptsFor(string stageType)
    {
        if (_retryOptions.PerStageMaxAttempts.TryGetValue(stageType, out var perStage))
        {
            return perStage;
        }

        return _retryOptions.LogicalStageMaxAttempts;
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

    private static void RequireOwner(string owner)
    {
        if (string.IsNullOrWhiteSpace(owner))
        {
            throw new DomainException("Lease owner must not be empty.");
        }
    }

    private static void RequireToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new DomainException("Lease token must not be empty.");
        }
    }

    private static void RequirePositiveTtl(TimeSpan ttl)
    {
        if (ttl <= TimeSpan.Zero)
        {
            throw new DomainException("Lease TTL must be positive.");
        }
    }

    private static void EnsureTenant(Guid actual, Guid expected)
    {
        if (actual != expected)
        {
            throw new ForbiddenException("Stage execution does not belong to the current tenant.");
        }
    }

    private static void EnsureLeaseOwner(StageExecution execution, string owner, string token)
    {
        if (!string.Equals(execution.LeaseOwner, owner, StringComparison.Ordinal)
            || !string.Equals(execution.LeaseToken, token, StringComparison.Ordinal))
        {
            throw new LeaseLostException($"Lease lost for stage execution '{execution.Id}'.");
        }
    }

    private static string TruncateMessage(string message)
    {
        var trimmed = message.Trim();
        return trimmed.Length <= 500 ? trimmed : trimmed.Substring(0, 500);
    }
}
