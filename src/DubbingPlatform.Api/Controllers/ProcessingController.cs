using System.Text.Json;
using DubbingPlatform.Api.Filters;
using DubbingPlatform.Api.Middleware;
using DubbingPlatform.Api.Models;
using DubbingPlatform.Application.Authorization;
using DubbingPlatform.Application.Exceptions;
using DubbingPlatform.Application.MultiTenancy;
using DubbingPlatform.Application.Services;
using DubbingPlatform.Contracts.Messages;
using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Domain.Enums;
using DubbingPlatform.Infrastructure.Orchestration;
using MassTransit;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DubbingPlatform.Api.Controllers;

/// <summary>
/// Processing surface nested under projects:
/// <c>POST /api/v1/projects/{projectId}/processing</c> (start, 409 when media
/// not ready or a run is already active),
/// <c>GET /api/v1/projects/{projectId}/processing</c> (active run),
/// <c>POST .../processing/cancel</c> (durable cancel, Owner+, 24h idempotent),
/// <c>POST .../processing/retry</c> (selective stage/segment retry, Owner+,
/// 24h idempotent),
/// <c>GET .../progress</c> (durable unit-state progress, indicator only),
/// <c>GET .../progress/stream</c> (SSE <c>text/event-stream</c>, 2s poll),
/// <c>GET .../processing/{runId}</c>.
/// Cancel flips the run to <c>Cancelling</c> (conditional update: exactly one
/// terminal state when racing completion) and publishes
/// <c>RunCancelledRequested</c> best effort; retry invalidates only transitive
/// downstream dependents (new stage-execution attempt, immutable history
/// preserved) and publishes <c>StageWorkRequested</c> best effort. SSE polls
/// <see cref="ProgressService"/> every 2s; Redis pub/sub
/// (<c>progress:{tenant}:{project}</c>) is a future push optimization — the
/// poll loop is the implemented transport and stays correct without Redis.
/// </summary>
[ApiController]
[Route("api/v1/projects/{projectId}")]
[Authorize]
[ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
[ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status403Forbidden)]
public sealed class ProcessingController : ControllerBase
{
    private static readonly JsonSerializerOptions SseJsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IStageExecutionContextFactory _contextFactory;
    private readonly ProcessingStartService _starts;
    private readonly ICostGate _costGate;
    private readonly CostService? _costs;
    private readonly ProgressService _progress;
    private readonly CancellationService _cancellation;
    private readonly RetryService _retry;
    private readonly AuditService _audit;
    private readonly IPublishEndpoint _publish;

