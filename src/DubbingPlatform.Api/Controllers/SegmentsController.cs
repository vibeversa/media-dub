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
using MassTransit;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DubbingPlatform.Api.Controllers;

/// <summary>
/// Segment reads nested under projects:
/// <c>GET /api/v1/projects/{projectId}/segments</c> (200 paginated, empty
/// until 022 populates segments),
/// <c>GET .../segments/{segmentId}</c> (200 or 404),
/// <c>POST .../segments/{segmentId}/retry</c> (202 or 404/409, Owner+).
/// Segment retry re-runs the segment's latest failed/review unit (409 when the
/// segment has no failed unit): the stage is resolved server-side from the
/// segment's executions, then delegated to <see cref="RetryService"/> (new
/// attempt, dependents-only invalidation) with <c>StageWorkRequested</c>
/// published best effort. Idempotent for 24h via <c>Idempotency-Key</c>.
/// </summary>
[ApiController]
[Route("api/v1/projects/{projectId}/segments")]
[Authorize]
[ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
[ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status403Forbidden)]
public sealed class SegmentsController : ControllerBase
{
    private static readonly JsonSerializerOptions AuditJsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IStageExecutionContextFactory _contextFactory;
    private readonly RetryService _retry;
    private readonly AuditService _audit;
    private readonly IPublishEndpoint _publish;

