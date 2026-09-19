using System.Text.Json;
using DubbingPlatform.Api.Filters;
using DubbingPlatform.Api.Middleware;
using DubbingPlatform.Api.Models;
using DubbingPlatform.Application.Authorization;
using DubbingPlatform.Application.Common;
using DubbingPlatform.Application.Exceptions;
using DubbingPlatform.Application.MultiTenancy;
using DubbingPlatform.Application.Services;
using DubbingPlatform.Contracts.Messages;
using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Domain.Enums;
using DubbingPlatform.Infrastructure.Observability;
using MassTransit;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DubbingPlatform.Api.Controllers;

/// <summary>
/// Review surface:
/// <c>GET /api/v1/projects/{projectId}/reviews</c> (200 paginated),
/// <c>GET /api/v1/projects/{projectId}/reviews/{reviewId}</c> (200),
/// <c>GET /api/v1/reviews/{reviewId}</c> (200),
/// <c>POST /api/v1/reviews/{reviewId}/approve|reject|requeue|resolve</c>.
/// Decisions run through <see cref="ReviewService"/> (state-machine guarded,
/// decision row appended, manual version on resolve, run resumed only when no
/// open reviews remain — reject never resumes), then publish
/// <c>ReviewResolved</c> best effort (without a stage for rejects, so the saga
/// performs no redispatch) and audit <c>review.{decision}</c>. Missing rows
/// return 404, repeat decisions on terminal rows return 409, empty
/// resolve text returns 400, resolve on project-level reviews returns 400.
/// No 501s.
/// </summary>
[ApiController]
[Route("api/v1/reviews")]
[Authorize]
[ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
[ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status403Forbidden)]
public sealed class ReviewsController : ControllerBase
{
    private static readonly JsonSerializerOptions AuditJsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IStageExecutionContextFactory _contextFactory;
    private readonly ReviewService _reviews;
    private readonly AuditService _audit;
    private readonly IPublishEndpoint _publish;

