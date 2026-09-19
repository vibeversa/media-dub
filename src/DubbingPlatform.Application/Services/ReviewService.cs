using System.Text.Json;
using DubbingPlatform.Application.Exceptions;
using DubbingPlatform.Application.MultiTenancy;
using DubbingPlatform.Application.StateMachines;
using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Domain.Enums;
using DubbingPlatform.Domain.Exceptions;
using Microsoft.EntityFrameworkCore;

namespace DubbingPlatform.Application.Services;

/// <summary>
/// Which version table a resolve-with-edit writes.
/// </summary>
public enum ManualVersionKind
{
    Transcript,
    Translation,
}

/// <summary>
/// Result of a resolve-with-edit: the transitioned review plus the new manual
/// version identity.
/// </summary>
public sealed record ResolveResult(Guid ReviewId, string Status, Guid ManualVersionId, string VersionKind);

/// <summary>
/// Actionable manual review resolution. Lists and inspects reviews;
/// <c>Approve/Reject/Requeue/ResolveWithEdit</c> transition
/// <c>Open→terminal</c> via <see cref="ReviewStateMachine"/> with a conditional
/// update (repeat decisions on terminal rows → 409) and append one
/// <see cref="ReviewDecision"/> row (reviewer + reason + metadata) in the same
/// transaction. Resolve-with-edit validates non-empty text (400), requires a
/// segment-scoped review (400 otherwise; project-level QC blocks resolve via
/// requeue/retry), unselects prior selected versions, inserts the new manual
/// version selected, and records reviewer/reason linkage in the decision
/// metadata. Approve/Requeue/Resolve resume the run
/// <c>ManualReviewRequired→Running</c> only when zero open reviews remain;
/// Reject never resumes (the run stays blocked for operator requeue/retry —
/// the saga redispatches only when the published <c>ReviewResolved</c> carries
/// a stage, which the controller omits for rejects). Eligible units continue
/// while others await review (barriers are per-stage; untouched stages keep
/// flowing). Unresolved required reviews block <c>RunCompleted</c> via the
/// RenderWorker open-review gate plus this service's no-resume-until-clear
/// rule. All decisions publish <c>ReviewResolved</c> (controller-owned,
/// best effort) and audit <c>review.{decision}</c> (controller-owned).
/// Manual versions carry <c>Provider="manual"</c> (<see cref="ManualProvider"/>)
/// instead of a new <c>IsManual</c> column — no migration, matching the 026
/// precedent where review state lives on <c>ReviewItem</c>, not version flags.
/// Only ids, decisions, counts, and hashes are logged — never edited text.
/// </summary>
public sealed class ReviewService
{
    /// <summary>Provider marker for reviewer-authored manual versions.</summary>
    public const string ManualProvider = "manual";

    /// <summary>Model marker for reviewer-authored manual versions.</summary>
    public const string ManualModel = "manual-review-v1";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IStageExecutionContextFactory _contextFactory;

