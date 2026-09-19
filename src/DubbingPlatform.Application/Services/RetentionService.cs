using System.Text.Json;
using DubbingPlatform.Application.Abstractions;
using DubbingPlatform.Application.Errors;
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
/// Result of one retention sweep: hard-deleted artifact rows, deleted blobs,
/// and completed deletion jobs. Pure data.
/// </summary>
public sealed record SweepResult(int ArtifactsDeleted, int BlobsDeleted, int JobsCompleted);

/// <summary>
/// Logical-then-physical deletion with retention windows and legal holds.
/// Logical phase (<see cref="RequestDeletionAsync"/> /
/// <see cref="ExecutePendingAsync"/>): sets the project <c>IsDeleted</c>
/// shadow flag, marks the project's artifact rows <c>Deleted</c> (rows are
/// retained for lineage/audit; bytes are retained), records a
/// <c>DeletionJob</c>, and audits. Physical phase (<see cref="SweepAsync"/>,
/// driven daily by <c>RetentionSweeper</c> in a maintenance scope): hard-deletes
/// <c>Deleted</c> artifact rows whose retention window expired with zero live
/// references and no active hold, then deletes orphaned blobs and marks their
/// content objects <c>Deleted</c> via the
/// <c>ContentObjectService.CanDeleteContentObjectAsync</c> gate. An active
/// <c>RetentionHold</c> (artifact- or project-scoped) blocks both phases.
/// Reference counts are derived from live rows (no stored counter to drift):
/// an artifact pins while children reference it or non-deleted artifacts share
/// its bytes; a content object pins while any artifact row references it.
/// Only ids, counts, and hold reasons are logged — never content, subjects, or
/// evidence.
/// </summary>
public sealed class RetentionService
{
    public const string ScopeProject = "project";

    public const string AuditActionExecuted = "retention.deletion.executed";

    private static readonly TimeSpan StaleRunningTakeover = TimeSpan.FromHours(1);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IStageExecutionContextFactory _contextFactory;
    private readonly IArtifactStorage _storage;
    private readonly ContentObjectService _contents;
    private readonly AuditService _audit;
    private readonly RetentionOptions _retention;
    private readonly ILogger<RetentionService> _logger;