    public ReviewsController(
        IStageExecutionContextFactory contextFactory,
        ReviewService reviews,
        AuditService audit,
        IPublishEndpoint publish)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        ArgumentNullException.ThrowIfNull(reviews);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(publish);
        _contextFactory = contextFactory;
        _reviews = reviews;
        _audit = audit;
        _publish = publish;
    }

    [HttpGet("/api/v1/projects/{projectId}/reviews")]
    [Authorize(Policy = AuthPolicies.RequireProjectViewer)]
    [ProducesResponseType(typeof(PaginatedResult<ReviewResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ListForProject(
        [FromRoute] string projectId,
        [FromQuery] PaginationParams? query,
        CancellationToken cancellationToken)
    {
        var tenantId = User.GetTenantId();
        var projectGuid = PublicIdParser.ParseProjectId(projectId);

        query ??= new PaginationParams();
        query.Normalize();
        var (page, pageSize) = query.Normalized();

        var (items, total) = await _reviews.ListAsync(tenantId, projectGuid, page, pageSize, cancellationToken).ConfigureAwait(false);
        var responses = items.Select(ToResponse).ToList();
        return Ok(PaginatedResult<ReviewResponse>.Create(responses, page, pageSize, total));
    }

    [HttpGet("/api/v1/projects/{projectId}/reviews/{reviewId}")]
    [Authorize(Policy = AuthPolicies.RequireProjectViewer)]
    [ProducesResponseType(typeof(ReviewResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetNested(
        [FromRoute] string projectId,
        [FromRoute] string reviewId,
        CancellationToken cancellationToken)
    {
        var tenantId = User.GetTenantId();
        var projectGuid = PublicIdParser.ParseProjectId(projectId);
        var reviewGuid = PublicIdParser.ParseReviewId(reviewId);

        var item = await _reviews.GetForProjectAsync(tenantId, projectGuid, reviewGuid, cancellationToken).ConfigureAwait(false);
        return Ok(ToResponse(item));
    }

    [HttpGet("{reviewId}")]
    [Authorize(Policy = AuthPolicies.RequireProjectViewer)]
    [ProducesResponseType(typeof(ReviewResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(
        [FromRoute] string reviewId,
        CancellationToken cancellationToken)
    {
        var tenantId = User.GetTenantId();
        var reviewGuid = PublicIdParser.ParseReviewId(reviewId);

        var item = await _reviews.GetAsync(tenantId, reviewGuid, cancellationToken).ConfigureAwait(false);
        return Ok(ToResponse(item));
    }

    [HttpPost("{reviewId}/approve")]
    [Authorize(Policy = AuthPolicies.RequireReviewer)]
    [ServiceFilter(typeof(IdempotencyFilter))]
    [ProducesResponseType(typeof(ReviewResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Approve(
        [FromRoute] string reviewId,
        [FromBody] ReviewDecisionRequest? request,
        [FromHeader(Name = IdempotencyService.HeaderName)] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        _ = idempotencyKey;
        return await DecideAsync(reviewId, ReviewDecisionType.Approve, request?.Reason, null, cancellationToken).ConfigureAwait(false);
    }

    [HttpPost("{reviewId}/reject")]
    [Authorize(Policy = AuthPolicies.RequireReviewer)]
    [ServiceFilter(typeof(IdempotencyFilter))]
    [ProducesResponseType(typeof(ReviewResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Reject(
        [FromRoute] string reviewId,
        [FromBody] ReviewDecisionRequest? request,
        [FromHeader(Name = IdempotencyService.HeaderName)] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        _ = idempotencyKey;
        return await DecideAsync(reviewId, ReviewDecisionType.Reject, request?.Reason, null, cancellationToken).ConfigureAwait(false);
    }

    [HttpPost("{reviewId}/requeue")]
    [Authorize(Policy = AuthPolicies.RequireReviewer)]
    [ServiceFilter(typeof(IdempotencyFilter))]
    [ProducesResponseType(typeof(ReviewResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Requeue(
        [FromRoute] string reviewId,
        [FromBody] ReviewDecisionRequest? request,
        [FromHeader(Name = IdempotencyService.HeaderName)] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        _ = idempotencyKey;
        return await DecideAsync(reviewId, ReviewDecisionType.Requeue, request?.Reason, null, cancellationToken).ConfigureAwait(false);
    }

    [HttpPost("{reviewId}/resolve")]
    [Authorize(Policy = AuthPolicies.RequireReviewer)]
    [ServiceFilter(typeof(IdempotencyFilter))]
    [ProducesResponseType(typeof(ReviewResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Resolve(
        [FromRoute] string reviewId,
        [FromBody] ReviewDecisionRequest? request,
        [FromHeader(Name = IdempotencyService.HeaderName)] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        _ = idempotencyKey;
        return await DecideAsync(reviewId, ReviewDecisionType.ResolveWithEdit, request?.Reason, request?.EditedText, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IActionResult> DecideAsync(
        string reviewId,
        ReviewDecisionType type,
        string? reason,
        string? editedText,
        CancellationToken cancellationToken)
    {
        var tenantId = User.GetTenantId();
        var reviewGuid = PublicIdParser.ParseReviewId(reviewId);
        var reviewer = User.GetSubject();

        ReviewItem item;
        string? resumeStage = null;
        if (type == ReviewDecisionType.ResolveWithEdit)
        {
            var resolved = await _reviews.ResolveWithEditAsync(
                tenantId, reviewGuid, reviewer, reason, editedText, cancellationToken).ConfigureAwait(false);
            item = await _reviews.GetAsync(tenantId, reviewGuid, cancellationToken).ConfigureAwait(false);
            resumeStage = ResolveResumeStage(item.Reason, item.ScopeType);
            await AuditAndPublishAsync(item, type, resumeStage, cancellationToken).ConfigureAwait(false);
            _ = resolved;
        }
        else
        {
            item = type switch
            {
                ReviewDecisionType.Approve => await _reviews.ApproveAsync(tenantId, reviewGuid, reviewer, reason, cancellationToken).ConfigureAwait(false),
                ReviewDecisionType.Reject => await _reviews.RejectAsync(tenantId, reviewGuid, reviewer, reason, cancellationToken).ConfigureAwait(false),
                ReviewDecisionType.Requeue => await _reviews.RequeueAsync(tenantId, reviewGuid, reviewer, reason, cancellationToken).ConfigureAwait(false),
                _ => throw new Domain.Exceptions.DomainException($"Unknown review decision '{type}'."),
            };

            // Reject never resumes: publish without a stage so the saga performs
            // no redispatch and the run stays blocked for requeue/retry.
            resumeStage = type == ReviewDecisionType.Reject
                ? null
                : ResolveResumeStage(item.Reason, item.ScopeType);
            await AuditAndPublishAsync(item, type, resumeStage, cancellationToken).ConfigureAwait(false);
        }

        PlatformMetrics.ReviewResolved(tenantId);
        return Ok(ToResponse(item));
    }

    /// <summary>
    /// Maps a review reason to the DAG stage to resume. Pure. Segment
    /// transcription reviews resume Transcription, translation reviews resume
    /// Translation, voice/audio reviews resume their stage, and project-level
    /// QC reviews resume QualityControl.
    /// </summary>
    public static string? ResolveResumeStage(string? reason, ScopeType scope)
    {
        var normalized = (reason ?? string.Empty).Trim().ToUpperInvariant();
        if (normalized.Contains("TRANSLAT", StringComparison.Ordinal))
        {
            return nameof(StageType.Translation);
        }

        if (normalized.Contains("TRANSCRIPT", StringComparison.Ordinal)
            || normalized.Contains("LOW_CONFIDENCE", StringComparison.Ordinal))
        {
            return nameof(StageType.Transcription);
        }

        if (normalized.Contains("TTS", StringComparison.Ordinal))
        {
            return nameof(StageType.VoiceGeneration);
        }

        if (normalized.Contains("MIX", StringComparison.Ordinal))
        {
            return nameof(StageType.AudioMixing);
        }

        if (normalized.Contains("QC", StringComparison.Ordinal))
        {
            return nameof(StageType.QualityControl);
        }

        if (normalized.Contains("SYNC", StringComparison.Ordinal)
            || normalized.Contains("TIMING", StringComparison.Ordinal))
        {
            return nameof(StageType.TimingOptimization);
        }

        return scope == ScopeType.Segment ? nameof(StageType.Translation) : nameof(StageType.QualityControl);
    }

    private async Task AuditAndPublishAsync(
        ReviewItem item,
        ReviewDecisionType type,
        string? resumeStage,
        CancellationToken cancellationToken)
    {
        await _audit.LogAsync(
            item.TenantId, item.ProjectId, User.GetSubject(), ReviewService.AuditActionFor(type),
            "review", item.Id.ToString("N"),
            JsonSerializer.Serialize(new { decision = type.ToString(), resumeStage }, AuditJsonOptions),
            cancellationToken).ConfigureAwait(false);

        ProcessingRun? run;
        using (TenantContext.BeginMaintenanceScope())
        {
            using var db = _contextFactory.CreateDbContext();
            run = await db.Set<ProcessingRun>()
                .AsNoTracking()
                .FirstOrDefaultAsync(r => r.Id == item.ProcessingRunId, cancellationToken).ConfigureAwait(false);
        }

        if (run is null || run.TenantId != item.TenantId)
        {
            return;
        }

        var correlationId = CorrelationIdMiddleware.GetCorrelationId(HttpContext);
        var message = new ReviewResolved(
            Guid.NewGuid(), correlationId,
            item.TenantId, item.ProjectId, item.ProcessingRunId,
            null, resumeStage, item.ScopeType.ToString(), item.ScopeId,
            item.SegmentId, 1, DateTimeOffset.UtcNow, run.Attempt,
            null, run.ConfigurationHash, run.ExecutionSnapshotHash,
            PublicIdParser.ToReviewId(item.Id), type.ToString());

        try
        {
            await _publish.Publish(message, cancellationToken).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Publish is best effort: the DB commit already succeeded.
        catch (Exception)
        {
        }
#pragma warning restore CA1031
    }

    private static ReviewResponse ToResponse(ReviewItem item)
    {
        return new ReviewResponse(
            PublicIdParser.ToReviewId(item.Id),
            PublicIdParser.ToProjectId(item.ProjectId),
            item.Status.ToString(),
            item.Reason,
            item.CreatedAt,
            item.ResolvedAt);
    }
}
