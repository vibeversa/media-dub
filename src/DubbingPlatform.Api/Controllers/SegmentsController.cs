using System.Text.Json;
using DubbingPlatform.Api.Filters;
using DubbingPlatform.Api.Middleware;
using DubbingPlatform.Api.Models;
using DubbingPlatform.Application.Authorization;
using DubbingPlatform.Application.Common;
using DubbingPlatform.Application.Exceptions;
using DubbingPlatform.Application.MultiTenancy;
using DubbingPlatform.Application.Segments;
using DubbingPlatform.Application.Services;
using DubbingPlatform.Contracts.Messages;
using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Domain.Enums;
using DubbingPlatform.Domain.Identity;
using MassTransit;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DubbingPlatform.Api.Controllers;

/// <summary>
/// Segment editing surface nested under projects:
/// <c>GET /api/v1/projects/{projectId}/segments</c> (filters + pagination
/// default 50/max 200, sort <c>startMs</c>),
/// <c>GET .../segments/{segmentId}</c> (detail with current versions +
/// selection version),
/// <c>POST .../segments/{segmentId}/retry</c> (202; active retry → 409
/// <c>SEGMENT_RETRY_ACTIVE</c> with existing execution),
/// <c>POST .../segments/{segmentId}/transcript-selection|translation-selection</c>
/// (body <c>{ versionId, expectedSelectionVersion, reason? }</c>),
/// <c>POST .../segments/{segmentId}/transcript-edits|translation-edits</c>
/// (body <c>{ text, expectedSelectionVersion, reason? }</c> → new manual
/// version + select).
/// Stale <c>expectedSelectionVersion</c> → 409 <c>SELECTION_CONFLICT</c> with
/// <c>{ currentSelectionVersion, currentVersionIds }</c>; cross-segment
/// version → 400 <c>VERSION_SEGMENT_MISMATCH</c>; missing version → 404
/// <c>VERSION_NOT_FOUND</c>; empty text → 400 <c>SEGMENT_TEXT_EMPTY</c>.
/// Successful mutations publish <c>SegmentSelectionChanged</c> (service-owned)
/// and return <c>outputStale:true</c> + <c>OUTPUT_STALE</c> when final output
/// exists (no recompute). Every mutation audits actor + reason + version delta
/// + correlationId (service-owned for selections; controller-owned for retry).
/// GETs require <c>review.view</c> (policy <c>RequireProjectViewer</c>, includes
/// Viewer); mutations require <c>project.edit</c> (policy
/// <c>RequireProjectEditor</c>); retry additionally requires
/// <c>processing.retry</c> (same Editor+ set per <c>RoleMatrix</c>).
/// Only version ids + correlationId are logged — never transcript bodies.
/// </summary>
[ApiController]
[Route("api/v1/projects/{projectId}/segments")]
[Authorize]
[ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
[ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status403Forbidden)]
public sealed class SegmentsController : ControllerBase
{
    private const int ListDefaultPageSize = 50;

    private const int ListMaxPageSize = 200;

    private static readonly JsonSerializerOptions AuditJsonOptions = new(JsonSerializerDefaults.Web);

    private static readonly StageStatus[] ActiveRetryStatuses =
    [
        StageStatus.Pending,
        StageStatus.Scheduled,
        StageStatus.Running,
        StageStatus.RetryPending,
    ];

    private readonly IStageExecutionContextFactory _contextFactory;
    private readonly SegmentSelectionService _selections;
    private readonly RetryService _retry;
    private readonly AuditService _audit;
    private readonly IPublishEndpoint _publish;
    private readonly ILogger<SegmentsController> _logger;