    public RetentionService(
        IStageExecutionContextFactory contextFactory,
        IArtifactStorage storage,
        ContentObjectService contents,
        AuditService audit,
        IOptions<RetentionOptions> retentionOptions,
        ILogger<RetentionService> logger)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(contents);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(retentionOptions);
        ArgumentNullException.ThrowIfNull(logger);
        _contextFactory = contextFactory;
        _storage = storage;
        _contents = contents;
        _audit = audit;
        _retention = retentionOptions.Value;
        _logger = logger;
    }

    /// <summary>
    /// Whether <paramref name="referenceTime"/> plus
    /// <paramref name="retentionDays"/> has passed relative to
    /// <paramref name="now"/>. Pure.
    /// </summary>
    public static bool IsExpired(DateTimeOffset referenceTime, int retentionDays, DateTimeOffset now)
    {
        if (retentionDays < 0)
        {
            throw new DomainException("RetentionDays must be >= 0.");
        }

        return referenceTime.AddDays(retentionDays) <= now;
    }

    /// <summary>
    /// Retention window for an artifact type: final outputs
    /// (<c>RenderedOutput</c>, <c>Export</c>) use <c>FinalDays</c>, every other
    /// type uses <c>IntermediateDays</c>. Pure.
    /// </summary>
    public static int RetentionDaysFor(ArtifactType type, RetentionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return type is ArtifactType.RenderedOutput or ArtifactType.Export
            ? options.FinalDays
            : options.IntermediateDays;
    }

    /// <summary>
    /// Whether a logically-deleted row may be physically removed: zero live
    /// references, retention window satisfied, and no active hold. Pure.
    /// </summary>
    public static bool CanPhysicallyDelete(int liveReferenceCount, bool retentionExpired, bool hasActiveHold)
    {
        if (liveReferenceCount < 0)
        {
            throw new DomainException("Reference count must be >= 0.");
        }

        return liveReferenceCount == 0 && retentionExpired && !hasActiveHold;
    }

    /// <summary>
    /// Requests logical deletion of a project: ownership is verified
    /// (404 missing/deleted, 403 cross-tenant via <c>TenantGuard</c>), an
    /// active hold fails with <c>POLICY_DENIED</c> naming the hold id, then the
    /// <c>IsDeleted</c> flag plus artifact <c>Deleted</c> marks are applied in
    /// one transaction together with a <c>Completed</c> deletion job, and the
    /// operation is audited. Idempotent: already-deleted projects return the
    /// latest job without new side effects. Physical removal belongs to
    /// <see cref="SweepAsync"/>.
    /// </summary>
    public async Task<Guid> RequestDeletionAsync(
        Guid tenantId,
        Guid projectId,
        string actor,
        CancellationToken cancellationToken = default)
    {
        TenantGuard.RequireTenant(tenantId);
        RequireId(projectId, nameof(projectId));
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);

        await EnsureOwnedProjectAsync(tenantId, projectId, cancellationToken).ConfigureAwait(false);
        await ThrowIfHeldAsync(tenantId, projectId, null, cancellationToken).ConfigureAwait(false);

        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();

            var existing = await db.Set<DeletionJob>()
                .AsNoTracking()
                .Where(j => j.ProjectId == projectId)
                .OrderByDescending(j => j.CreatedAt)
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            var alreadyDeleted = await IsProjectDeletedAsync(db, projectId, cancellationToken).ConfigureAwait(false);
            if (alreadyDeleted && existing is not null)
            {
                return existing.Id;
            }

            var now = DateTimeOffset.UtcNow;
            await MarkProjectDeletedAsync(db, projectId, cancellationToken).ConfigureAwait(false);
            await MarkProjectArtifactsDeletedAsync(db, tenantId, projectId, cancellationToken).ConfigureAwait(false);

            var jobId = Guid.NewGuid();
            db.Set<DeletionJob>().Add(new DeletionJob(
                jobId, tenantId, projectId, ScopeProject, "Completed", now, now));
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            await _audit.LogAsync(
                tenantId, projectId, actor.Trim(), "project.delete",
                "project", projectId.ToString("N"),
                JsonSerializer.Serialize(new { deletionJobId = jobId.ToString("N") }, JsonOptions),
                cancellationToken).ConfigureAwait(false);

            _logger.LogInformation(
                "Project {ProjectId} logically deleted with job {JobId}.",
                projectId, jobId);
            return jobId;
        }
    }

    /// <summary>
    /// Executes one pending deletion job (worker fast-path and sweeper
    /// backstop share this): claims <c>Pending</c> (or stale <c>Running</c>
    /// older than one hour) via a conditional update so concurrent deliveries
    /// serialize with exactly one winner, performs the logical delete, marks
    /// the job <c>Completed</c>, and audits. Terminal jobs (<c>Completed</c>,
    /// <c>Failed</c>, <c>Cancelled</c>) and jobs owned by another tenant are
    /// returned untouched (the caller routes cross-tenant to <c>_error</c>).
    /// </summary>
    public async Task<DeletionJob> ExecutePendingAsync(
        Guid tenantId,
        Guid deletionJobId,
        CancellationToken cancellationToken = default)
    {
        TenantGuard.RequireTenant(tenantId);
        RequireId(deletionJobId, nameof(deletionJobId));

        DeletionJob job;
        using (TenantContext.BeginMaintenanceScope())
        {
            using var db = _contextFactory.CreateDbContext();
            var loaded = await db.Set<DeletionJob>()
                .AsNoTracking()
                .FirstOrDefaultAsync(j => j.Id == deletionJobId, cancellationToken).ConfigureAwait(false);
            if (loaded is null)
            {
                throw new NotFoundException($"Deletion job '{deletionJobId}' was not found.");
            }

            TenantGuard.AssertMatch(tenantId, loaded.TenantId);
            job = loaded;
        }

        if (job.Status is "Completed" or "Failed" or "Cancelled")
        {
            return job;
        }

        if (!string.Equals(job.Scope, ScopeProject, StringComparison.OrdinalIgnoreCase) || !job.ProjectId.HasValue)
        {
            await MarkJobTerminalAsync(tenantId, deletionJobId, "Failed", cancellationToken).ConfigureAwait(false);
            throw new ErrorCodeException(
                ErrorCodes.PipelineInvariantViolation,
                $"Deletion job '{deletionJobId}' has unsupported scope '{job.Scope}'.");
        }

        var claimed = await TryClaimAsync(deletionJobId, cancellationToken).ConfigureAwait(false);
        if (!claimed)
        {
            return await LoadJobAsync(deletionJobId, cancellationToken).ConfigureAwait(false);
        }

        var projectId = job.ProjectId.Value;
        await ThrowIfHeldAsync(tenantId, projectId, null, cancellationToken).ConfigureAwait(false);

        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            await MarkProjectDeletedAsync(db, projectId, cancellationToken).ConfigureAwait(false);
            await MarkProjectArtifactsDeletedAsync(db, tenantId, projectId, cancellationToken).ConfigureAwait(false);
            await db.Database.ExecuteSqlRawAsync(
                "UPDATE deletion_jobs SET status = {0}, completed_at = {1} WHERE id = {2}",
                "Completed", DateTimeOffset.UtcNow, deletionJobId).ConfigureAwait(false);
        }

        await _audit.LogAsync(
            tenantId, projectId, "deletion-worker", AuditActionExecuted,
            "deletion_job", deletionJobId.ToString("N"),
            JsonSerializer.Serialize(new { deletionJobId = deletionJobId.ToString("N") }, JsonOptions),
            cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Deletion job {JobId} executed for project {ProjectId}.",
            deletionJobId, projectId);
        return await LoadJobAsync(deletionJobId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Daily physical sweep (maintenance scope, dedicated role bypassing RLS at
    /// deploy): hard-deletes <c>Deleted</c> artifact rows whose window expired
    /// with zero live references and no active hold (lineage rows go first),
    /// completes <c>Pending</c> (or stale <c>Running</c>) deletion jobs via
    /// <see cref="ExecutePendingAsync"/>, then deletes orphaned blobs and marks
    /// their content objects <c>Deleted</c> through the
    /// <c>ContentObjectService</c> gate. Skips (never fails) on holds and
    /// unexpired rows; storage failures skip the row for the next sweep.
    /// </summary>
    public async Task<SweepResult> SweepAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var artifactsDeleted = 0;
        var blobsDeleted = 0;
        var jobsCompleted = 0;

        using (TenantContext.BeginMaintenanceScope())
        {
            using var db = _contextFactory.CreateDbContext();

            var bound = now.AddDays(-_retention.IntermediateDays);
            var candidates = await db.Set<Artifact>()
                .AsNoTracking()
                .Where(a => a.Status == ArtifactStatus.Deleted && a.CreatedAt <= bound)
                .Select(a => new { a.Id, a.TenantId, a.ProjectId, a.Type, a.CreatedAt, a.ContentObjectId })
                .ToListAsync(cancellationToken).ConfigureAwait(false);

            foreach (var candidate in candidates)
            {
                var days = RetentionDaysFor(candidate.Type, _retention);
                if (!IsExpired(candidate.CreatedAt, days, now))
                {
                    continue;
                }

                if (await HasActiveHoldAsync(db, candidate.TenantId, candidate.ProjectId, candidate.Id, cancellationToken).ConfigureAwait(false))
                {
                    continue;
                }

                var liveRefs = await CountLiveReferencesAsync(db, candidate.Id, candidate.ContentObjectId, cancellationToken).ConfigureAwait(false);
                if (!CanPhysicallyDelete(liveRefs, true, false))
                {
                    continue;
                }

                await db.Database.ExecuteSqlRawAsync(
                    "DELETE FROM stage_input_artifacts WHERE artifact_id = {0}",
                    candidate.Id).ConfigureAwait(false);
                await db.Database.ExecuteSqlRawAsync(
                    "DELETE FROM stage_output_artifacts WHERE artifact_id = {0}",
                    candidate.Id).ConfigureAwait(false);
                await db.Database.ExecuteSqlRawAsync(
                    "DELETE FROM artifact_parents WHERE child_artifact_id = {0} OR parent_artifact_id = {0}",
                    candidate.Id).ConfigureAwait(false);
                await db.Database.ExecuteSqlRawAsync(
                    "DELETE FROM artifacts WHERE id = {0} AND status = {1}",
                    candidate.Id, ArtifactStatus.Deleted.ToString()).ConfigureAwait(false);
                artifactsDeleted++;
            }

            var pendingJobs = await db.Set<DeletionJob>()
                .AsNoTracking()
                .Where(j => j.Status == "Pending" || j.Status == "Running")
                .Select(j => new { j.Id, j.TenantId })
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            foreach (var pending in pendingJobs)
            {
                try
                {
                    var before = await LoadJobAsync(pending.Id, cancellationToken).ConfigureAwait(false);
                    var after = await ExecutePendingAsync(pending.TenantId, pending.Id, cancellationToken).ConfigureAwait(false);
                    if (!string.Equals(before.Status, "Completed", StringComparison.Ordinal)
                        && string.Equals(after.Status, "Completed", StringComparison.Ordinal))
                    {
                        jobsCompleted++;
                    }
                }
#pragma warning disable CA1031 // Sweeper must survive single-job failures (holds, races); failures ride the next sweep.
                catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
                {
                    _logger.LogWarning(
                        ex,
                        "Retention sweep skipped deletion job {JobId}.",
                        pending.Id);
                }
            }

            var contentBound = now.AddDays(-_retention.FinalDays);
            var contents = await db.Set<ContentObject>()
                .AsNoTracking()
                .Where(c => c.Status == ContentObjectStatus.Committed && c.LastReferencedAt <= contentBound)
                .Select(c => new { c.Id, c.TenantId, c.StorageKey })
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            foreach (var content in contents)
            {
                bool deletable;
                try
                {
                    deletable = await _contents.CanDeleteContentObjectAsync(
                        content.TenantId, content.Id, _retention.FinalDays, cancellationToken).ConfigureAwait(false);
                }
#pragma warning disable CA1031 // Content gate fail-closed is handled inside; transport faults skip to the next sweep.
                catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
                {
                    _logger.LogWarning(
                        ex,
                        "Retention sweep skipped content {ContentId}.",
                        content.Id);
                    continue;
                }

                if (!deletable)
                {
                    continue;
                }

                try
                {
                    await _storage.DeleteAsync(content.StorageKey, cancellationToken).ConfigureAwait(false);
                }
#pragma warning disable CA1031 // Blob-delete failure skips the row for the next sweep; bytes stay until then.
                catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
                {
                    _logger.LogWarning(
                        ex,
                        "Retention sweep could not delete blob for content {ContentId}; retrying next sweep.",
                        content.Id);
                    continue;
                }

                await db.Database.ExecuteSqlRawAsync(
                    "UPDATE content_objects SET status = {0} WHERE id = {1} AND tenant_id = {2}",
                    ContentObjectStatus.Deleted.ToString(), content.Id, content.TenantId).ConfigureAwait(false);
                blobsDeleted++;
            }
        }

        if (artifactsDeleted > 0 || blobsDeleted > 0 || jobsCompleted > 0)
        {
            _logger.LogInformation(
                "Retention sweep completed: {Artifacts} artifact rows, {Blobs} blobs, {Jobs} jobs.",
                artifactsDeleted, blobsDeleted, jobsCompleted);
        }

        return new SweepResult(artifactsDeleted, blobsDeleted, jobsCompleted);
    }

    private async Task EnsureOwnedProjectAsync(Guid tenantId, Guid projectId, CancellationToken cancellationToken)
    {
        DubbingProject? project;
        using (TenantContext.BeginMaintenanceScope())
        {
            using var db = _contextFactory.CreateDbContext();
            project = await db.Set<DubbingProject>()
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken).ConfigureAwait(false);
        }

        if (project is null)
        {
            throw new NotFoundException($"Project '{projectId}' was not found.");
        }

        TenantGuard.AssertMatch(tenantId, project.TenantId);
    }

    private async Task ThrowIfHeldAsync(
        Guid tenantId,
        Guid projectId,
        Guid? artifactId,
        CancellationToken cancellationToken)
    {
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            var hold = await db.Set<RetentionHold>()
                .AsNoTracking()
                .Where(h => h.IsActive && h.ProjectId == projectId)
                .OrderBy(h => h.PlacedAt)
                .Select(h => new { h.Id })
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (hold is not null)
            {
                throw new ErrorCodeException(
                    ErrorCodes.PolicyDenied,
                    string.Concat(
                        "Retention hold '", hold.Id.ToString("N"),
                        "' blocks deletion of project '", projectId.ToString("N"), "'."));
            }
        }
    }

    private static async Task<bool> IsProjectDeletedAsync(
        Microsoft.EntityFrameworkCore.DbContext db,
        Guid projectId,
        CancellationToken cancellationToken)
    {
        return await db.Set<DubbingProject>()
            .Where(p => p.Id == projectId)
            .Select(p => EF.Property<bool>(p, "IsDeleted"))
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task MarkProjectDeletedAsync(
        Microsoft.EntityFrameworkCore.DbContext db,
        Guid projectId,
        CancellationToken cancellationToken)
    {
        var attached = await db.Set<DubbingProject>()
            .FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken).ConfigureAwait(false);
        if (attached is null)
        {
            throw new NotFoundException($"Project '{projectId}' was not found.");
        }

        db.Entry(attached).Property("IsDeleted").CurrentValue = true;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task MarkProjectArtifactsDeletedAsync(
        Microsoft.EntityFrameworkCore.DbContext db,
        Guid tenantId,
        Guid projectId,
        CancellationToken cancellationToken)
    {
        await db.Database.ExecuteSqlRawAsync(
            "UPDATE artifacts SET status = {0} WHERE tenant_id = {1} AND project_id = {2} AND status <> {0}",
            ArtifactStatus.Deleted.ToString(), tenantId, projectId).ConfigureAwait(false);
    }

    private async Task<bool> TryClaimAsync(Guid deletionJobId, CancellationToken cancellationToken)
    {
        using (TenantContext.BeginMaintenanceScope())
        {
            using var db = _contextFactory.CreateDbContext();
            var staleBefore = DateTimeOffset.UtcNow - StaleRunningTakeover;
            var rows = await db.Database.ExecuteSqlRawAsync(
                "UPDATE deletion_jobs SET status = {0} WHERE id = {1} AND (status = {2} OR (status = {3} AND created_at <= {4}))",
                "Running", deletionJobId, "Pending", "Running", staleBefore).ConfigureAwait(false);
            return rows == 1;
        }
    }

    private async Task MarkJobTerminalAsync(
        Guid tenantId,
        Guid deletionJobId,
        string status,
        CancellationToken cancellationToken)
    {
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            await db.Database.ExecuteSqlRawAsync(
                "UPDATE deletion_jobs SET status = {0}, completed_at = {1} WHERE id = {2}",
                status, DateTimeOffset.UtcNow, deletionJobId).ConfigureAwait(false);
        }
    }

    private async Task<DeletionJob> LoadJobAsync(Guid deletionJobId, CancellationToken cancellationToken)
    {
        using (TenantContext.BeginMaintenanceScope())
        {
            using var db = _contextFactory.CreateDbContext();
            var job = await db.Set<DeletionJob>()
                .AsNoTracking()
                .FirstOrDefaultAsync(j => j.Id == deletionJobId, cancellationToken).ConfigureAwait(false);
            if (job is null)
            {
                throw new NotFoundException($"Deletion job '{deletionJobId}' was not found.");
            }

            return job;
        }
    }

    private static async Task<bool> HasActiveHoldAsync(
        Microsoft.EntityFrameworkCore.DbContext db,
        Guid tenantId,
        Guid projectId,
        Guid artifactId,
        CancellationToken cancellationToken)
    {
        return await db.Set<RetentionHold>()
            .AnyAsync(
                h => h.TenantId == tenantId
                    && h.IsActive
                    && (h.ArtifactId == artifactId || h.ProjectId == projectId),
                cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> CountLiveReferencesAsync(
        Microsoft.EntityFrameworkCore.DbContext db,
        Guid artifactId,
        Guid contentObjectId,
        CancellationToken cancellationToken)
    {
        var childRefs = await db.Set<ArtifactParent>()
            .Where(p => p.ParentArtifactId == artifactId)
            .Select(p => p.ChildArtifactId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var liveChildren = 0;
        foreach (var childId in childRefs.Distinct())
        {
            var childDeleted = await db.Set<Artifact>()
                .Where(a => a.Id == childId)
                .Select(a => a.Status == ArtifactStatus.Deleted)
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            var childExists = await db.Set<Artifact>()
                .AnyAsync(a => a.Id == childId, cancellationToken).ConfigureAwait(false);
            if (childExists && !childDeleted)
            {
                liveChildren++;
            }
        }

        var sharedBytes = await db.Set<Artifact>()
            .Where(a => a.Id != artifactId
                && a.ContentObjectId == contentObjectId
                && a.Status != ArtifactStatus.Deleted)
            .CountAsync(cancellationToken).ConfigureAwait(false);

        return checked(liveChildren + sharedBytes);
    }

    private static void RequireId(Guid id, string name)
    {
        if (id == Guid.Empty)
        {
            throw new DomainException($"{name} must not be empty.");
        }
    }
}
