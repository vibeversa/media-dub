using System.Text.Json;
using System.Text.RegularExpressions;
using DubbingPlatform.Application.Configuration;
using DubbingPlatform.Application.Errors;
using DubbingPlatform.Application.Exceptions;
using DubbingPlatform.Application.MultiTenancy;
using DubbingPlatform.Application.Security;
using DubbingPlatform.Application.Services;
using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Domain.Enums;
using DubbingPlatform.Domain.Exceptions;
using Microsoft.EntityFrameworkCore;

namespace DubbingPlatform.Application.Reviews;

/// <summary>
/// Hardened review mutation action.
/// </summary>
public enum ReviewMutationAction
{
    Resolve,
    Dismiss,
    Reopen,
    ResolveWithEdit,
}

/// <summary>
/// Outcome of one hardened review mutation: the transitioned status, the new
/// decision-count version, and the linked manual version for resolve-with-edit
/// (null otherwise). Only ids, versions, and statuses travel here — never
/// reason text or edited content.
/// </summary>
public sealed record ReviewMutationResult(
    Guid ReviewId,
    string Status,
    int Version,
    Guid? ManualVersionId,
    string? VersionKind);

/// <summary>
/// Version-guarded, reasoned, idempotent review mutations (Task 011).
/// Resolve maps to <c>Approve</c>, dismiss to <c>Reject</c>, reopen flips any
/// terminal status back to <c>Open</c>, and resolve-with-edit delegates to the
/// Task 003 <see cref="ReviewService.ResolveWithEditAsync"/> manual-version
/// path (immutable new version, priors untouched, new version id linked).
/// The review <c>version</c> is the count of <c>ReviewDecision</c> rows;
/// <c>expectedVersion</c> must match it or the writer gets 409
/// <c>REVIEW_VERSION_CONFLICT</c> with the current version (never a silent
/// resolve). Idempotency is per-review over <c>(tenant, review, key)</c> with
/// 24h retention: same key + same body replays the stored result (no second
/// transition, no second decision row); same key + different body is 409
/// <c>CONFLICT</c>. Every mutation appends exactly one <c>ReviewDecision</c>
/// and one <c>AuditEvent</c> carrying actor, action, reason, old/new status,
/// version delta, correlationId, and idempotency key (texts redacted, never
/// raw). Cross-tenant ids read as 404 <c>NOT_FOUND</c> (no leak). Run resume
/// reuses <see cref="ReviewService"/> semantics (approve/resolve-with-edit
/// resume <c>ManualReviewRequired→Running</c> only when zero open reviews
/// remain; dismiss never resumes); reopen leaves the run untouched — the
/// render open-review gate blocks completion while the item is open.
/// </summary>
public sealed class ReviewMutationService
{
    /// <summary>Idempotency retention for review mutations.</summary>
    public static readonly TimeSpan IdempotencyExpiry = TimeSpan.FromHours(24);

    /// <summary>Maximum stored reason length; longer reasons are truncated.</summary>
    public const int MaxReasonLength = 500;

    /// <summary>Header set on idempotent replays.</summary>
    public const string ReplayedHeaderName = "Idempotent-Replayed";

    private static readonly Regex HtmlTagRegex = new(
        "<[^>]*>",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IStageExecutionContextFactory _contextFactory;
    private readonly IdempotencyService _idempotency;
    private readonly ReviewService _reviews;
    private readonly AuditService _audit;

    public ReviewMutationService(
        IStageExecutionContextFactory contextFactory,
        IdempotencyService idempotency,
        ReviewService reviews,
        AuditService audit)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        ArgumentNullException.ThrowIfNull(idempotency);
        ArgumentNullException.ThrowIfNull(reviews);
        ArgumentNullException.ThrowIfNull(audit);
        _contextFactory = contextFactory;
        _idempotency = idempotency;
        _reviews = reviews;
        _audit = audit;
    }