    public ProcessingController(
        IStageExecutionContextFactory contextFactory,
        ProcessingStartService starts,
        ICostGate costGate,
        ProgressService progress,
        CancellationService cancellation,
        RetryService retry,
        AuditService audit,
        IPublishEndpoint publish,
        CostService? costs = null)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        ArgumentNullException.ThrowIfNull(starts);
        ArgumentNullException.ThrowIfNull(costGate);
        ArgumentNullException.ThrowIfNull(progress);
        ArgumentNullException.ThrowIfNull(cancellation);
        ArgumentNullException.ThrowIfNull(retry);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(publish);
        _contextFactory = contextFactory;
        _starts = starts;
        _costGate = costGate;
        _costs = costs;
        _progress = progress;
        _cancellation = cancellation;
        _retry = retry;
        _audit = audit;
        _publish = publish;
    }

    /// <summary>
    /// Starts a processing run. Requires validated media (project
    /// <c>MediaReady</c>), else 409 CONFLICT; a second start with an active
    /// run returns 409 CONFLICT. Quota exhaustion returns 429
    /// <c>QUOTA_EXCEEDED</c>; tenant-policy denial returns 403
    /// <c>POLICY_DENIED</c>. Creates Attempt=(max+1), PipelineVersion
    /// <c>1.0.0</c>, deterministic hashes, one <c>RunStageSummary</c> per DAG
    /// stage, and transitions the project to Processing. Idempotent for 7d
    /// via <c>Idempotency-Key</c>.
    /// </summary>
    [HttpPost("processing")]
    [Authorize(Policy = AuthPolicies.RequireProjectEditor)]
    [ServiceFilter(typeof(IdempotencyFilter))]
    [ProducesResponseType(typeof(ProcessingRunResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Start(
        [FromRoute] string projectId,
        [FromBody] StartProcessingRequest? request,
        [FromHeader(Name = IdempotencyService.HeaderName)] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        _ = idempotencyKey;
        _ = request;
        var tenantId = User.GetTenantId();
        var projectGuid = PublicIdParser.ParseProjectId(projectId);

        var runId = Guid.NewGuid();
        if (!await _costGate.CanProceedAsync(tenantId, runId, nameof(StageType.MediaAnalysis), 1, cancellationToken).ConfigureAwait(false))
        {
            throw new QuotaExceededException("Cost budget would be exceeded by starting processing.");
        }

        // Task 036 start preflight: pipeline estimate (transcription +
        // translation + TTS) against Quota:MaxCostPerProject. Check-only; the
        // dispatch gate above stays the frozen contract. Null when CostService
        // is not wired (tests); production wires it for real preflights.
        if (_costs is not null)
        {
            await _costs.PreflightAsync(tenantId, projectGuid, runId, cancellationToken).ConfigureAwait(false);
        }

        var started = await _starts.StartAsync(tenantId, projectGuid, User.GetSubject(), runId, cancellationToken).ConfigureAwait(false);

        await _audit.LogAsync(
            tenantId, projectGuid, User.GetSubject(), "processing.start",
            "processing-run", runId.ToString("N"), null, cancellationToken).ConfigureAwait(false);

        await PublishRunStartedAsync(tenantId, projectGuid, started, cancellationToken).ConfigureAwait(false);

        return Accepted(new ProcessingRunResponse(
            PublicIdParser.ToRunId(started.RunId),
            PublicIdParser.ToProjectId(projectGuid),
            started.Status,
            started.Attempt,
            started.CreatedAt,
            started.StartedAt));
    }

    /// <summary>
    /// Gets the active processing run (404 when none).
    /// </summary>
    [HttpGet("processing")]
    [Authorize(Policy = AuthPolicies.RequireProjectViewer)]
    [ProducesResponseType(typeof(ProcessingRunResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetActive(
        [FromRoute] string projectId,
        CancellationToken cancellationToken)
    {
        var tenantId = User.GetTenantId();
        var projectGuid = PublicIdParser.ParseProjectId(projectId);
        await RequireProjectAsync(tenantId, projectGuid, cancellationToken).ConfigureAwait(false);

        var active = await FindActiveRunAsync(tenantId, projectGuid, cancellationToken).ConfigureAwait(false);
        if (active is null)
        {
            throw new NotFoundException($"Project '{projectGuid}' has no active processing run.");
        }

        return Ok(ToResponse(projectGuid, active));
    }

    /// <summary>
    /// Gets a processing run by id (raw GUID or <c>run_</c>).
    /// </summary>
    [HttpGet("processing/{runId}")]
    [Authorize(Policy = AuthPolicies.RequireProjectViewer)]
    [ProducesResponseType(typeof(ProcessingRunResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetRun(
        [FromRoute] string projectId,
        [FromRoute] string runId,
        CancellationToken cancellationToken)
    {
        var tenantId = User.GetTenantId();
        var projectGuid = PublicIdParser.ParseProjectId(projectId);
        var runGuid = PublicIdParser.ParseRunId(runId);
        await RequireProjectAsync(tenantId, projectGuid, cancellationToken).ConfigureAwait(false);

        ProcessingRun? run;
        using (TenantContext.BeginMaintenanceScope())
        {
            using var db = _contextFactory.CreateDbContext();
            run = await db.Set<ProcessingRun>()
                .AsNoTracking()
                .FirstOrDefaultAsync(r => r.Id == runGuid, cancellationToken).ConfigureAwait(false);
        }

        if (run is null)
        {
            throw new NotFoundException($"Run '{runGuid}' was not found.");
        }

        if (run.TenantId != tenantId || run.ProjectId != projectGuid)
        {
            throw new ForbiddenException($"Run '{runGuid}' does not belong to the current tenant/project.");
        }

        return Ok(ToResponse(projectGuid, run));
    }

    /// <summary>
    /// Cancels the active run (404 when none, 409 when already terminal).
    /// Durable: the run moves to <c>Cancelling</c> (new scheduling blocked,
    /// in-flight work drains), reaching <c>Cancelled</c> only after the sweeper
    /// observes zero running executions. Idempotent for 24h via
    /// <c>Idempotency-Key</c>.
    /// </summary>
    [HttpPost("processing/cancel")]
    [Authorize(Policy = AuthPolicies.RequireProjectOwner)]
    [ServiceFilter(typeof(IdempotencyFilter))]
    [ProducesResponseType(typeof(ProcessingRunResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Cancel(
        [FromRoute] string projectId,
        [FromBody] CancelRequest? request,
        [FromHeader(Name = IdempotencyService.HeaderName)] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        _ = idempotencyKey;
        var tenantId = User.GetTenantId();
        var projectGuid = PublicIdParser.ParseProjectId(projectId);
        var reason = string.IsNullOrWhiteSpace(request?.Reason) ? "operator-requested" : request.Reason.Trim();

        var result = await _cancellation.CancelAsync(tenantId, projectGuid, reason, cancellationToken).ConfigureAwait(false);

        await _audit.LogAsync(
            tenantId, projectGuid, User.GetSubject(), CancellationService.AuditAction,
            "processing-run", result.RunId.ToString("N"),
            JsonSerializer.Serialize(new { reason }, SseJsonOptions),
            cancellationToken).ConfigureAwait(false);

        await PublishCancelRequestedAsync(tenantId, projectGuid, result.RunId, reason, cancellationToken).ConfigureAwait(false);

        var run = await FindRunByIdAsync(tenantId, projectGuid, result.RunId, cancellationToken).ConfigureAwait(false);
        return Accepted(ToResponse(projectGuid, run));
    }

    /// <summary>
    /// Selectively retries one stage or one segment unit. Body
    /// <c>{scope, stageType, segmentId?}</c> with <c>scope</c> =
    /// <c>stage|segment</c>. Only transitive downstream dependents are
    /// invalidated (new attempt, immutable history preserved); upstream stages
    /// are untouched. 409 when the run is cancelled/terminal or the attempt
    /// budget (<c>Retry:ManualRetryMaxAttempts</c>) is exhausted; 404 for
    /// unknown segments. Idempotent for 24h via <c>Idempotency-Key</c>.
    /// </summary>
    [HttpPost("processing/retry")]
    [Authorize(Policy = AuthPolicies.RequireProjectOwner)]
    [ServiceFilter(typeof(IdempotencyFilter))]
    [ProducesResponseType(typeof(ProcessingRunResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Retry(
        [FromRoute] string projectId,
        [FromBody] RetryRequest? request,
        [FromHeader(Name = IdempotencyService.HeaderName)] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        _ = idempotencyKey;
        var tenantId = User.GetTenantId();
        var projectGuid = PublicIdParser.ParseProjectId(projectId);
        if (request is null)
        {
            throw new Domain.Exceptions.DomainException("Retry requires a body with {scope, stageType, segmentId?}.");
        }

        Guid? segmentGuid = null;
        if (!string.IsNullOrWhiteSpace(request.SegmentId))
        {
            segmentGuid = PublicIdParser.ParseSegmentId(request.SegmentId);
        }

        var descriptor = await _retry.RetryAsync(
            tenantId, projectGuid, request.Scope, request.StageType ?? string.Empty, segmentGuid,
            cancellationToken).ConfigureAwait(false);

        await _audit.LogAsync(
            tenantId, projectGuid, User.GetSubject(), RetryService.AuditAction,
            "processing-run", descriptor.RunId.ToString("N"),
            JsonSerializer.Serialize(new
            {
                stage = descriptor.Stage.ToString(),
                scope = descriptor.Scope.ToString(),
                attempt = descriptor.Attempt,
                invalidated = descriptor.InvalidatedStages,
            }, SseJsonOptions),
            cancellationToken).ConfigureAwait(false);

        await PublishRetryAsync(tenantId, projectGuid, descriptor, cancellationToken).ConfigureAwait(false);

        var run = await FindRunByIdAsync(tenantId, projectGuid, descriptor.RunId, cancellationToken).ConfigureAwait(false);
        return Accepted(ToResponse(projectGuid, run));
    }

    /// <summary>
    /// Gets durable progress for the active run (else the latest run; 404 when
    /// the project never started). Counts come from barrier summaries and
    /// execution states; <c>percentageIndicator</c> is display-only with
    /// <c>notEta:true</c>.
    /// </summary>
    [HttpGet("progress")]
    [Authorize(Policy = AuthPolicies.RequireProjectViewer)]
    [ProducesResponseType(typeof(ProgressResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetProgress(
        [FromRoute] string projectId,
        CancellationToken cancellationToken)
    {
        var tenantId = User.GetTenantId();
        var projectGuid = PublicIdParser.ParseProjectId(projectId);

        var snapshot = await _progress.GetAsync(tenantId, projectGuid, cancellationToken).ConfigureAwait(false);
        return Ok(ToProgressResponse(snapshot));
    }

    /// <summary>
    /// Streams progress as Server-Sent Events (<c>text/event-stream</c>): one
    /// <c>data:</c> JSON progress document every 2 seconds until the client
    /// disconnects. Requires the project Viewer role. Poll-based (correct
    /// without Redis); a future Redis <c>progress:{tenant}:{project}</c>
    /// subscriber can push between polls without changing the event shape.
    /// </summary>
    [HttpGet("progress/stream")]
    [Authorize(Policy = AuthPolicies.RequireProjectViewer)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task StreamProgress(
        [FromRoute] string projectId,
        CancellationToken cancellationToken)
    {
        var tenantId = User.GetTenantId();
        var projectGuid = PublicIdParser.ParseProjectId(projectId);

        Response.Headers.CacheControl = "no-cache";
        Response.Headers.ContentType = "text/event-stream";
        Response.Headers["X-Accel-Buffering"] = "no";

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var snapshot = await _progress.GetAsync(tenantId, projectGuid, HttpContext.RequestAborted).ConfigureAwait(false);
                var payload = JsonSerializer.Serialize(ToProgressResponse(snapshot), SseJsonOptions);
                await Response.WriteAsync(string.Concat("data: ", payload, "\n\n"), cancellationToken).ConfigureAwait(false);
                await Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Client disconnected; SSE streams end by cancellation, never an error envelope.
        }
    }

    private static ProcessingRunResponse ToResponse(Guid projectId, ProcessingRun run)
    {
        return new ProcessingRunResponse(
            PublicIdParser.ToRunId(run.Id),
            PublicIdParser.ToProjectId(projectId),
            run.Status.ToString(),
            run.Attempt,
            run.CreatedAt,
            run.StartedAt);
    }

    private static ProgressResponse ToProgressResponse(ProgressSnapshot snapshot)
    {
        return new ProgressResponse(
            PublicIdParser.ToProjectId(snapshot.ProjectId),
            snapshot.RunId.HasValue ? PublicIdParser.ToRunId(snapshot.RunId.Value) : null,
            snapshot.Status,
            snapshot.Phase,
            snapshot.CurrentStage,
            snapshot.CompletedUnits,
            snapshot.FailedUnits,
            snapshot.RetryingUnits,
            snapshot.ReviewUnits,
            snapshot.SkippedUnits,
            snapshot.ExpectedUnits,
            snapshot.EstimatedRemaining,
            snapshot.PercentageIndicator,
            snapshot.NotEta,
            snapshot.Warnings,
            snapshot.GeneratedAt);
    }

    private async Task<DubbingProject> RequireProjectAsync(Guid tenantId, Guid projectId, CancellationToken cancellationToken)
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

        return project;
    }

    private async Task<ProcessingRun?> FindActiveRunAsync(Guid tenantId, Guid projectId, CancellationToken cancellationToken)
    {
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            return await db.Set<ProcessingRun>()
                .AsNoTracking()
                .Where(r => r.ProjectId == projectId
                    && (r.Status == ProcessingRunStatus.Pending || r.Status == ProcessingRunStatus.Running || r.Status == ProcessingRunStatus.Cancelling || r.Status == ProcessingRunStatus.ManualReviewRequired))
                .OrderByDescending(r => r.CreatedAt)
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<ProcessingRun> FindRunByIdAsync(Guid tenantId, Guid projectId, Guid runId, CancellationToken cancellationToken)
    {
        ProcessingRun? run;
        using (TenantContext.BeginMaintenanceScope())
        {
            using var db = _contextFactory.CreateDbContext();
            run = await db.Set<ProcessingRun>()
                .AsNoTracking()
                .FirstOrDefaultAsync(r => r.Id == runId, cancellationToken).ConfigureAwait(false);
        }

        if (run is null)
        {
            throw new NotFoundException($"Run '{runId}' was not found.");
        }

        if (run.TenantId != tenantId || run.ProjectId != projectId)
        {
            throw new ForbiddenException($"Run '{runId}' does not belong to the current tenant/project.");
        }

        return run;
    }

    private async Task PublishRunStartedAsync(Guid tenantId, Guid projectId, ProcessingStartResult started, CancellationToken cancellationToken)
    {
        var correlationId = CorrelationIdMiddleware.GetCorrelationId(HttpContext);
        var message = new RunStarted(
            Guid.NewGuid(), correlationId, tenantId, projectId, started.RunId,
            null, null, null, null, null, 1, DateTimeOffset.UtcNow, started.Attempt,
            null, started.ConfigurationHash, started.ExecutionSnapshotHash,
            started.PipelineVersion, started.ProviderRouteHash);

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

    private async Task PublishCancelRequestedAsync(
        Guid tenantId,
        Guid projectId,
        Guid runId,
        string reason,
        CancellationToken cancellationToken)
    {
        var run = await FindRunByIdAsync(tenantId, projectId, runId, cancellationToken).ConfigureAwait(false);
        var correlationId = CorrelationIdMiddleware.GetCorrelationId(HttpContext);
        var message = new RunCancelledRequested(
            Guid.NewGuid(), correlationId, tenantId, projectId, runId,
            null, null, null, null, null, 1, DateTimeOffset.UtcNow, run.Attempt,
            null, run.ConfigurationHash, run.ExecutionSnapshotHash,
            reason);

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

    private async Task PublishRetryAsync(
        Guid tenantId,
        Guid projectId,
        RetryDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        var run = await FindRunByIdAsync(tenantId, projectId, descriptor.RunId, cancellationToken).ConfigureAwait(false);
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
}