    public SegmentsController(
        IStageExecutionContextFactory contextFactory,
        SegmentSelectionService selections,
        RetryService retry,
        AuditService audit,
        IPublishEndpoint publish,
        ILogger<SegmentsController> logger)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        ArgumentNullException.ThrowIfNull(selections);
        ArgumentNullException.ThrowIfNull(retry);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(publish);
        ArgumentNullException.ThrowIfNull(logger);
        _contextFactory = contextFactory;
        _selections = selections;
        _retry = retry;
        _audit = audit;
        _publish = publish;
        _logger = logger;
    }

    /// <summary>
    /// Lists segments with combined AND filters: <c>speakerId</c> (raw GUID or
    /// <c>spk_</c>), <c>reviewStatus</c> (ReviewStatus name), <c>qualityFlag</c>
    /// (QualityStatus name or QualityResult code), <c>syncIssue</c>
    /// (<c>true|false</c> or SyncStatus name), <c>text</c> substring over
    /// transcript + translation texts (case-insensitive), <c>startMs/endMs</c>
    /// time window (overlap). Pagination default 50/max 200; sort
    /// <c>startMs</c> ascending. Example:
    /// <c>?speakerId=spk_...&amp;reviewStatus=Open&amp;page=1&amp;pageSize=50</c>.
    /// </summary>
    [HttpGet]
    [Authorize(Policy = AuthPolicies.RequireProjectViewer)]
    [ProducesResponseType(typeof(PaginatedResult<SegmentSummaryResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> List(
        [FromRoute] string projectId,
        [FromQuery] string? speakerId,
        [FromQuery] string? reviewStatus,
        [FromQuery] string? qualityFlag,
        [FromQuery] string? syncIssue,
        [FromQuery] string? text,
        [FromQuery] int? startMs,
        [FromQuery] int? endMs,
        [FromQuery] int? page,
        [FromQuery] int? pageSize,
        CancellationToken cancellationToken)
    {
        var tenantId = User.GetTenantId();
        var projectGuid = PublicIdParser.ParseProjectId(projectId);
        await RequireProjectAsync(tenantId, projectGuid, cancellationToken).ConfigureAwait(false);

        var speakerGuid = ParseOptionalSpeaker(speakerId);
        var reviewParsed = ParseOptionalReviewStatus(reviewStatus);
        var qualityParsed = ParseOptionalQuality(qualityFlag);
        var syncParsed = ParseOptionalSync(syncIssue);
        var textTerm = string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        ValidateWindow(startMs, endMs);

        var (safePage, safeSize) = NormalizeSegmentPaging(page, pageSize);

        List<SpeechSegment> items;
        long total;
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            var scoped = ApplyFilters(db, projectGuid, speakerGuid, reviewParsed, qualityParsed, syncParsed, textTerm, startMs, endMs);
            total = await scoped.LongCountAsync(cancellationToken).ConfigureAwait(false);
            items = await scoped
                .OrderBy(s => s.StartMs)
                .ThenBy(s => s.Sequence)
                .Skip((safePage - 1) * safeSize)
                .Take(safeSize)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
        }

        var summaries = await ToSummariesAsync(tenantId, items, cancellationToken).ConfigureAwait(false);
        return Ok(PaginatedResult<SegmentSummaryResponse>.Create(summaries, safePage, safeSize, total));
    }

    /// <summary>
    /// Gets segment detail with current transcript/translation versions +
    /// selection version. Cross-tenant segment ids return 404 (no leak).
    /// </summary>
    [HttpGet("{segmentId}")]
    [Authorize(Policy = AuthPolicies.RequireProjectViewer)]
    [ProducesResponseType(typeof(SegmentDetailResponse), StatusCodes.Status200OK)]
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
        var detail = await ToDetailAsync(tenantId, segment, cancellationToken).ConfigureAwait(false);
        return Ok(detail);
    }

    /// <summary>
    /// Requeues ASR/translation for one segment (202). Resolves the stage
    /// server-side from the segment's latest failed/review unit (409 when none).
    /// Active retry (Pending/Scheduled/Running/RetryPending execution) → 409
    /// <c>SEGMENT_RETRY_ACTIVE</c> with the existing execution (idempotent poll
    /// reuses it). Requires <c>project.edit</c> + <c>processing.retry</c>
    /// (Editor+). Idempotent for 24h via <c>Idempotency-Key</c>.
    /// </summary>
    [HttpPost("{segmentId}/retry")]
    [Authorize(Policy = AuthPolicies.RequireProjectEditor)]
    [ServiceFilter(typeof(IdempotencyFilter))]
    [ProducesResponseType(typeof(SegmentSummaryResponse), StatusCodes.Status202Accepted)]
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
        var correlationId = CorrelationIdMiddleware.GetCorrelationId(HttpContext);
        await RequireProjectAsync(tenantId, projectGuid, cancellationToken).ConfigureAwait(false);

        var segment = await LoadOwnedSegmentAsync(tenantId, projectGuid, segmentGuid, cancellationToken).ConfigureAwait(false);
        await ThrowIfRetryActiveAsync(tenantId, segment, cancellationToken).ConfigureAwait(false);

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
                correlationId,
            }, AuditJsonOptions),
            cancellationToken).ConfigureAwait(false);

        await PublishRetryAsync(tenantId, projectGuid, descriptor, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Segment retry. {TenantId} {ProjectId} {SegmentId} {Stage} {Attempt} {CorrelationId}",
            tenantId, projectGuid, segment.Id, descriptor.Stage, descriptor.Attempt, correlationId);

        var summaries = await ToSummariesAsync(tenantId, [segment], cancellationToken).ConfigureAwait(false);
        return Accepted(summaries[0]);
    }

    /// <summary>
    /// Selects a transcript version. Body
    /// <c>{ versionId, expectedSelectionVersion, reason? }</c>. Stale version →
    /// 409 <c>SELECTION_CONFLICT</c> with refresh payload; cross-segment → 400
    /// <c>VERSION_SEGMENT_MISMATCH</c>; missing → 404 <c>VERSION_NOT_FOUND</c>.
    /// Success returns <c>outputStale</c> + <c>OUTPUT_STALE</c> when final
    /// output exists. Example: <c>{ versionId: "...", expectedSelectionVersion:
    /// 1, reason: "prefer v2" }</c>.
    /// </summary>
    [HttpPost("{segmentId}/transcript-selection")]
    [Authorize(Policy = AuthPolicies.RequireProjectEditor)]
    [ProducesResponseType(typeof(SegmentMutationResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> SelectTranscript(
        [FromRoute] string projectId,
        [FromRoute] string segmentId,
        [FromBody] SelectVersionRequest? request,
        CancellationToken cancellationToken)
    {
        var tenantId = User.GetTenantId();
        var projectGuid = PublicIdParser.ParseProjectId(projectId);
        var segmentGuid = PublicIdParser.ParseSegmentId(segmentId);
        await RequireProjectAsync(tenantId, projectGuid, cancellationToken).ConfigureAwait(false);
        await LoadOwnedSegmentAsync(tenantId, projectGuid, segmentGuid, cancellationToken).ConfigureAwait(false);

        if (request is null)
        {
            throw new Domain.Exceptions.DomainException("Selection requires { versionId, expectedSelectionVersion, reason? }.");
        }

        var versionGuid = ParseVersionId(request.VersionId);
        var actor = RequireActorGuid();
        var correlationId = CorrelationIdMiddleware.GetCorrelationId(HttpContext);

        var result = await ExecuteSelectionAsync(
            (actorId, ct) => _selections.SelectTranscriptAsync(
                tenantId, projectGuid, segmentGuid, versionGuid,
                request.ExpectedSelectionVersion, actorId, request.Reason, ct, correlationId),
            tenantId, projectGuid, segmentGuid, actor, correlationId, cancellationToken).ConfigureAwait(false);
        return Ok(result);
    }

    /// <summary>
    /// Selects a translation version (same contract as transcript-selection).
    /// </summary>
    [HttpPost("{segmentId}/translation-selection")]
    [Authorize(Policy = AuthPolicies.RequireProjectEditor)]
    [ProducesResponseType(typeof(SegmentMutationResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> SelectTranslation(
        [FromRoute] string projectId,
        [FromRoute] string segmentId,
        [FromBody] SelectVersionRequest? request,
        CancellationToken cancellationToken)
    {
        var tenantId = User.GetTenantId();
        var projectGuid = PublicIdParser.ParseProjectId(projectId);
        var segmentGuid = PublicIdParser.ParseSegmentId(segmentId);
        await RequireProjectAsync(tenantId, projectGuid, cancellationToken).ConfigureAwait(false);
        await LoadOwnedSegmentAsync(tenantId, projectGuid, segmentGuid, cancellationToken).ConfigureAwait(false);

        if (request is null)
        {
            throw new Domain.Exceptions.DomainException("Selection requires { versionId, expectedSelectionVersion, reason? }.");
        }

        var versionGuid = ParseVersionId(request.VersionId);
        var actor = RequireActorGuid();
        var correlationId = CorrelationIdMiddleware.GetCorrelationId(HttpContext);

        var result = await ExecuteSelectionAsync(
            (actorId, ct) => _selections.SelectTranslationAsync(
                tenantId, projectGuid, segmentGuid, versionGuid,
                request.ExpectedSelectionVersion, actorId, request.Reason, ct, correlationId),
            tenantId, projectGuid, segmentGuid, actor, correlationId, cancellationToken).ConfigureAwait(false);
        return Ok(result);
    }

    /// <summary>
    /// Creates a manual transcript version (plain text, HTML stripped) and
    /// selects it. Body <c>{ text, expectedSelectionVersion, reason? }</c>
    /// (1..5000 chars; empty → 400 <c>SEGMENT_TEXT_EMPTY</c>). Old rows stay
    /// immutable. Example: <c>{ text: "corrected line",
    /// expectedSelectionVersion: 1 }</c>.
    /// </summary>
    [HttpPost("{segmentId}/transcript-edits")]
    [Authorize(Policy = AuthPolicies.RequireProjectEditor)]
    [ProducesResponseType(typeof(SegmentMutationResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> EditTranscript(
        [FromRoute] string projectId,
        [FromRoute] string segmentId,
        [FromBody] EditTextRequest? request,
        CancellationToken cancellationToken)
    {
        var tenantId = User.GetTenantId();
        var projectGuid = PublicIdParser.ParseProjectId(projectId);
        var segmentGuid = PublicIdParser.ParseSegmentId(segmentId);
        await RequireProjectAsync(tenantId, projectGuid, cancellationToken).ConfigureAwait(false);
        await LoadOwnedSegmentAsync(tenantId, projectGuid, segmentGuid, cancellationToken).ConfigureAwait(false);

        if (request is null)
        {
            throw new SegmentTextEmptyApiException("Manual edit requires non-empty text.");
        }

        RequireEditText(request.Text);
        var actor = RequireActorGuid();
        var correlationId = CorrelationIdMiddleware.GetCorrelationId(HttpContext);

        var result = await ExecuteSelectionAsync(
            (actorId, ct) => _selections.CreateManualTranscriptVersionAsync(
                tenantId, projectGuid, segmentGuid,
                request.ExpectedSelectionVersion, request.Text!, actorId, request.Reason, ct, correlationId),
            tenantId, projectGuid, segmentGuid, actor, correlationId, cancellationToken).ConfigureAwait(false);
        return Ok(result);
    }

    /// <summary>
    /// Creates a manual translation version and selects it (same contract as
    /// transcript-edits).
    /// </summary>
    [HttpPost("{segmentId}/translation-edits")]
    [Authorize(Policy = AuthPolicies.RequireProjectEditor)]
    [ProducesResponseType(typeof(SegmentMutationResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> EditTranslation(
        [FromRoute] string projectId,
        [FromRoute] string segmentId,
        [FromBody] EditTextRequest? request,
        CancellationToken cancellationToken)
    {
        var tenantId = User.GetTenantId();
        var projectGuid = PublicIdParser.ParseProjectId(projectId);
        var segmentGuid = PublicIdParser.ParseSegmentId(segmentId);
        await RequireProjectAsync(tenantId, projectGuid, cancellationToken).ConfigureAwait(false);
        await LoadOwnedSegmentAsync(tenantId, projectGuid, segmentGuid, cancellationToken).ConfigureAwait(false);

        if (request is null)
        {
            throw new SegmentTextEmptyApiException("Manual edit requires non-empty text.");
        }

        RequireEditText(request.Text);
        var actor = RequireActorGuid();
        var correlationId = CorrelationIdMiddleware.GetCorrelationId(HttpContext);

        var result = await ExecuteSelectionAsync(
            (actorId, ct) => _selections.CreateManualTranslationVersionAsync(
                tenantId, projectGuid, segmentGuid,
                request.ExpectedSelectionVersion, request.Text!, actorId, request.Reason, ct, correlationId),
            tenantId, projectGuid, segmentGuid, actor, correlationId, cancellationToken).ConfigureAwait(false);
        return Ok(result);
    }

    private async Task<SegmentMutationResponse> ExecuteSelectionAsync(
        Func<Guid, CancellationToken, Task<SegmentSelectionResult>> mutate,
        Guid tenantId,
        Guid projectId,
        Guid segmentId,
        Guid actor,
        string correlationId,
        CancellationToken cancellationToken)
    {
        SegmentSelectionResult result;
        try
        {
            result = await mutate(actor, cancellationToken).ConfigureAwait(false);
        }
        catch (SegmentSelectionConflictException ex)
        {
            throw new SelectionConflictApiException(
                ex.CurrentSelectionVersion,
                ex.CurrentTranscriptVersionId,
                ex.CurrentTranslationVersionId);
        }
        catch (NotFoundException ex) when (ex.Message.Contains(
            SegmentSelectionService.VersionNotFoundMarker, StringComparison.Ordinal))
        {
            throw new VersionNotFoundApiException(ex.Message);
        }
        catch (Domain.Exceptions.DomainException ex) when (ex.Message.Contains(
            SegmentSelectionService.VersionSegmentMismatchMarker, StringComparison.Ordinal))
        {
            throw new VersionSegmentMismatchApiException(ex.Message);
        }
        catch (Domain.Exceptions.DomainException ex) when (IsEmptyTextError(ex))
        {
            throw new SegmentTextEmptyApiException(ex.Message);
        }

        _logger.LogInformation(
            "Segment selection changed. {TenantId} {ProjectId} {SegmentId} {SelectionVersion} {CorrelationId}",
            tenantId, projectId, segmentId, result.SelectionVersion, correlationId);

        return new SegmentMutationResponse(
            PublicIdParser.ToSegmentId(result.SegmentId),
            result.SelectionVersion,
            result.SelectedTranscriptVersionId.HasValue
                ? result.SelectedTranscriptVersionId.Value.ToString("D") : null,
            result.SelectedTranslationVersionId.HasValue
                ? result.SelectedTranslationVersionId.Value.ToString("D") : null,
            result.NewVersionId.HasValue
                ? result.NewVersionId.Value.ToString("D") : null,
            result.OutputStale,
            result.WarningCode);
    }

    private static bool IsEmptyTextError(Domain.Exceptions.DomainException ex)
    {
        return ex.Message.Contains("non-empty text", StringComparison.OrdinalIgnoreCase);
    }

    private static void RequireEditText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new SegmentTextEmptyApiException("Manual edit requires non-empty text.");
        }

        if (text.Trim().Length > SegmentSelectionService.MaxManualTextLength)
        {
            throw new Domain.Exceptions.DomainException(string.Concat(
                "Manual edit text must not exceed ",
                SegmentSelectionService.MaxManualTextLength.ToString(System.Globalization.CultureInfo.InvariantCulture),
                " characters."));
        }
    }

    private static Guid ParseVersionId(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new Domain.Exceptions.DomainException("VersionId must not be empty.");
        }

        var trimmed = raw.Trim();
        if (Guid.TryParse(trimmed, out var guid) && guid != Guid.Empty)
        {
            return guid;
        }

        var compact = trimmed.Replace("-", string.Empty, StringComparison.Ordinal);
        if (Guid.TryParseExact(compact, "N", out var compactGuid) && compactGuid != Guid.Empty)
        {
            return compactGuid;
        }

        throw new Domain.Exceptions.DomainException($"Version id '{trimmed}' is not a valid identifier.");
    }

    private Guid RequireActorGuid()
    {
        var sub = User.GetSubject();
        if (Guid.TryParse(sub.Trim(), out var actor) && actor != Guid.Empty)
        {
            return actor;
        }

        throw new UnauthorizedAccessException("The 'sub' claim must be a user id.");
    }

    private async Task ThrowIfRetryActiveAsync(
        Guid tenantId,
        SpeechSegment segment,
        CancellationToken cancellationToken)
    {
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            var active = await db.Set<StageExecution>()
                .AsNoTracking()
                .Where(e => e.ProcessingRunId == segment.RunId
                    && e.SegmentId == segment.Id
                    && ActiveRetryStatuses.Contains(e.Status))
                .OrderByDescending(e => e.CreatedAt)
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (active is not null)
            {
                throw new SegmentRetryActiveApiException(active.Id, active.StageType.ToString(), active.Attempt);
            }
        }
    }

    private static Guid? ParseOptionalSpeaker(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        return PublicIdParser.ParseSpeakerId(raw);
    }

    private static ReviewStatus? ParseOptionalReviewStatus(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        if (Enum.TryParse<ReviewStatus>(raw.Trim(), ignoreCase: true, out var parsed)
            && string.Equals(parsed.ToString(), raw.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return parsed;
        }

        throw new Domain.Exceptions.DomainException($"Unknown reviewStatus '{raw}'.");
    }

    private sealed record QualityFilter(QualityStatus? Status, string? Code);

    private static QualityFilter? ParseOptionalQuality(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var trimmed = raw.Trim();
        if (Enum.TryParse<QualityStatus>(trimmed, ignoreCase: true, out var status)
            && string.Equals(status.ToString(), trimmed, StringComparison.OrdinalIgnoreCase))
        {
            return new QualityFilter(status, null);
        }

        return new QualityFilter(null, trimmed);
    }

    private sealed record SyncFilter(bool? HasIssue, SyncStatus? Status);

    private static SyncFilter? ParseOptionalSync(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var trimmed = raw.Trim();
        if (bool.TryParse(trimmed, out var flag))
        {
            return new SyncFilter(flag, null);
        }

        if (Enum.TryParse<SyncStatus>(trimmed, ignoreCase: true, out var status)
            && string.Equals(status.ToString(), trimmed, StringComparison.OrdinalIgnoreCase))
        {
            return new SyncFilter(null, status);
        }

        throw new Domain.Exceptions.DomainException($"Unknown syncIssue '{raw}'. Use true|false or a SyncStatus name.");
    }

    private static void ValidateWindow(int? startMs, int? endMs)
    {
        if (startMs.HasValue && startMs.Value < 0)
        {
            throw new Domain.Exceptions.DomainException("startMs must be >= 0.");
        }

        if (endMs.HasValue && endMs.Value <= 0)
        {
            throw new Domain.Exceptions.DomainException("endMs must be > 0.");
        }

        if (startMs.HasValue && endMs.HasValue && endMs.Value <= startMs.Value)
        {
            throw new Domain.Exceptions.DomainException("endMs must be greater than startMs.");
        }
    }

    private static (int Page, int PageSize) NormalizeSegmentPaging(int? page, int? pageSize)
    {
        var safePage = page ?? 1;
        if (safePage < 1)
        {
            safePage = 1;
        }

        var safeSize = pageSize ?? ListDefaultPageSize;
        if (safeSize < 1)
        {
            safeSize = ListDefaultPageSize;
        }

        if (safeSize > ListMaxPageSize)
        {
            safeSize = ListMaxPageSize;
        }

        return (safePage, safeSize);
    }

    private static IQueryable<SpeechSegment> ApplyFilters(
        Microsoft.EntityFrameworkCore.DbContext db,
        Guid projectGuid,
        Guid? speakerGuid,
        ReviewStatus? reviewStatus,
        QualityFilter? quality,
        SyncFilter? sync,
        string? textTerm,
        int? startMs,
        int? endMs)
    {
        var scoped = db.Set<SpeechSegment>().AsNoTracking()
            .Where(s => s.ProjectId == projectGuid);

        if (speakerGuid.HasValue)
        {
            var speaker = speakerGuid.Value;
            scoped = scoped.Where(s => s.SpeakerId == speaker);
        }

        if (reviewStatus.HasValue)
        {
            var status = reviewStatus.Value;
            scoped = scoped.Where(s => db.Set<ReviewItem>()
                .Any(r => r.SegmentId == s.Id && r.Status == status));
        }

        if (quality is not null)
        {
            if (quality.Status.HasValue)
            {
                var status = quality.Status.Value;
                scoped = scoped.Where(s => db.Set<QualityResult>()
                    .Any(q => q.SegmentId == s.Id && q.Status == status));
            }
            else
            {
                var code = quality.Code!;
                var lowered = code.ToLowerInvariant();
                scoped = scoped.Where(s => db.Set<QualityResult>()
                    .Any(q => q.SegmentId == s.Id && q.Code.ToLower() == lowered));
            }
        }

        if (sync is not null)
        {
            if (sync.Status.HasValue)
            {
                var status = sync.Status.Value;
                scoped = scoped.Where(s => db.Set<SyncResult>()
                    .Any(y => y.SegmentId == s.Id && y.Status == status));
            }
            else if (sync.HasIssue.HasValue && sync.HasIssue.Value)
            {
                scoped = scoped.Where(s => db.Set<SyncResult>()
                    .Any(y => y.SegmentId == s.Id && y.Status != SyncStatus.SyncAcceptable));
            }
            else
            {
                scoped = scoped.Where(s => !db.Set<SyncResult>()
                    .Any(y => y.SegmentId == s.Id && y.Status != SyncStatus.SyncAcceptable));
            }
        }

        if (!string.IsNullOrWhiteSpace(textTerm))
        {
            var pattern = string.Concat("%", EscapeLike(textTerm!), "%");
            scoped = scoped.Where(s =>
                db.Set<TranscriptVersion>().Any(v => v.SegmentId == s.Id && EF.Functions.ILike(v.Text, pattern))
                || db.Set<TranslationVersion>().Any(v => v.SegmentId == s.Id && EF.Functions.ILike(v.PrimaryText, pattern)));
        }

        if (startMs.HasValue && endMs.HasValue)
        {
            var start = startMs.Value;
            var end = endMs.Value;
            scoped = scoped.Where(s => s.StartMs < end && s.EndMs > start);
        }
        else if (startMs.HasValue)
        {
            var start = startMs.Value;
            scoped = scoped.Where(s => s.EndMs > start);
        }
        else if (endMs.HasValue)
        {
            var end = endMs.Value;
            scoped = scoped.Where(s => s.StartMs < end);
        }

        return scoped;
    }

    private static string EscapeLike(string term)
    {
        return term.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);
    }

    private async Task<List<SegmentSummaryResponse>> ToSummariesAsync(
        Guid tenantId,
        IReadOnlyList<SpeechSegment> segments,
        CancellationToken cancellationToken)
    {
        if (segments.Count == 0)
        {
            return [];
        }

        var ids = segments.Select(s => s.Id).ToList();
        Dictionary<Guid, SegmentSelection> selections;
        Dictionary<Guid, ReviewStatus> reviews;
        Dictionary<Guid, List<string>> qualities;
        Dictionary<Guid, SyncStatus> syncs;

        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();

            selections = await db.Set<SegmentSelection>()
                .AsNoTracking()
                .Where(x => ids.Contains(x.SegmentId))
                .ToDictionaryAsync(x => x.SegmentId, x => x, cancellationToken).ConfigureAwait(false);

            var reviewRows = await db.Set<ReviewItem>()
                .AsNoTracking()
                .Where(r => r.SegmentId.HasValue && ids.Contains(r.SegmentId!.Value))
                .OrderByDescending(r => r.CreatedAt)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            reviews = new Dictionary<Guid, ReviewStatus>();
            foreach (var row in reviewRows)
            {
                if (row.SegmentId.HasValue && !reviews.ContainsKey(row.SegmentId.Value))
                {
                    reviews[row.SegmentId.Value] = row.Status;
                }
            }

            var qualityRows = await db.Set<QualityResult>()
                .AsNoTracking()
                .Where(q => q.SegmentId.HasValue && ids.Contains(q.SegmentId!.Value))
                .Select(q => new { SegmentId = q.SegmentId!.Value, q.Code })
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            qualities = new Dictionary<Guid, List<string>>();
            foreach (var row in qualityRows)
            {
                if (!qualities.TryGetValue(row.SegmentId, out var list))
                {
                    list = [];
                    qualities[row.SegmentId] = list;
                }

                if (!list.Contains(row.Code, StringComparer.Ordinal))
                {
                    list.Add(row.Code);
                }
            }

            var syncRows = await db.Set<SyncResult>()
                .AsNoTracking()
                .Where(y => ids.Contains(y.SegmentId))
                .OrderByDescending(y => y.CreatedAt)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            syncs = new Dictionary<Guid, SyncStatus>();
            foreach (var row in syncRows)
            {
                if (!syncs.ContainsKey(row.SegmentId))
                {
                    syncs[row.SegmentId] = row.Status;
                }
            }
        }

        var result = new List<SegmentSummaryResponse>(segments.Count);
        foreach (var segment in segments)
        {
            var selectionVersion = selections.TryGetValue(segment.Id, out var selection)
                ? selection.SelectionVersion : 0;
            var review = reviews.TryGetValue(segment.Id, out var reviewStatus)
                ? reviewStatus.ToString() : null;
            var codes = qualities.TryGetValue(segment.Id, out var list)
                ? (IReadOnlyList<string>)list.OrderBy(c => c, StringComparer.Ordinal).ToList()
                : Array.Empty<string>();
            var sync = syncs.TryGetValue(segment.Id, out var syncStatus)
                ? syncStatus.ToString() : null;

            result.Add(new SegmentSummaryResponse(
                PublicIdParser.ToSegmentId(segment.Id),
                PublicIdParser.ToProjectId(segment.ProjectId),
                segment.Status,
                segment.Sequence,
                segment.StartMs,
                segment.EndMs,
                segment.SpeakerId.HasValue ? PublicIdParser.ToSpeakerId(segment.SpeakerId.Value) : null,
                selectionVersion,
                review,
                codes,
                sync));
        }

        return result;
    }

    private async Task<SegmentDetailResponse> ToDetailAsync(
        Guid tenantId,
        SpeechSegment segment,
        CancellationToken cancellationToken)
    {
        List<TranscriptVersion> transcripts;
        List<TranslationVersion> translations;
        SegmentSelection? selection;
        ReviewStatus? reviewStatus;
        List<string> qualityCodes;
        SyncStatus? syncStatus;
        bool outputStale;

        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();

            transcripts = await db.Set<TranscriptVersion>()
                .AsNoTracking()
                .Where(v => v.SegmentId == segment.Id)
                .OrderBy(v => v.CreatedAt)
                .ToListAsync(cancellationToken).ConfigureAwait(false);

            translations = await db.Set<TranslationVersion>()
                .AsNoTracking()
                .Where(v => v.SegmentId == segment.Id)
                .OrderBy(v => v.CreatedAt)
                .ToListAsync(cancellationToken).ConfigureAwait(false);

            selection = await db.Set<SegmentSelection>()
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.SegmentId == segment.Id, cancellationToken).ConfigureAwait(false);

            var review = await db.Set<ReviewItem>()
                .AsNoTracking()
                .Where(r => r.SegmentId == segment.Id)
                .OrderByDescending(r => r.CreatedAt)
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            reviewStatus = review?.Status;

            qualityCodes = await db.Set<QualityResult>()
                .AsNoTracking()
                .Where(q => q.SegmentId == segment.Id)
                .Select(q => q.Code)
                .Distinct()
                .OrderBy(c => c)
                .ToListAsync(cancellationToken).ConfigureAwait(false);

            var sync = await db.Set<SyncResult>()
                .AsNoTracking()
                .Where(y => y.SegmentId == segment.Id)
                .OrderByDescending(y => y.CreatedAt)
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            syncStatus = sync?.Status;

            outputStale = await db.Set<OutputAsset>()
                .AsNoTracking()
                .AnyAsync(o => o.ProcessingRunId == segment.RunId, cancellationToken).ConfigureAwait(false);
        }

        return new SegmentDetailResponse(
            PublicIdParser.ToSegmentId(segment.Id),
            PublicIdParser.ToProjectId(segment.ProjectId),
            segment.Status,
            segment.Sequence,
            segment.StartMs,
            segment.EndMs,
            segment.SpeakerId.HasValue ? PublicIdParser.ToSpeakerId(segment.SpeakerId.Value) : null,
            selection?.SelectionVersion ?? 0,
            selection?.SelectedTranscriptVersionId.HasValue == true
                ? selection.SelectedTranscriptVersionId.Value.ToString("D") : null,
            selection?.SelectedTranslationVersionId.HasValue == true
                ? selection.SelectedTranslationVersionId.Value.ToString("D") : null,
            transcripts.Select(v => new SegmentTranscriptVersionResponse(
                v.Id.ToString("D"), v.Provider, v.Model, v.Text, v.IsSelected, v.CreatedAt)).ToList(),
            translations.Select(v => new SegmentTranslationVersionResponse(
                v.Id.ToString("D"), v.PrimaryText, v.Provider, v.Model, v.IsSelected, v.CreatedAt)).ToList(),
            reviewStatus?.ToString(),
            qualityCodes,
            syncStatus?.ToString(),
            outputStale);
    }

    private async Task<SpeechSegment> LoadOwnedSegmentAsync(
        Guid tenantId,
        Guid projectGuid,
        Guid segmentGuid,
        CancellationToken cancellationToken)
    {
        SpeechSegment? segment;
        using (TenantContext.BeginMaintenanceScope())
        {
            using var db = _contextFactory.CreateDbContext();
            segment = await db.Set<SpeechSegment>()
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.Id == segmentGuid, cancellationToken).ConfigureAwait(false);
        }

        if (segment is null)
        {
            throw new NotFoundException($"Segment '{segmentGuid}' was not found.");
        }

        if (segment.TenantId != tenantId || segment.ProjectId != projectGuid)
        {
            throw new NotFoundException($"Segment '{segmentGuid}' was not found.");
        }

        return segment;
    }

    private async Task<StageType?> FindRetryStageAsync(
        Guid tenantId,
        SpeechSegment segment,
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