    public ReviewService(IStageExecutionContextFactory contextFactory)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        _contextFactory = contextFactory;
    }

    /// <summary>
    /// Whether a decision type resumes run scheduling once no open reviews
    /// remain. Pure. Reject never resumes (blocked until requeue/retry).
    /// </summary>
    public static bool ResumesRun(ReviewDecisionType decision, int openReviewsRemaining)
    {
        if (openReviewsRemaining != 0)
        {
            return false;
        }

        return decision is ReviewDecisionType.Approve
            or ReviewDecisionType.Requeue
            or ReviewDecisionType.ResolveWithEdit;
    }

    /// <summary>
    /// Picks the manual version table from the review reason. Pure. Reasons
    /// mentioning translation (e.g. <c>TRANSLATION_QUALITY</c>) yield a
    /// translation version; everything else (transcription confidence, QC
    /// sync, terminology) yields a transcript version the downstream
    /// re-translation then consumes.
    /// </summary>
    public static ManualVersionKind ResolveVersionKind(string? reason)
    {
        if (!string.IsNullOrWhiteSpace(reason)
            && reason.Contains("translat", StringComparison.OrdinalIgnoreCase))
        {
            return ManualVersionKind.Translation;
        }

        return ManualVersionKind.Transcript;
    }

    /// <summary>
    /// Whether a provider marker denotes a reviewer-authored manual version.
    /// Pure.
    /// </summary>
    public static bool IsManualVersion(string? provider)
    {
        return string.Equals((provider ?? string.Empty).Trim(), ManualProvider, StringComparison.Ordinal);
    }

    /// <summary>
    /// Validates resolve-with-edit text (non-empty). Pure; throws
    /// <see cref="DomainException"/> (400 VALIDATION_FAILED) on empty input.
    /// </summary>
    public static string RequireEditedText(string? editedText)
    {
        if (string.IsNullOrWhiteSpace(editedText))
        {
            throw new DomainException("Resolve requires non-empty editedText.");
        }

        var trimmed = editedText.Trim();
        if (trimmed.Length > 5000)
        {
            throw new DomainException("Resolve editedText must not exceed 5000 characters.");
        }

        return trimmed;
    }

    /// <summary>
    /// Audit action for a decision. Pure.
    /// </summary>
    public static string AuditActionFor(ReviewDecisionType decision)
    {
        return string.Concat("review.", decision.ToString().ToLowerInvariant());
    }

    /// <summary>
    /// Paginated review listing for a project (oldest first).
    /// </summary>
    public async Task<(IReadOnlyList<ReviewItem> Items, long Total)> ListAsync(
        Guid tenantId,
        Guid projectId,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        RequireTenant(tenantId);
        RequireId(projectId, nameof(projectId));
        await RequireProjectAsync(tenantId, projectId, cancellationToken).ConfigureAwait(false);

        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            var scoped = db.Set<ReviewItem>().AsNoTracking().Where(r => r.ProjectId == projectId);
            var total = await scoped.LongCountAsync(cancellationToken).ConfigureAwait(false);
            var items = await scoped
                .OrderBy(r => r.CreatedAt)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            return (items, total);
        }
    }

    /// <summary>
    /// Loads one owned review (404 vs 403 split preserved).
    /// </summary>
    public async Task<ReviewItem> GetAsync(
        Guid tenantId,
        Guid reviewId,
        CancellationToken cancellationToken = default)
    {
        RequireTenant(tenantId);
        RequireId(reviewId, nameof(reviewId));
        return await LoadOwnedReviewAsync(tenantId, null, reviewId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Loads one review owned by a project (nested route).
    /// </summary>
    public async Task<ReviewItem> GetForProjectAsync(
        Guid tenantId,
        Guid projectId,
        Guid reviewId,
        CancellationToken cancellationToken = default)
    {
        RequireTenant(tenantId);
        RequireId(projectId, nameof(projectId));
        RequireId(reviewId, nameof(reviewId));
        await RequireProjectAsync(tenantId, projectId, cancellationToken).ConfigureAwait(false);
        return await LoadOwnedReviewAsync(tenantId, projectId, reviewId, cancellationToken).ConfigureAwait(false);
    }

    public Task<ReviewItem> ApproveAsync(Guid tenantId, Guid reviewId, string reviewer, string? reason, CancellationToken cancellationToken = default)
    {
        return DecideAsync(tenantId, reviewId, ReviewDecisionType.Approve, reviewer, reason, cancellationToken);
    }

    public Task<ReviewItem> RejectAsync(Guid tenantId, Guid reviewId, string reviewer, string? reason, CancellationToken cancellationToken = default)
    {
        return DecideAsync(tenantId, reviewId, ReviewDecisionType.Reject, reviewer, reason, cancellationToken);
    }

    public Task<ReviewItem> RequeueAsync(Guid tenantId, Guid reviewId, string reviewer, string? reason, CancellationToken cancellationToken = default)
    {
        return DecideAsync(tenantId, reviewId, ReviewDecisionType.Requeue, reviewer, reason, cancellationToken);
    }

    /// <summary>
    /// Resolves with a reviewer edit: creates the manual version, transitions
    /// the review, and resumes the run when cleared.
    /// </summary>
    public async Task<ResolveResult> ResolveWithEditAsync(
        Guid tenantId,
        Guid reviewId,
        string reviewer,
        string? reason,
        string? editedText,
        CancellationToken cancellationToken = default)
    {
        RequireTenant(tenantId);
        RequireId(reviewId, nameof(reviewId));
        ArgumentException.ThrowIfNullOrWhiteSpace(reviewer);
        var text = RequireEditedText(editedText);
        var review = await LoadOwnedReviewAsync(tenantId, null, reviewId, cancellationToken).ConfigureAwait(false);

        if (review.Status != ReviewStatus.Open)
        {
            throw new ConflictException($"Review '{reviewId}' is already {review.Status} and cannot transition.");
        }

        if (review.SegmentId is null)
        {
            throw new DomainException("Resolve-with-edit requires a segment-scoped review; requeue project-level reviews instead.");
        }

        var segment = await LoadSegmentAsync(review, tenantId, cancellationToken).ConfigureAwait(false);
        var kind = ResolveVersionKind(review.Reason);
        var decisionReason = string.IsNullOrWhiteSpace(reason) ? ReviewDecisionType.ResolveWithEdit.ToString() : reason.Trim();

        Guid versionId = Guid.NewGuid();
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var now = DateTimeOffset.UtcNow;
                if (kind == ManualVersionKind.Transcript)
                {
                    await db.Database.ExecuteSqlRawAsync(
                        "UPDATE transcript_versions SET is_selected = FALSE WHERE run_id = {0} AND segment_id = {1}",
                        review.ProcessingRunId, segment.Id).ConfigureAwait(false);
                    var project = await db.Set<DubbingProject>()
                        .AsNoTracking()
                        .FirstOrDefaultAsync(p => p.Id == review.ProjectId, cancellationToken).ConfigureAwait(false);
                    var language = project?.SourceLanguage ?? "en";
                    db.Set<TranscriptVersion>().Add(new TranscriptVersion(
                        versionId, tenantId, review.ProjectId, review.ProcessingRunId, segment.Id,
                        ManualProvider, ManualModel, language, text, 1.0, null, true, false, now));
                }
                else
                {
                    await db.Database.ExecuteSqlRawAsync(
                        "UPDATE translation_versions SET is_selected = FALSE WHERE run_id = {0} AND segment_id = {1}",
                        review.ProcessingRunId, segment.Id).ConfigureAwait(false);
                    db.Set<TranslationVersion>().Add(new TranslationVersion(
                        versionId, tenantId, review.ProjectId, review.ProcessingRunId, segment.Id,
                        text, [], 1.0, 1.0, 1.0, ManualProvider, ManualModel, null, null, true, now));
                }

                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

                ReviewStateMachine.EnsureCanTransition(review.Status, ReviewStatus.ResolvedWithEdit);
                var transitioned = await db.Database.ExecuteSqlRawAsync(
                    "UPDATE review_items SET status = {0}, updated_at = {1}, resolved_at = {1} " +
                    "WHERE id = {2} AND tenant_id = {3} AND status = 'Open'",
                    ReviewStatus.ResolvedWithEdit.ToString(), now, reviewId, tenantId).ConfigureAwait(false);
                if (transitioned == 0)
                {
                    throw new ConflictException($"Review '{reviewId}' is no longer open; resolve lost a race.");
                }

                db.Set<ReviewDecision>().Add(new ReviewDecision(
                    Guid.NewGuid(), tenantId, reviewId, ReviewDecisionType.ResolveWithEdit,
                    reviewer.Trim(), decisionReason,
                    JsonSerializer.Serialize(new
                    {
                        manualVersionId = versionId.ToString("N"),
                        versionKind = kind.ToString(),
                        segmentId = segment.Id.ToString("N"),
                    }, JsonOptions),
                    now));
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                try
                {
                    await db.Database.RollbackTransactionAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Rollback best effort; original error propagates.
                }

                throw;
            }
        }

        await MaybeResumeRunAsync(tenantId, review.ProcessingRunId, cancellationToken).ConfigureAwait(false);
        return new ResolveResult(reviewId, ReviewStatus.ResolvedWithEdit.ToString(), versionId, kind.ToString());
    }

    private async Task<ReviewItem> DecideAsync(
        Guid tenantId,
        Guid reviewId,
        ReviewDecisionType decision,
        string reviewer,
        string? reason,
        CancellationToken cancellationToken)
    {
        RequireTenant(tenantId);
        RequireId(reviewId, nameof(reviewId));
        ArgumentException.ThrowIfNullOrWhiteSpace(reviewer);
        var review = await LoadOwnedReviewAsync(tenantId, null, reviewId, cancellationToken).ConfigureAwait(false);

        if (review.Status != ReviewStatus.Open)
        {
            throw new ConflictException($"Review '{reviewId}' is already {review.Status} and cannot transition.");
        }

        var target = decision switch
        {
            ReviewDecisionType.Approve => ReviewStatus.Approved,
            ReviewDecisionType.Reject => ReviewStatus.Rejected,
            ReviewDecisionType.Requeue => ReviewStatus.Requeued,
            ReviewDecisionType.ResolveWithEdit => ReviewStatus.ResolvedWithEdit,
            _ => throw new DomainException($"Unknown review decision '{decision}'."),
        };

        ReviewStateMachine.EnsureCanTransition(review.Status, target);
        var decisionReason = string.IsNullOrWhiteSpace(reason) ? decision.ToString() : reason.Trim();
        var now = DateTimeOffset.UtcNow;

        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var transitioned = await db.Database.ExecuteSqlRawAsync(
                    "UPDATE review_items SET status = {0}, updated_at = {1}, resolved_at = {1} " +
                    "WHERE id = {2} AND tenant_id = {3} AND status = 'Open'",
                    target.ToString(), now, reviewId, tenantId).ConfigureAwait(false);
                if (transitioned == 0)
                {
                    throw new ConflictException($"Review '{reviewId}' is no longer open; decision lost a race.");
                }

                db.Set<ReviewDecision>().Add(new ReviewDecision(
                    Guid.NewGuid(), tenantId, reviewId, decision,
                    reviewer.Trim(), decisionReason, null, now));
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                try
                {
                    await db.Database.RollbackTransactionAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Rollback best effort; original error propagates.
                }

                throw;
            }
        }

        if (ResumesRun(decision, await CountOpenReviewsAsync(tenantId, review.ProcessingRunId, cancellationToken).ConfigureAwait(false)))
        {
            await MaybeResumeRunAsync(tenantId, review.ProcessingRunId, cancellationToken).ConfigureAwait(false);
        }

        return await LoadOwnedReviewAsync(tenantId, null, reviewId, cancellationToken).ConfigureAwait(false);
    }

    private async Task MaybeResumeRunAsync(Guid tenantId, Guid runId, CancellationToken cancellationToken)
    {
        var open = await CountOpenReviewsAsync(tenantId, runId, cancellationToken).ConfigureAwait(false);
        if (open != 0)
        {
            return;
        }

        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            var run = await db.Set<ProcessingRun>()
                .AsNoTracking()
                .FirstOrDefaultAsync(r => r.Id == runId, cancellationToken).ConfigureAwait(false);
            if (run is null || run.Status != ProcessingRunStatus.ManualReviewRequired)
            {
                return;
            }

            try
            {
                RunStateMachine.EnsureCanTransition(run.Status, ProcessingRunStatus.Running);
            }
            catch (DomainException)
            {
                return;
            }

            var now = DateTimeOffset.UtcNow;
            await db.Database.ExecuteSqlRawAsync(
                "UPDATE processing_runs SET status = {0}, updated_at = {1}, started_at = COALESCE(started_at, {1}) " +
                "WHERE id = {2} AND tenant_id = {3} AND status = 'ManualReviewRequired'",
                ProcessingRunStatus.Running.ToString(), now, runId, tenantId).ConfigureAwait(false);
        }
    }

    private async Task<int> CountOpenReviewsAsync(Guid tenantId, Guid runId, CancellationToken cancellationToken)
    {
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            return await db.Set<ReviewItem>()
                .AsNoTracking()
                .CountAsync(r => r.ProcessingRunId == runId && r.Status == ReviewStatus.Open, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<ReviewItem> LoadOwnedReviewAsync(
        Guid tenantId,
        Guid? projectId,
        Guid reviewId,
        CancellationToken cancellationToken)
    {
        ReviewItem? item;
        using (TenantContext.BeginMaintenanceScope())
        {
            using var db = _contextFactory.CreateDbContext();
            item = await db.Set<ReviewItem>()
                .AsNoTracking()
                .FirstOrDefaultAsync(r => r.Id == reviewId, cancellationToken).ConfigureAwait(false);
        }

        if (item is null)
        {
            throw new NotFoundException($"Review '{reviewId}' was not found.");
        }

        if (item.TenantId != tenantId)
        {
            throw new ForbiddenException($"Review '{reviewId}' does not belong to the current tenant.");
        }

        if (projectId.HasValue && item.ProjectId != projectId.Value)
        {
            throw new ForbiddenException($"Review '{reviewId}' does not belong to the current project.");
        }

        return item;
    }

    private async Task<SpeechSegment> LoadSegmentAsync(ReviewItem review, Guid tenantId, CancellationToken cancellationToken)
    {
        if (review.SegmentId is null || review.SegmentId.Value == Guid.Empty)
        {
            throw new DomainException("Resolve-with-edit requires a segment-scoped review.");
        }

        SpeechSegment? segment;
        using (TenantContext.BeginMaintenanceScope())
        {
            using var db = _contextFactory.CreateDbContext();
            segment = await db.Set<SpeechSegment>()
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.Id == review.SegmentId.Value, cancellationToken).ConfigureAwait(false);
        }

        if (segment is null
            || segment.TenantId != tenantId
            || segment.ProjectId != review.ProjectId
            || segment.RunId != review.ProcessingRunId)
        {
            throw new NotFoundException($"Segment '{review.SegmentId.Value}' was not found.");
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