    /// <summary>
    /// Sanitizes a mutation reason: strips HTML, trims, truncates to
    /// <see cref="MaxReasonLength"/>. Null/whitespace yields null. Pure.
    /// </summary>
    public static string? SanitizeReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return null;
        }

        var stripped = HtmlTagRegex.Replace(reason, string.Empty).Trim();
        if (stripped.Length == 0)
        {
            return null;
        }

        return stripped.Length > MaxReasonLength
            ? stripped.Substring(0, MaxReasonLength)
            : stripped;
    }

    /// <summary>
    /// Sanitizes resolve-with-edit text (HTML stripped, trimmed).
    /// Null/whitespace yields null. Pure.
    /// </summary>
    public static string? SanitizeEditText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var stripped = HtmlTagRegex.Replace(text, string.Empty).Trim();
        return stripped.Length == 0 ? null : stripped;
    }

    /// <summary>
    /// Canonical request hash for an idempotency claim. Pure.
    /// </summary>
    public static string HashFor(
        Guid reviewId,
        ReviewMutationAction action,
        int expectedVersion,
        string? reason,
        string? editText)
    {
        return ConfigurationHashCalculator.Compute(new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["reviewId"] = reviewId.ToString("N"),
            ["action"] = action.ToString(),
            ["expectedVersion"] = expectedVersion,
            ["reason"] = (reason ?? string.Empty).Trim(),
            ["editText"] = (editText ?? string.Empty).Trim(),
        });
    }

    /// <summary>
    /// API action name for audit rows (<c>review.{action}</c>, no-dash style
    /// matching <see cref="ReviewService.AuditActionFor"/>).
    /// </summary>
    public static string AuditActionFor(ReviewMutationAction action)
    {
        return action switch
        {
            ReviewMutationAction.Resolve => "review.resolve",
            ReviewMutationAction.Dismiss => "review.dismiss",
            ReviewMutationAction.Reopen => "review.reopen",
            ReviewMutationAction.ResolveWithEdit => "review.resolvewithedit",
            _ => throw new DomainException($"Unknown review mutation '{action}'."),
        };
    }

    /// <summary>
    /// Executes one hardened mutation with per-review idempotency.
    /// </summary>
    public async Task<(ReviewMutationResult Result, bool IsReplay)> MutateAsync(
        Guid tenantId,
        Guid reviewId,
        ReviewMutationAction action,
        int expectedVersion,
        string? reason,
        string? editText,
        string? idempotencyKey,
        Guid actorUserId,
        string? correlationId = null,
        CancellationToken cancellationToken = default)
    {
        if (tenantId == Guid.Empty)
        {
            throw new DomainException("TenantId must not be empty.");
        }

        if (reviewId == Guid.Empty)
        {
            throw new DomainException("ReviewId must not be empty.");
        }

        if (actorUserId == Guid.Empty)
        {
            throw new DomainException("ActorUserId must not be empty.");
        }

        if (expectedVersion < 0)
        {
            throw new DomainException("ExpectedVersion must be >= 0.");
        }

        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Trim().Length > 256)
        {
            throw new ErrorCodeException(
                ErrorCodes.IdempotencyKeyRequired,
                "The Idempotency-Key header is required for this operation.");
        }

        var key = idempotencyKey.Trim();
        var endpoint = string.Concat("review-mutation:", reviewId.ToString("N"));
        var requestHash = HashFor(reviewId, action, expectedVersion, reason, editText);

        var claim = await _idempotency.TryClaimAsync(
            tenantId, endpoint, key, requestHash, IdempotencyExpiry, cancellationToken).ConfigureAwait(false);
        if (claim.IsReplay)
        {
            var replayed = TryParseStored(claim.Response)
                ?? throw new ConflictException($"Idempotency key '{key}' replay is unavailable; retry with a new key.");
            return (replayed, true);
        }

        try
        {
            var result = await ExecuteAsync(
                tenantId, reviewId, action, expectedVersion, reason, editText,
                key, actorUserId, correlationId, cancellationToken).ConfigureAwait(false);
            var stored = JsonSerializer.Serialize(
                new StoredResponse(200, JsonSerializer.Serialize(result, JsonOptions)), JsonOptions);
            await _idempotency.CompleteAsync(tenantId, endpoint, key, 200, stored, cancellationToken).ConfigureAwait(false);
            return (result, false);
        }
        catch
        {
            await _idempotency.FailAsync(tenantId, endpoint, key, cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<ReviewMutationResult> ExecuteAsync(
        Guid tenantId,
        Guid reviewId,
        ReviewMutationAction action,
        int expectedVersion,
        string? reason,
        string? editText,
        string idempotencyKey,
        Guid actorUserId,
        string? correlationId,
        CancellationToken cancellationToken)
    {
        var cleanedReason = SanitizeReason(reason)
            ?? throw new ReviewReasonRequiredException();
        var reviewer = actorUserId.ToString("D");
        var correlation = string.IsNullOrWhiteSpace(correlationId) ? null : correlationId.Trim();

        var review = await LoadOwnedReviewAsync(tenantId, reviewId, cancellationToken).ConfigureAwait(false);
        var currentVersion = await CountDecisionsAsync(tenantId, reviewId, cancellationToken).ConfigureAwait(false);
        if (expectedVersion != currentVersion)
        {
            throw new ReviewVersionConflictException(currentVersion);
        }

        var oldStatus = review.Status;
        ReviewMutationResult result;
        switch (action)
        {
            case ReviewMutationAction.Resolve:
                EnsureOpen(review, alreadyResolved: true);
                await _reviews.ApproveAsync(tenantId, reviewId, reviewer, cleanedReason, cancellationToken).ConfigureAwait(false);
                result = new ReviewMutationResult(reviewId, ReviewStatus.Approved.ToString(), currentVersion + 1, null, null);
                break;
            case ReviewMutationAction.Dismiss:
                EnsureOpen(review, alreadyResolved: true);
                await _reviews.RejectAsync(tenantId, reviewId, reviewer, cleanedReason, cancellationToken).ConfigureAwait(false);
                result = new ReviewMutationResult(reviewId, ReviewStatus.Rejected.ToString(), currentVersion + 1, null, null);
                break;
            case ReviewMutationAction.ResolveWithEdit:
                EnsureOpen(review, alreadyResolved: true);
                if (SanitizeEditText(editText) is null)
                {
                    throw new ReviewEditEmptyException();
                }

                var resolved = await _reviews.ResolveWithEditAsync(
                    tenantId, reviewId, reviewer, cleanedReason, editText, cancellationToken).ConfigureAwait(false);
                result = new ReviewMutationResult(
                    reviewId, ReviewStatus.ResolvedWithEdit.ToString(), currentVersion + 1,
                    resolved.ManualVersionId, resolved.VersionKind);
                break;
            case ReviewMutationAction.Reopen:
                if (review.Status == ReviewStatus.Open)
                {
                    throw new ReviewNotResolvedException(review.Status.ToString());
                }

                await ReopenAsync(tenantId, review, cleanedReason, reviewer, correlation, idempotencyKey, cancellationToken).ConfigureAwait(false);
                result = new ReviewMutationResult(reviewId, ReviewStatus.Open.ToString(), currentVersion + 1, null, null);
                break;
            default:
                throw new DomainException($"Unknown review mutation '{action}'.");
        }

        var details = SecretRedactor.Redact(JsonSerializer.Serialize(new
        {
            action = action.ToString(),
            reason = cleanedReason,
            oldStatus = oldStatus.ToString(),
            newStatus = result.Status,
            versionDelta = new
            {
                expectedVersion,
                previousVersion = currentVersion,
                newVersion = result.Version,
            },
            manualVersionId = result.ManualVersionId?.ToString("N"),
            versionKind = result.VersionKind,
            correlationId = correlation,
            idempotencyKey,
        }, JsonOptions));

        await _audit.LogAsync(
            tenantId, review.ProjectId, reviewer, AuditActionFor(action),
            "review", reviewId.ToString("N"), details, cancellationToken).ConfigureAwait(false);

        return result;
    }

    private static void EnsureOpen(ReviewItem review, bool alreadyResolved)
    {
        if (review.Status != ReviewStatus.Open && alreadyResolved)
        {
            throw new ReviewAlreadyResolvedException(review.Status.ToString());
        }
    }

    /// <summary>
    /// Flips a terminal review back to <c>Open</c> with a conditional update
    /// (losers get <c>REVIEW_NOT_RESOLVED</c> when a concurrent reopen won)
    /// and appends one <c>Requeue</c> decision row carrying the reopen linkage
    /// in metadata. Bypasses <see cref="StateMachines.ReviewStateMachine"/>
    /// (which pins terminal states as sinks for the Plan A forward path) —
    /// the bypass is explicit to this method and audited per mutation.
    /// </summary>
    private async Task ReopenAsync(
        Guid tenantId,
        ReviewItem review,
        string reason,
        string reviewer,
        string? correlationId,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var now = DateTimeOffset.UtcNow;
                var transitioned = await db.Database.ExecuteSqlRawAsync(
                    "UPDATE review_items SET status = {0}, updated_at = {1}, resolved_at = NULL " +
                    "WHERE id = {2} AND tenant_id = {3} AND status <> 'Open'",
                    ReviewStatus.Open.ToString(), now, review.Id, tenantId).ConfigureAwait(false);
                if (transitioned == 0)
                {
                    throw new ReviewNotResolvedException(ReviewStatus.Open.ToString());
                }

                db.Set<ReviewDecision>().Add(new ReviewDecision(
                    Guid.NewGuid(), tenantId, review.Id, ReviewDecisionType.Requeue,
                    reviewer.Trim(), reason,
                    JsonSerializer.Serialize(new
                    {
                        action = ReviewMutationAction.Reopen.ToString(),
                        fromStatus = review.Status.ToString(),
                        toStatus = ReviewStatus.Open.ToString(),
                        correlationId,
                        idempotencyKey,
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
    }

    private async Task<int> CountDecisionsAsync(Guid tenantId, Guid reviewId, CancellationToken cancellationToken)
    {
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            return await db.Set<ReviewDecision>()
                .CountAsync(d => d.ReviewItemId == reviewId, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<ReviewItem> LoadOwnedReviewAsync(
        Guid tenantId,
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

        if (item is null || item.TenantId != tenantId)
        {
            throw new NotFoundException($"Review '{reviewId}' was not found.");
        }

        return item;
    }

    private static ReviewMutationResult? TryParseStored(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored))
        {
            return null;
        }

        try
        {
            using var outer = JsonDocument.Parse(stored);
            if (outer.RootElement.ValueKind != JsonValueKind.Object
                || !outer.RootElement.TryGetProperty("body", out var bodyProp)
                || bodyProp.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var innerJson = bodyProp.GetString();
            if (string.IsNullOrWhiteSpace(innerJson))
            {
                return null;
            }

            return JsonSerializer.Deserialize<ReviewMutationResult>(innerJson, JsonOptions);
        }
#pragma warning disable CA1031 // Stored-response parsing is best effort; null means fail closed.
        catch (Exception)
        {
            return null;
        }
#pragma warning restore CA1031
    }

    private sealed record StoredResponse(int StatusCode, string Body);
}