    public SegmentsController(
        IStageExecutionContextFactory contextFactory,
        RetryService retry,
        AuditService audit,
        IPublishEndpoint publish)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        ArgumentNullException.ThrowIfNull(retry);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(publish);
        _contextFactory = contextFactory;
        _retry = retry;
        _audit = audit;
        _publish = publish;
    }

    [HttpGet]
    [Authorize(Policy = AuthPolicies.RequireProjectViewer)]
    [ProducesResponseType(typeof(PaginatedResult<SegmentResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> List(
        [FromRoute] string projectId,
        [FromQuery] PaginationParams? query,
        CancellationToken cancellationToken)
    {
        var tenantId = User.GetTenantId();
        var projectGuid = PublicIdParser.ParseProjectId(projectId);
        await RequireProjectAsync(tenantId, projectGuid, cancellationToken).ConfigureAwait(false);

        query ??= new PaginationParams();
        query.Normalize();
        var (page, pageSize) = query.Normalized();

        List<Domain.Entities.SpeechSegment> items;
        long total;
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            var scoped = db.Set<Domain.Entities.SpeechSegment>().AsNoTracking()
                .Where(s => s.ProjectId == projectGuid);
            total = await scoped.LongCountAsync(cancellationToken).ConfigureAwait(false);
            items = await scoped
                .OrderBy(s => s.Sequence)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
        }

        var responses = items.Select(s => new SegmentResponse(
            PublicIdParser.ToSegmentId(s.Id),
            PublicIdParser.ToProjectId(s.ProjectId),
            s.Status, s.Sequence, s.StartMs, s.EndMs)).ToList();
        return Ok(PaginatedResult<SegmentResponse>.Create(responses, page, pageSize, total));
    }

    [HttpGet("{segmentId}")]
    [Authorize(Policy = AuthPolicies.RequireProjectViewer)]
    [ProducesResponseType(typeof(SegmentResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(
        [FromRoute] string projectId,
        [FromRoute] string segmentId,
        CancellationToken cancellationToken)
    {
        var tenantId = User.GetTenantId();
        var projectGuid = PublicIdParser.ParseProjectId(projectId);
        var segmentGuid = PublicIdParser.ParseSegmentId(segmentId);
        await RequireProjectAsync(tenantId, projectGuid, cancellationToken).ConfigureAwait(false);

        var segment = await LoadOwnedSegmentAsync(tenantId, projectGuid, segmentGuid, cancellationToken).ConfigureAwait(false);
        return Ok(new SegmentResponse(
            PublicIdParser.ToSegmentId(segment.Id),
            PublicIdParser.ToProjectId(segment.ProjectId),
            segment.Status, segment.Sequence, segment.StartMs, segment.EndMs));
    }

    [HttpPost("{segmentId}/retry")]
    [Authorize(Policy = AuthPolicies.RequireProjectOwner)]
    [ServiceFilter(typeof(IdempotencyFilter))]
    [ProducesResponseType(typeof(SegmentResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Retry(
        [FromRoute] string projectId,
        [FromRoute] string segmentId,
        [FromHeader(Name = IdempotencyService.HeaderName)] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        _ = idempotencyKey;
        var tenantId = User.GetTenantId();
        var projectGuid = PublicIdParser.ParseProjectId(projectId);
        var segmentGuid = PublicIdParser.ParseSegmentId(segmentId);
        await RequireProjectAsync(tenantId, projectGuid, cancellationToken).ConfigureAwait(false);

        var segment = await LoadOwnedSegmentAsync(tenantId, projectGuid, segmentGuid, cancellationToken).ConfigureAwait(false);
        var stage = await FindRetryStageAsync(tenantId, segment, cancellationToken).ConfigureAwait(false);
        if (stage is null)
        {
            throw new ConflictException($"Segment '{segmentGuid}' has no failed unit to retry.");
        }

        var descriptor = await _retry.RetryAsync(
            tenantId, projectGuid, "segment", stage.Value.ToString(), segment.Id,
            cancellationToken).ConfigureAwait(false);

        await _audit.LogAsync(
            tenantId, projectGuid, User.GetSubject(), RetryService.AuditAction,
            "segment", segment.Id.ToString("N"),
            JsonSerializer.Serialize(new
            {
                stage = descriptor.Stage.ToString(),
                attempt = descriptor.Attempt,
                invalidated = descriptor.InvalidatedStages,
            }, AuditJsonOptions),
            cancellationToken).ConfigureAwait(false);

        await PublishRetryAsync(tenantId, projectGuid, descriptor, cancellationToken).ConfigureAwait(false);

        return Accepted(new SegmentResponse(
            PublicIdParser.ToSegmentId(segment.Id),
            PublicIdParser.ToProjectId(segment.ProjectId),
            segment.Status, segment.Sequence, segment.StartMs, segment.EndMs));
    }

    private async Task<Domain.Entities.SpeechSegment> LoadOwnedSegmentAsync(
        Guid tenantId,
        Guid projectGuid,
        Guid segmentGuid,
        CancellationToken cancellationToken)
    {
        Domain.Entities.SpeechSegment? segment;
        using (TenantContext.BeginMaintenanceScope())
        {
            using var db = _contextFactory.CreateDbContext();
            segment = await db.Set<Domain.Entities.SpeechSegment>()
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.Id == segmentGuid, cancellationToken).ConfigureAwait(false);
        }

        if (segment is null)
        {
            throw new NotFoundException($"Segment '{segmentGuid}' was not found.");
        }

        if (segment.TenantId != tenantId || segment.ProjectId != projectGuid)
        {
            throw new ForbiddenException($"Segment '{segmentGuid}' does not belong to the current tenant/project.");
        }

        return segment;
    }

    private async Task<StageType?> FindRetryStageAsync(
        Guid tenantId,
        Domain.Entities.SpeechSegment segment,
        CancellationToken cancellationToken)
    {
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            var failed = await db.Set<StageExecution>()
                .AsNoTracking()
                .Where(e => e.ProcessingRunId == segment.RunId && e.SegmentId == segment.Id && e.Status == StageStatus.Failed)
                .OrderByDescending(e => e.CreatedAt)
                .Select(e => (StageType?)e.StageType)
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (failed.HasValue)
            {
                return failed.Value;
            }

            var review = await db.Set<StageExecution>()
                .AsNoTracking()
                .Where(e => e.ProcessingRunId == segment.RunId && e.SegmentId == segment.Id && e.Status == StageStatus.ManualReviewRequired)
                .OrderByDescending(e => e.CreatedAt)
                .Select(e => (StageType?)e.StageType)
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (review.HasValue)
            {
                return review.Value;
            }

            return await db.Set<StageExecution>()
                .AsNoTracking()
                .Where(e => e.ProcessingRunId == segment.RunId && e.SegmentId == segment.Id && e.Status == StageStatus.RetryPending)
                .OrderByDescending(e => e.CreatedAt)
                .Select(e => (StageType?)e.StageType)
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task PublishRetryAsync(
        Guid tenantId,
        Guid projectId,
        RetryDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        ProcessingRun? run;
        using (TenantContext.BeginMaintenanceScope())
        {
            using var db = _contextFactory.CreateDbContext();
            run = await db.Set<ProcessingRun>()
                .AsNoTracking()
                .FirstOrDefaultAsync(r => r.Id == descriptor.RunId, cancellationToken).ConfigureAwait(false);
        }

        if (run is null || run.TenantId != tenantId || run.ProjectId != projectId)
        {
            return;
        }

        var correlationId = CorrelationIdMiddleware.GetCorrelationId(HttpContext);
        var stageName = descriptor.Stage.ToString();
        var scopeName = descriptor.Scope.ToString();
        var message = new StageWorkRequested(
            Guid.NewGuid(), correlationId, tenantId, projectId, descriptor.RunId,
            null, stageName, scopeName, descriptor.ScopeId,
            descriptor.SegmentId, 1, DateTimeOffset.UtcNow, descriptor.Attempt,
            null, run.ConfigurationHash, run.ExecutionSnapshotHash,
            stageName, scopeName, descriptor.ScopeId, null);

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
}
