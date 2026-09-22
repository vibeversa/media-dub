using System.Text.Json;
using DubbingPlatform.Api.Filters;
using DubbingPlatform.Api.Middleware;
using DubbingPlatform.Api.Models;
using DubbingPlatform.Application.Authorization;
using DubbingPlatform.Application.Common;
using DubbingPlatform.Application.Errors;
using DubbingPlatform.Application.Exceptions;
using DubbingPlatform.Application.MultiTenancy;
using DubbingPlatform.Application.Orchestration;
using DubbingPlatform.Application.Processing;
using DubbingPlatform.Application.Services;
using DubbingPlatform.Application.StateMachines;
using DubbingPlatform.Contracts.Messages;
using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Domain.Enums;
using DubbingPlatform.Infrastructure.Orchestration;
using DubbingPlatform.Infrastructure.Observability;
using MassTransit;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DubbingPlatform.Api.Controllers;

/// <summary>
/// Processing surface nested under projects:
/// <c>POST /api/v1/projects/{projectId}/processing</c> (start, 202; replay
/// same key+body → 200 + <c>Idempotent-Replayed: true</c>; missing key → 400
/// <c>IDEMPOTENCY_KEY_REQUIRED</c>; archived → 409 <c>PROJECT_ARCHIVED</c>;
/// active run → 409 <c>RUN_ALREADY_ACTIVE</c> unless <c>?force=true</c> with
/// <c>processing.retry</c>),
/// <c>GET /api/v1/projects/{projectId}/processing</c> (run list, paginated),
/// <c>GET .../processing/active</c> (active run, legacy),
/// <c>POST .../processing/{runId}/cancel</c> (202 idempotent; terminal → 409
/// <c>RUN_ALREADY_TERMINAL</c>),
/// <c>POST .../processing/{runId}/retry</c> (run-level retry: new run linked
/// via <c>retryOfRunId</c>, copies config hash; changed settings → 409
/// <c>CONFIG_CHANGED_SINCE_RUN</c>; reuse of a start key → 422
/// <c>IDEMPOTENCY_KEY_REUSED</c>),
/// legacy <c>POST .../processing/cancel</c> and <c>POST .../processing/retry</c>
/// (selective stage retry) are preserved,
/// <c>GET .../progress</c> (durable unit-state progress with
/// <c>percentApproximate</c> alias, indicator only),
/// <c>GET .../progress/stream</c> (SSE <c>text/event-stream</c>, 2s poll,
/// query <c>access_token</c> supported, <c>Last-Event-ID</c> accepted with
/// replay deferred to Task 013),
/// <c>GET .../processing/{runId}</c>.
/// Cancel flips the run to <c>Cancelling</c> (conditional update: exactly one
/// terminal state when racing completion) and publishes
/// <c>RunCancelledRequested</c> best effort; run-level retry inserts a new
/// <c>ProcessingRun</c> (Attempt=max+1, config hash copied) plus stage
/// summaries and transitions Failed→Processing (manual-retry allowed).
/// SSE polls <see cref="ProgressService"/> every 2s; Redis pub/sub
/// (<c>progress:{tenant}:{project}</c>) is a future push optimization — the
/// poll loop is the implemented transport and stays correct without Redis.
/// All mutations audit with correlationId + idempotency key (where applicable).
/// Cost fields are display approximations; billing stays in the Plan A ledger.
/// </summary>
[ApiController]
[Route("api/v1/projects/{projectId}")]
[Authorize]
[ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
[ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status403Forbidden)]
public sealed class ProcessingController : ControllerBase
{
    private static readonly JsonSerializerOptions SseJsonOptions = new(JsonSerializerDefaults.Web);

    private static readonly ProcessingRunStatus[] ActiveStatuses =
    [
        ProcessingRunStatus.Pending,
        ProcessingRunStatus.Running,
        ProcessingRunStatus.Cancelling,
        ProcessingRunStatus.ManualReviewRequired,
    ];

    private readonly IStageExecutionContextFactory _contextFactory;
    private readonly ProcessingStartService _starts;
    private readonly ICostGate _costGate;
    private readonly CostService? _costs;
    private readonly ProgressService _progress;
    private readonly CancellationService _cancellation;
    private readonly RetryService _retry;
    private readonly AuditService _audit;
    private readonly IPublishEndpoint _publish;
    private readonly ProcessingIdempotency _processingIdempotency;

    public ProcessingController(
        IStageExecutionContextFactory contextFactory,
        ProcessingStartService starts,
        ICostGate costGate,
        ProgressService progress,
        CancellationService cancellation,
        RetryService retry,
        AuditService audit,
        IPublishEndpoint publish,
        ProcessingIdempotency processingIdempotency,
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
        ArgumentNullException.ThrowIfNull(processingIdempotency);
        _contextFactory = contextFactory;
        _starts = starts;
        _costGate = costGate;
        _progress = progress;
        _cancellation = cancellation;
        _retry = retry;
        _audit = audit;
        _publish = publish;
        _processingIdempotency = processingIdempotency;
        _costs = costs;
    }

    /// <summary>
    /// Starts a processing run. Requires <c>Idempotency-Key</c> (else 400
    /// <c>IDEMPOTENCY_KEY_REQUIRED</c>) with 24h processing-scoped idempotency:
    /// same key+body replays the same run (second response 200 +
    /// <c>Idempotent-Replayed: true</c>, only one run created); same key with a
    /// different body → 422 <c>IDEMPOTENCY_KEY_REUSED</c>. Requires validated
    /// media (project <c>MediaReady</c>), else 409 CONFLICT; archived projects
    /// → 409 <c>PROJECT_ARCHIVED</c>; a second start with an active run → 409
    /// <c>RUN_ALREADY_ACTIVE</c> unless <c>?force=true</c> with
    /// <c>processing.retry</c>. Quota exhaustion → 429 <c>QUOTA_EXCEEDED</c>;
    /// tenant-policy denial → 403 <c>POLICY_DENIED</c>. Creates
    /// Attempt=(max+1), PipelineVersion <c>1.0.0</c>, deterministic hashes, one
    /// <c>RunStageSummary</c> per DAG stage, and transitions the project to
    /// Processing. Example 202: <c>{ runId: "run_...", status: "Pending" }</c>;
    /// 409 example: <c>{ error: { code: "RUN_ALREADY_ACTIVE", ... } }</c>.
    /// </summary>
    [HttpPost("processing")]
    [Authorize(Policy = AuthPolicies.RequireProjectEditor)]
    [ProducesResponseType(typeof(ProcessingRunResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ProcessingRunResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Start(
        [FromRoute] string projectId,
        [FromBody] StartProcessingRequest? request,
        [FromHeader(Name = IdempotencyService.HeaderName)] string? idempotencyKey,
        [FromQuery] bool force = false,
        CancellationToken cancellationToken = default)
    {
        var key = ProcessingIdempotency.RequireKey(idempotencyKey);
        var tenantId = User.GetTenantId();
        var projectGuid = PublicIdParser.ParseProjectId(projectId);
        var correlationId = CorrelationIdMiddleware.GetCorrelationId(HttpContext);

        var bodyJson = JsonSerializer.Serialize(
            new { pipelineVersion = request?.PipelineVersion, force },
            SseJsonOptions);
        var requestHash = ProcessingIdempotency.HashFor(projectGuid, bodyJson, force);

        var claim = await _processingIdempotency.TryClaimAsync(tenantId, key, requestHash, cancellationToken).ConfigureAwait(false);
        if (claim.IsReplay && claim.RunId.HasValue)
        {
            var replayed = await FindRunByIdAsync(tenantId, projectGuid, claim.RunId.Value, cancellationToken).ConfigureAwait(false);
            Response.Headers[ProcessingIdempotency.ReplayedHeaderName] = "true";
            return Ok(ToResponse(projectGuid, replayed));
        }

        try
        {
            var project = await RequireProjectAsync(tenantId, projectGuid, cancellationToken).ConfigureAwait(false);
            if (project.IsArchived)
            {
                throw new ErrorCodeException(ErrorCodes.ProjectArchived, $"Project '{projectGuid}' is archived and cannot start processing.");
            }

            var active = await FindActiveRunAsync(tenantId, projectGuid, cancellationToken).ConfigureAwait(false);
            if (active is not null && !force)
            {
                throw new ErrorCodeException(
                    ErrorCodes.RunAlreadyActive,
                    $"Project '{projectGuid}' already has an active run '{active.Id}' ({active.Status}).");
            }

            if (active is not null && force && !HasRetryPermission())
            {
                throw new ErrorCodeException(
                    ErrorCodes.RunAlreadyActive,
                    $"Project '{projectGuid}' already has an active run '{active.Id}' ({active.Status}).");
            }

            var runId = Guid.NewGuid();
            if (!await _costGate.CanProceedAsync(tenantId, runId, nameof(StageType.MediaAnalysis), 1, cancellationToken).ConfigureAwait(false))
            {
                throw new QuotaExceededException("Cost budget would be exceeded by starting processing.");
            }

            if (_costs is not null)
            {
                await _costs.PreflightAsync(tenantId, projectGuid, runId, cancellationToken).ConfigureAwait(false);
            }

            ProcessingStartResult started;
            try
            {
                started = await _starts.StartAsync(tenantId, projectGuid, User.GetSubject(), runId, cancellationToken).ConfigureAwait(false);
            }
            catch (ConflictException ex) when (ex.Message.Contains("active", StringComparison.OrdinalIgnoreCase))
            {
                throw new ErrorCodeException(ErrorCodes.RunAlreadyActive, ex.Message);
            }

            await _audit.LogAsync(
                tenantId, projectGuid, User.GetSubject(), "processing.start",
                "processing-run", runId.ToString("N"),
                AuditDetails(correlationId, key),
                cancellationToken).ConfigureAwait(false);

            await PublishRunStartedAsync(tenantId, projectGuid, started, cancellationToken).ConfigureAwait(false);

            var response = new ProcessingRunResponse(
                PublicIdParser.ToRunId(started.RunId),
                PublicIdParser.ToProjectId(projectGuid),
                started.Status,
                started.Attempt,
                started.CreatedAt,
                started.StartedAt,
                null,
                started.ConfigurationHash);

            await _processingIdempotency.CompleteAsync(
                tenantId, key, StatusCodes.Status202Accepted, started.RunId,
                PublicIdParser.ToProjectId(projectGuid), started.Status, cancellationToken).ConfigureAwait(false);

            return Accepted(response);
        }
        catch
        {
            await _processingIdempotency.FailAsync(tenantId, key, cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Lists processing runs for the project (newest first, paginated).
    /// Example: <c>{ items: [{ runId: "run_...", status: "Running" }], page: 1,
    /// pageSize: 20, total: 2, hasMore: false }</c>.
    /// </summary>
    [HttpGet("processing")]
    [Authorize(Policy = AuthPolicies.RequireProjectViewer)]
    [ProducesResponseType(typeof(ProcessingRunListResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ListRuns(
        [FromRoute] string projectId,
        [FromQuery] int? page,
        [FromQuery] int? pageSize,
        CancellationToken cancellationToken)
    {
        var tenantId = User.GetTenantId();
        var projectGuid = PublicIdParser.ParseProjectId(projectId);
        await RequireProjectAsync(tenantId, projectGuid, cancellationToken).ConfigureAwait(false);

        var query = new PaginationParams { Page = page ?? 1, PageSize = pageSize ?? PaginationParams.DefaultPageSize };
        query.Normalize();
        var (safePage, safeSize) = query.Normalized();

        List<ProcessingRun> runs;
        long total;
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            var scoped = db.Set<ProcessingRun>()
                .AsNoTracking()
                .Where(r => r.ProjectId == projectGuid);
            total = await scoped.LongCountAsync(cancellationToken).ConfigureAwait(false);
            runs = await scoped
                .OrderByDescending(r => r.CreatedAt)
                .Skip((safePage - 1) * safeSize)
                .Take(safeSize)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
        }

        var items = runs.Select(r => ToResponse(projectGuid, r)).ToList();
        return Ok(ProcessingRunListResponse.Create(items, safePage, safeSize, total));
    }

    /// <summary>
    /// Gets the active processing run (404 when none). Legacy single-run view;
    /// prefer <c>GET .../processing</c> list plus <c>GET .../processing/{runId}</c>.
    /// </summary>
    [HttpGet("processing/active")]
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
    /// Cancels a specific run (202, idempotent: cancel twice → 202 both, single
    /// terminal transition). Cancel on a terminal run → 409
    /// <c>RUN_ALREADY_TERMINAL</c>. Archived projects may still cancel.
    /// </summary>
    [HttpPost("processing/{runId}/cancel")]
    [Authorize(Policy = AuthPolicies.RequireProjectOwner)]
    [ServiceFilter(typeof(IdempotencyFilter))]
    [ProducesResponseType(typeof(ProcessingRunResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CancelById(
        [FromRoute] string projectId,
        [FromRoute] string runId,
        [FromBody] CancelRequest? request,
        [FromHeader(Name = IdempotencyService.HeaderName)] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        _ = idempotencyKey;
        var tenantId = User.GetTenantId();
        var projectGuid = PublicIdParser.ParseProjectId(projectId);
        var runGuid = PublicIdParser.ParseRunId(runId);
        var correlationId = CorrelationIdMiddleware.GetCorrelationId(HttpContext);
        var reason = string.IsNullOrWhiteSpace(request?.Reason) ? "operator-requested" : request.Reason.Trim();

        var target = await FindRunByIdAsync(tenantId, projectGuid, runGuid, cancellationToken).ConfigureAwait(false);
        if (target.Status is ProcessingRunStatus.Completed or ProcessingRunStatus.Failed or ProcessingRunStatus.Cancelled)
        {
            throw new ErrorCodeException(
                ErrorCodes.RunAlreadyTerminal,
                $"Run '{runGuid}' is already {target.Status} and cannot be cancelled.");
        }

        CancellationResult result;
        try
        {
            result = await _cancellation.CancelAsync(tenantId, projectGuid, reason, cancellationToken).ConfigureAwait(false);
        }
        catch (ConflictException ex)
        {
            throw new ErrorCodeException(ErrorCodes.RunAlreadyTerminal, ex.Message);
        }

        await _audit.LogAsync(
            tenantId, projectGuid, User.GetSubject(), CancellationService.AuditAction,
            "processing-run", result.RunId.ToString("N"),
            AuditDetails(correlationId, idempotencyKey, reason),
            cancellationToken).ConfigureAwait(false);

        await PublishCancelRequestedAsync(tenantId, projectGuid, result.RunId, reason, cancellationToken).ConfigureAwait(false);

        var run = await FindRunByIdAsync(tenantId, projectGuid, result.RunId, cancellationToken).ConfigureAwait(false);
        return Accepted(ToResponse(projectGuid, run));
    }

    /// <summary>
    /// Run-level retry: creates a new run linked via <c>retryOfRunId</c> from a
    /// failed run, copying the config hash. Requires a fresh
    /// <c>Idempotency-Key</c> (missing → 400 <c>IDEMPOTENCY_KEY_REQUIRED</c>;
    /// reuse of a completed-start key for a different payload → 422
    /// <c>IDEMPOTENCY_KEY_REUSED</c>). Settings changed since the run → 409
    /// <c>CONFIG_CHANGED_SINCE_RUN</c> with the current hash. Archived → 409
    /// <c>PROJECT_ARCHIVED</c>. Example 202:
    /// <c>{ runId: "run_...", retryOfRunId: "run_...", status: "Pending" }</c>.
    /// </summary>
    [HttpPost("processing/{runId}/retry")]
    [Authorize(Policy = AuthPolicies.RequireProjectEditor)]
    [ProducesResponseType(typeof(ProcessingRunResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ProcessingRunResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> RetryById(
        [FromRoute] string projectId,
        [FromRoute] string runId,
        [FromHeader(Name = IdempotencyService.HeaderName)] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        var key = ProcessingIdempotency.RequireKey(idempotencyKey);
        var tenantId = User.GetTenantId();
        var projectGuid = PublicIdParser.ParseProjectId(projectId);
        var runGuid = PublicIdParser.ParseRunId(runId);
        var correlationId = CorrelationIdMiddleware.GetCorrelationId(HttpContext);

        var requestHash = ProcessingIdempotency.HashForRetry(projectGuid, runGuid, string.Empty);
        var claim = await _processingIdempotency.TryClaimAsync(tenantId, key, requestHash, cancellationToken).ConfigureAwait(false);
        if (claim.IsReplay && claim.RunId.HasValue)
        {
            var replayed = await FindRunByIdAsync(tenantId, projectGuid, claim.RunId.Value, cancellationToken).ConfigureAwait(false);
            Response.Headers[ProcessingIdempotency.ReplayedHeaderName] = "true";
            return Ok(ToResponse(projectGuid, replayed, runGuid));
        }

        try
        {
            var project = await RequireProjectAsync(tenantId, projectGuid, cancellationToken).ConfigureAwait(false);
            if (project.IsArchived)
            {
                throw new ErrorCodeException(ErrorCodes.ProjectArchived, $"Project '{projectGuid}' is archived and cannot be retried.");
            }

            var source = await FindRunByIdAsync(tenantId, projectGuid, runGuid, cancellationToken).ConfigureAwait(false);
            if (source.Status is ProcessingRunStatus.Pending or ProcessingRunStatus.Running or ProcessingRunStatus.Cancelling or ProcessingRunStatus.ManualReviewRequired)
            {
                throw new ErrorCodeException(
                    ErrorCodes.RunAlreadyActive,
                    $"Run '{runGuid}' is {source.Status}; only terminal runs can be retried as a new run.");
            }

            if (source.Status != ProcessingRunStatus.Failed)
            {
                throw new ErrorCodeException(
                    ErrorCodes.RunAlreadyTerminal,
                    $"Run '{runGuid}' is {source.Status}; only failed runs can be retried as a new run.");
            }

            if (!string.Equals(project.ConfigurationHash, source.ConfigurationHash, StringComparison.Ordinal))
            {
                throw new ErrorCodeException(
                    ErrorCodes.ConfigChangedSinceRun,
                    $"Project '{projectGuid}' settings changed since run '{runGuid}'. Current hash: {project.ConfigurationHash}.");
            }

            var newRunId = Guid.NewGuid();
            var created = await CreateRetryRunAsync(tenantId, project, source, newRunId, cancellationToken).ConfigureAwait(false);

            await _audit.LogAsync(
                tenantId, projectGuid, User.GetSubject(), "processing.retry-run",
                "processing-run", newRunId.ToString("N"),
                AuditDetails(correlationId, key, runGuid.ToString("N")),
                cancellationToken).ConfigureAwait(false);

            var response = ToResponse(projectGuid, created, runGuid);
            await _processingIdempotency.CompleteAsync(
                tenantId, key, StatusCodes.Status202Accepted, newRunId,
                PublicIdParser.ToProjectId(projectGuid), created.Status.ToString(), cancellationToken).ConfigureAwait(false);

            return Accepted(response);
        }
        catch
        {
            await _processingIdempotency.FailAsync(tenantId, key, cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Cancels the active run (404 when none, 409 <c>RUN_ALREADY_TERMINAL</c>
    /// when the latest run is already terminal). Durable: the run moves to
    /// <c>Cancelling</c> (new scheduling blocked, in-flight work drains),
    /// reaching <c>Cancelled</c> only after the sweeper observes zero running
    /// executions. Idempotent for 24h via <c>Idempotency-Key</c> plus
    /// service-level idempotency (cancel twice → 202 both).
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
        var correlationId = CorrelationIdMiddleware.GetCorrelationId(HttpContext);
        var reason = string.IsNullOrWhiteSpace(request?.Reason) ? "operator-requested" : request.Reason.Trim();

        CancellationResult result;
        try
        {
            result = await _cancellation.CancelAsync(tenantId, projectGuid, reason, cancellationToken).ConfigureAwait(false);
        }
        catch (ConflictException ex)
        {
            throw new ErrorCodeException(ErrorCodes.RunAlreadyTerminal, ex.Message);
        }

        await _audit.LogAsync(
            tenantId, projectGuid, User.GetSubject(), CancellationService.AuditAction,
            "processing-run", result.RunId.ToString("N"),
            AuditDetails(correlationId, idempotencyKey, reason),
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
    /// execution states; <c>percentageIndicator</c>/<c>percentApproximate</c>
    /// are display-only with <c>notEta:true</c> (never drive billing). A run
    /// with no stages yet returns <c>{ percentApproximate: 0,
    /// currentStage: null }</c> (zeroed counts), not 404.
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
    /// disconnects. Requires the project Viewer role; auth may arrive as
    /// <c>Authorization: Bearer</c> or query <c>?access_token=</c> (same
    /// policy, short-TTL single-use-scoped token, never logged). Accepts
    /// <c>Last-Event-ID</c> for resume (replay semantics frozen in Task 013;
    /// this shell only accepts the header). Cross-tenant ids return 404 (no
    /// existence leak); unauthenticated returns 401. Event serialization beyond
    /// the progress document is deferred to Task 013 — this shell emits
    /// invalidation-hint data frames the Task 026 client polls on.
    /// </summary>
    [HttpGet("progress/stream")]
    [Authorize(Policy = AuthPolicies.RequireProjectViewer)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task StreamProgress(
        [FromRoute] string projectId,
        CancellationToken cancellationToken)
    {
        _ = Request.Headers.TryGetValue("Last-Event-ID", out var _lastEventId);
        var tenantId = User.GetTenantId();
        var projectGuid = PublicIdParser.ParseProjectId(projectId);

        Response.Headers.CacheControl = "no-cache";
        Response.Headers.ContentType = "text/event-stream";
        Response.Headers["X-Accel-Buffering"] = "no";

        try
        {
            // Tenant-scoped existence check first so cross-tenant ids yield 404
            // (not 403) without leaking existence.
            await RequireProjectStreamAsync(tenantId, projectGuid, HttpContext.RequestAborted).ConfigureAwait(false);

            while (!cancellationToken.IsCancellationRequested)
            {
                ProgressSnapshot snapshot;
                try
                {
                    snapshot = await _progress.GetAsync(tenantId, projectGuid, HttpContext.RequestAborted).ConfigureAwait(false);
                }
                catch (ForbiddenException)
                {
                    throw new NotFoundException($"Project '{projectGuid}' was not found.");
                }

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

    private static ProcessingRunResponse ToResponse(Guid projectId, ProcessingRun run, Guid? retryOf = null)
    {
        return new ProcessingRunResponse(
            PublicIdParser.ToRunId(run.Id),
            PublicIdParser.ToProjectId(projectId),
            run.Status.ToString(),
            run.Attempt,
            run.CreatedAt,
            run.StartedAt,
            retryOf.HasValue ? PublicIdParser.ToRunId(retryOf.Value) : null,
            run.ConfigurationHash);
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
            snapshot.GeneratedAt,
            snapshot.PercentageIndicator);
    }

    private bool HasRetryPermission()
    {
        return User.HasAnyRole([Roles.TenantAdmin, Roles.ProjectOwner, Roles.ProjectEditor, Roles.Service]);
    }

    private static string AuditDetails(string correlationId, string? idempotencyKey, string? extra = null)
    {
        using var stream = new System.IO.MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("correlationId", correlationId);
            if (!string.IsNullOrWhiteSpace(idempotencyKey))
            {
                writer.WriteString("idempotencyKey", idempotencyKey.Trim());
            }

            if (!string.IsNullOrWhiteSpace(extra))
            {
                writer.WriteString("detail", extra.Trim());
            }

            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    private async Task<ProcessingRun> CreateRetryRunAsync(
        Guid tenantId,
        DubbingProject project,
        ProcessingRun source,
        Guid newRunId,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var maxAttempt = await MaxAttemptAsync(tenantId, project.Id, cancellationToken).ConfigureAwait(false);
        var attempt = maxAttempt + 1;

        var run = new ProcessingRun(
            newRunId, tenantId, project.Id, attempt, ProcessingRunStatus.Pending,
            ProcessingStartService.PipelineVersion,
            source.ConfigurationHash,
            source.ProviderRouteHash,
            source.ExecutionSnapshotHash,
            now, now, null, null);

        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                db.Set<ProcessingRun>().Add(run);

                var oldSnapshot = await db.Set<ProviderRouteSnapshot>()
                    .AsNoTracking()
                    .Where(s => s.ProcessingRunId == source.Id)
                    .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
                if (oldSnapshot is not null)
                {
                    db.Set<ProviderRouteSnapshot>().Add(new ProviderRouteSnapshot(
                        Guid.NewGuid(), tenantId, project.Id, newRunId,
                        oldSnapshot.RouteConfigHash, oldSnapshot.CapabilityHash, oldSnapshot.PrivacyHash, now));
                }

                foreach (var node in StageGraph.Nodes)
                {
                    var expected = node.Scope is ScopeType.Project or ScopeType.Run ? 1 : 0;
                    db.Set<RunStageSummary>().Add(new RunStageSummary(
                        Guid.NewGuid(), tenantId, newRunId, node.StageType, expected,
                        0, 0, 0, 0, 0, now, now));
                }

                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

                ProjectStateMachine.EnsureCanTransition(project.Status, ProjectStatus.Processing, isManualRetry: true);
                await db.Database.ExecuteSqlRawAsync(
                    "UPDATE dubbing_projects SET status = {0}, active_run_id = {1}, updated_at = {2} WHERE id = {3} AND tenant_id = {4}",
                    ProjectStatus.Processing.ToString(), newRunId, now, project.Id, tenantId).ConfigureAwait(false);

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
                }

                throw;
            }
        }

        return run;
    }

    private async Task<int> MaxAttemptAsync(Guid tenantId, Guid projectId, CancellationToken cancellationToken)
    {
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            var max = await db.Set<ProcessingRun>()
                .Where(r => r.ProjectId == projectId)
                .MaxAsync(r => (int?)r.Attempt, cancellationToken).ConfigureAwait(false);
            return max ?? -1;
        }
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

    private async Task RequireProjectStreamAsync(Guid tenantId, Guid projectId, CancellationToken cancellationToken)
    {
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            var exists = await db.Set<DubbingProject>()
                .AsNoTracking()
                .Where(p => p.Id == projectId && EF.Property<bool>(p, "IsDeleted") == false)
                .AnyAsync(cancellationToken).ConfigureAwait(false);
            if (!exists)
            {
                throw new NotFoundException($"Project '{projectId}' was not found.");
            }
        }
    }

    private async Task<ProcessingRun?> FindActiveRunAsync(Guid tenantId, Guid projectId, CancellationToken cancellationToken)
    {
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            return await db.Set<ProcessingRun>()
                .AsNoTracking()
                .Where(r => r.ProjectId == projectId && ActiveStatuses.Contains(r.Status))
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
