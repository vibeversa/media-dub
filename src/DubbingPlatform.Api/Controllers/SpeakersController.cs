using System.Text.Json;
using DubbingPlatform.Api.Middleware;
using DubbingPlatform.Api.Models;
using DubbingPlatform.Application.Authorization;
using DubbingPlatform.Application.Common;
using DubbingPlatform.Application.Exceptions;
using DubbingPlatform.Application.MultiTenancy;
using DubbingPlatform.Application.Options;
using DubbingPlatform.Application.Previews;
using DubbingPlatform.Application.Services;
using DubbingPlatform.Application.Storage;
using DubbingPlatform.Application.Voices;
using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DubbingPlatform.Api.Controllers;

/// <summary>
/// Speaker, voice-assignment, and voice-preview surface nested under projects:
/// <c>GET /api/v1/projects/{projectId}/speakers</c> (list with segment counts
/// + assigned voice),
/// <c>GET .../speakers/{speakerId}</c> (detail),
/// <c>GET .../speakers/{speakerId}/available-voices</c> (compatible-only with
/// <c>excludedCount</c> + reasons; excluded voices never selectable),
/// <c>PUT .../speakers/{speakerId}/voice-assignment</c> (body
/// <c>{ voiceId, reason? }</c>; incompatible → 422
/// <c>VOICE_INCOMPATIBLE</c>, cloning without consent → 403
/// <c>VOICE_CONSENT_REQUIRED</c>, same voice → 200 <c>changed:false</c>),
/// <c>POST .../voice-previews</c> (body
/// <c>{ speakerId, voiceId, text? }</c> + <c>Idempotency-Key</c> → 202 first,
/// 200 duplicate, quota → 429 <c>PREVIEW_QUOTA_EXCEEDED</c>),
/// <c>GET .../voice-previews</c> (paginated list),
/// <c>GET .../voice-previews/{previewId}</c> (status + 15-minute presigned URL
/// when Completed; never an internal path).
/// Compatibility (<see cref="VoiceCompatibility"/>) and consent are enforced
/// server-side regardless of client filtering. GETs require
/// <c>project.view</c> (policy <c>RequireProjectViewer</c>); assignment and
/// preview creation require <c>project.edit</c> (policy
/// <c>RequireProjectEditor</c>). Cross-tenant speaker/voice/preview ids return
/// 404 (no leak); project mismatch stays 403. Only ids + correlationId are
/// logged — never audio, keys, or subject identity.
/// </summary>
[ApiController]
[Route("api/v1/projects/{projectId}")]
[Authorize]
[ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
[ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status403Forbidden)]
public sealed class SpeakersController : ControllerBase
{
    private const string DefaultPreviewText = "Hello, this is a voice preview.";

    private static readonly JsonSerializerOptions AuditJsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IStageExecutionContextFactory _contextFactory;
    private readonly SpeakerVoiceService _voices;
    private readonly VoicePreviewService _previews;
    private readonly ArtifactService _artifacts;
    private readonly AuditService _audit;
    private readonly VoiceOptions _voiceOptions;
    private readonly ILogger<SpeakersController> _logger;

    public SpeakersController(
        IStageExecutionContextFactory contextFactory,
        SpeakerVoiceService voices,
        VoicePreviewService previews,
        ArtifactService artifacts,
        AuditService audit,
        IOptions<VoiceOptions> voiceOptions,
        ILogger<SpeakersController> logger)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        ArgumentNullException.ThrowIfNull(voices);
        ArgumentNullException.ThrowIfNull(previews);
        ArgumentNullException.ThrowIfNull(artifacts);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(voiceOptions);
        ArgumentNullException.ThrowIfNull(logger);
        _contextFactory = contextFactory;
        _voices = voices;
        _previews = previews;
        _artifacts = artifacts;
        _audit = audit;
        _voiceOptions = voiceOptions.Value;
        _logger = logger;
    }

    /// <summary>
    /// Lists speakers with segment counts and the stable assigned voice
    /// (null when unassigned). Ordered by <c>SpeakerKey</c>.
    /// </summary>
    [HttpGet("speakers")]
    [Authorize(Policy = AuthPolicies.RequireProjectViewer)]
    [ProducesResponseType(typeof(PaginatedResult<SpeakerSummaryResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ListSpeakers(
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

        List<Speaker> speakers;
        long total;
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            var scoped = db.Set<Speaker>().AsNoTracking().Where(s => s.ProjectId == projectGuid);
            total = await scoped.LongCountAsync(cancellationToken).ConfigureAwait(false);
            speakers = await scoped
                .OrderBy(s => s.SpeakerKey)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
        }

        var items = await ToSummariesAsync(tenantId, projectGuid, speakers, cancellationToken).ConfigureAwait(false);
        return Ok(PaginatedResult<SpeakerSummaryResponse>.Create(items, page, pageSize, total));
    }

    /// <summary>
    /// Gets one speaker with mapping metadata + assigned voice.
    /// Cross-tenant ids return 404.
    /// </summary>
    [HttpGet("speakers/{speakerId}")]
    [Authorize(Policy = AuthPolicies.RequireProjectViewer)]
    [ProducesResponseType(typeof(SpeakerDetailResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetSpeaker(
        [FromRoute] string projectId,
        [FromRoute] string speakerId,
        CancellationToken cancellationToken)
    {
        var tenantId = User.GetTenantId();
        var projectGuid = PublicIdParser.ParseProjectId(projectId);
        var speakerGuid = PublicIdParser.ParseSpeakerId(speakerId);
        await RequireProjectAsync(tenantId, projectGuid, cancellationToken).ConfigureAwait(false);

        var speaker = await LoadOwnedSpeakerAsync(tenantId, projectGuid, speakerGuid, cancellationToken).ConfigureAwait(false);
        var summaries = await ToSummariesAsync(tenantId, projectGuid, [speaker], cancellationToken).ConfigureAwait(false);
        var summary = summaries[0];

        return Ok(new SpeakerDetailResponse(
            summary.Id, summary.ProjectId, speaker.SpeakerKey, speaker.DisplayName,
            speaker.FirstAppearanceMs, speaker.LastAppearanceMs, speaker.Confidence,
            summary.SegmentCount, summary.AssignedVoice));
    }

    /// <summary>
    /// Lists compatible-only voices for a speaker with exclusion reasons.
    /// Incompatible voices appear in <c>excluded</c> (never selectable);
    /// direct assignment of an excluded voice returns 422
    /// <c>VOICE_INCOMPATIBLE</c>. Example:
    /// <c>{ voices: [{ voiceProfileId, voiceId, ... }], excludedCount: 1,
    /// excluded: [{ voiceId, reasons }] }</c>.
    /// </summary>
    [HttpGet("speakers/{speakerId}/available-voices")]
    [Authorize(Policy = AuthPolicies.RequireProjectViewer)]
    [ProducesResponseType(typeof(AvailableVoicesResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> AvailableVoices(
        [FromRoute] string projectId,
        [FromRoute] string speakerId,
        CancellationToken cancellationToken)
    {
        var tenantId = User.GetTenantId();
        var projectGuid = PublicIdParser.ParseProjectId(projectId);
        var speakerGuid = PublicIdParser.ParseSpeakerId(speakerId);
        await RequireProjectAsync(tenantId, projectGuid, cancellationToken).ConfigureAwait(false);
        await LoadOwnedSpeakerAsync(tenantId, projectGuid, speakerGuid, cancellationToken).ConfigureAwait(false);

        DubbingProject project;
        List<VoiceProfile> profiles;
        using (TenantContext.BeginMaintenanceScope())
        {
            using var db = _contextFactory.CreateDbContext();
            project = (await db.Set<DubbingProject>()
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == projectGuid, cancellationToken)
                .ConfigureAwait(false))!;
        }

        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            profiles = await db.Set<VoiceProfile>()
                .AsNoTracking()
                .OrderBy(v => v.VoiceId)
                .ThenBy(v => v.Provider)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        var compatible = new List<AvailableVoiceResponse>();
        var excluded = new List<ExcludedVoiceResponse>();
        foreach (var voice in profiles)
        {
            var needsConsent = VoiceCompatibility.RequiresConsent(voice);
            var hasConsent = !needsConsent
                || (_voiceOptions.CloningEnabled
                    && await _voices.HasCoveringConsentAsync(tenantId, projectGuid, voice.Id, cancellationToken).ConfigureAwait(false));
            var reasons = VoiceCompatibility.Check(voice, project, hasConsent, _voiceOptions.CloningEnabled);
            if (reasons.Count == 0)
            {
                compatible.Add(new AvailableVoiceResponse(
                    PublicIdParser.ToVoiceId(voice.Id), voice.VoiceId,
                    voice.Provider, voice.Language, voice.Type.ToString(), voice.CloningEnabled));
            }
            else
            {
                excluded.Add(new ExcludedVoiceResponse(voice.VoiceId, reasons));
            }
        }

        return Ok(new AvailableVoicesResponse(compatible, excluded.Count, excluded));
    }

    /// <summary>
    /// Assigns a voice to a speaker (body <c>{ voiceId, reason? }</c>).
    /// Enforces compatibility (422 <c>VOICE_INCOMPATIBLE</c>) and cloning
    /// consent (403 <c>VOICE_CONSENT_REQUIRED</c>) server-side; keeps exactly
    /// one voice per speaker (reassignment replaces). Same voice returns 200
    /// <c>changed:false</c> with no invalidation. Changes publish
    /// invalidation and return <c>outputStale:true</c> + <c>OUTPUT_STALE</c>
    /// when final output exists. Zero-segment speakers allowed with
    /// <c>unusedSpeaker:true</c>.
    /// </summary>
    [HttpPut("speakers/{speakerId}/voice-assignment")]
    [Authorize(Policy = AuthPolicies.RequireProjectEditor)]
    [ProducesResponseType(typeof(VoiceAssignmentResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> AssignVoice(
        [FromRoute] string projectId,
        [FromRoute] string speakerId,
        [FromBody] VoiceAssignmentRequest? request,
        CancellationToken cancellationToken)
    {
        var tenantId = User.GetTenantId();
        var projectGuid = PublicIdParser.ParseProjectId(projectId);
        var speakerGuid = PublicIdParser.ParseSpeakerId(speakerId);
        await RequireProjectAsync(tenantId, projectGuid, cancellationToken).ConfigureAwait(false);
        await LoadOwnedSpeakerAsync(tenantId, projectGuid, speakerGuid, cancellationToken).ConfigureAwait(false);

        if (request is null || string.IsNullOrWhiteSpace(request.VoiceId))
        {
            throw new VoiceNotFoundException("Voice id must not be empty.");
        }

        var actor = RequireActorGuid();
        var correlationId = CorrelationIdMiddleware.GetCorrelationId(HttpContext);

        var result = await _voices.AssignExplicitAsync(
            tenantId, projectGuid, speakerGuid, request.VoiceId!,
            request.Reason, actor, correlationId, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Voice assignment. {TenantId} {ProjectId} {SpeakerId} {VoiceId} {Changed} {CorrelationId}",
            tenantId, projectGuid, speakerGuid, result.VoiceId, result.Changed, correlationId);

        return Ok(new VoiceAssignmentResponse(
            PublicIdParser.ToSpeakerId(result.SpeakerId),
            PublicIdParser.ToVoiceId(result.VoiceProfileId),
            result.VoiceId, result.Changed, result.OutputStale,
            result.WarningCode, result.UnusedSpeaker,
            result.OldVoiceProfileId.HasValue ? PublicIdParser.ToVoiceId(result.OldVoiceProfileId.Value) : null));
    }

    /// <summary>
    /// Creates a voice preview (body <c>{ speakerId, voiceId, text? }</c> +
    /// <c>Idempotency-Key</c> header). Delegates to the Task 004
    /// <c>VoicePreviewService</c> (consent/quota/text gates preserved):
    /// 202 first, 200 duplicate key, 403 cloning without consent, 429 quota,
    /// 400 empty/overlong text. Returns <c>previewId</c> (<c>vpv_</c>).
    /// </summary>
    [HttpPost("voice-previews")]
    [Authorize(Policy = AuthPolicies.RequireProjectEditor)]
    [ProducesResponseType(typeof(VoicePreviewResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> CreatePreview(
        [FromRoute] string projectId,
        [FromBody] CreateVoicePreviewRequest? request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        var tenantId = User.GetTenantId();
        var projectGuid = PublicIdParser.ParseProjectId(projectId);
        await RequireProjectAsync(tenantId, projectGuid, cancellationToken).ConfigureAwait(false);

        if (request is null)
        {
            throw new Domain.Exceptions.DomainException("Preview requires { speakerId, voiceId, text? }.");
        }

        var speakerGuid = PublicIdParser.ParseSpeakerId(request.SpeakerId);
        await LoadOwnedSpeakerAsync(tenantId, projectGuid, speakerGuid, cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(request.VoiceId))
        {
            throw new VoiceNotFoundException("Voice id must not be empty.");
        }

        var text = string.IsNullOrWhiteSpace(request.Text) ? DefaultPreviewText : request.Text.Trim();
        var actor = RequireActorGuid();
        var correlationId = CorrelationIdMiddleware.GetCorrelationId(HttpContext);

        VoicePreviewRequestResult result;
        try
        {
            result = await _previews.RequestPreviewAsync(
                tenantId, projectGuid, speakerGuid, request.VoiceId!,
                text, actor, idempotencyKey, null, cancellationToken).ConfigureAwait(false);
        }
        catch (NotFoundException ex)
        {
            throw new VoiceNotFoundException(ex.Message);
        }
        catch (ErrorCodeException ex) when (IsMarker(ex, VoicePreviewService.VoiceConsentRequiredMarker))
        {
            throw new VoiceConsentRequiredException(ex.Message);
        }
        catch (QuotaExceededException ex) when (IsMarker(ex, VoicePreviewService.PreviewQuotaExceededMarker))
        {
            throw new PreviewQuotaExceededException(ex.Message);
        }
        catch (ErrorCodeException ex) when (IsMarker(ex, VoicePreviewService.PreviewTextInvalidMarker))
        {
            throw new PreviewTextInvalidException(ex.Message);
        }

        _logger.LogInformation(
            "Voice preview requested. {TenantId} {ProjectId} {SpeakerId} {PreviewId} {Duplicate} {CorrelationId}",
            tenantId, projectGuid, speakerGuid, result.Job.Id, result.IsDuplicate, correlationId);

        var body = new VoicePreviewResponse(
            PublicIdParser.ToPreviewId(result.Job.Id),
            result.Job.Status.ToString(), result.IsDuplicate);
        return result.IsDuplicate ? Ok(body) : Accepted(body);
    }

    /// <summary>
    /// Lists voice previews for a project (newest first, paginated 20/max 100).
    /// </summary>
    [HttpGet("voice-previews")]
    [Authorize(Policy = AuthPolicies.RequireProjectViewer)]
    [ProducesResponseType(typeof(PaginatedResult<VoicePreviewSummaryResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ListPreviews(
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

        List<VoicePreviewJob> jobs;
        long total;
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            var scoped = db.Set<VoicePreviewJob>().AsNoTracking().Where(j => j.ProjectId == projectGuid);
            total = await scoped.LongCountAsync(cancellationToken).ConfigureAwait(false);
            jobs = await scoped
                .OrderByDescending(j => j.CreatedAt)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
        }

        var items = jobs.Select(j => new VoicePreviewSummaryResponse(
            PublicIdParser.ToPreviewId(j.Id),
            PublicIdParser.ToProjectId(j.ProjectId),
            PublicIdParser.ToSpeakerId(j.SpeakerId),
            j.VoiceId, j.Status.ToString(), j.CreatedAt)).ToList();
        return Ok(PaginatedResult<VoicePreviewSummaryResponse>.Create(items, page, pageSize, total));
    }

    /// <summary>
    /// Gets one voice preview (status + presigned URL when Completed).
    /// Cross-tenant ids return 404. <c>downloadUrl</c> is a 15-minute
    /// presigned URL (never an internal storage path); null until Completed.
    /// </summary>
    [HttpGet("voice-previews/{previewId}")]
    [Authorize(Policy = AuthPolicies.RequireProjectViewer)]
    [ProducesResponseType(typeof(VoicePreviewDetailResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetPreview(
        [FromRoute] string projectId,
        [FromRoute] string previewId,
        CancellationToken cancellationToken)
    {
        var tenantId = User.GetTenantId();
        var projectGuid = PublicIdParser.ParseProjectId(projectId);
        var jobGuid = PublicIdParser.ParsePreviewId(previewId);
        await RequireProjectAsync(tenantId, projectGuid, cancellationToken).ConfigureAwait(false);

        VoicePreviewJob? job;
        using (TenantContext.BeginMaintenanceScope())
        {
            using var db = _contextFactory.CreateDbContext();
            job = await db.Set<VoicePreviewJob>()
                .AsNoTracking()
                .FirstOrDefaultAsync(j => j.Id == jobGuid, cancellationToken)
                .ConfigureAwait(false);
        }

        if (job is null || job.TenantId != tenantId || job.ProjectId != projectGuid)
        {
            throw new NotFoundException($"Voice preview '{jobGuid:D}' was not found.");
        }

        string? url = null;
        DateTimeOffset? expiresAt = null;
        string? artifactId = job.ArtifactId?.ToString("D");
        if (job.Status == VoicePreviewStatus.Completed && job.ArtifactId.HasValue)
        {
            try
            {
                url = await _artifacts.GetDownloadUrlAsync(
                    tenantId, projectGuid, job.ArtifactId.Value,
                    SignedUrlPolicy.DefaultExpiry, cancellationToken).ConfigureAwait(false);
                expiresAt = DateTimeOffset.UtcNow.Add(SignedUrlPolicy.DefaultExpiry);
            }
            catch (ErrorCodeException)
            {
                url = null;
                expiresAt = null;
            }
        }

        return Ok(new VoicePreviewDetailResponse(
            PublicIdParser.ToPreviewId(job.Id),
            PublicIdParser.ToProjectId(job.ProjectId),
            PublicIdParser.ToSpeakerId(job.SpeakerId),
            job.VoiceId, job.Status.ToString(),
            artifactId, url, expiresAt, job.CreatedAt));
    }

    private static bool IsMarker(AppException ex, string marker)
    {
        return ex.Message.Contains(marker, StringComparison.Ordinal);
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

    private async Task<List<SpeakerSummaryResponse>> ToSummariesAsync(
        Guid tenantId,
        Guid projectGuid,
        IReadOnlyList<Speaker> speakers,
        CancellationToken cancellationToken)
    {
        if (speakers.Count == 0)
        {
            return [];
        }

        var speakerIds = speakers.Select(s => s.Id).ToList();
        Dictionary<Guid, int> counts;
        Dictionary<Guid, SpeakerVoiceAssignment> latest;
        Dictionary<Guid, VoiceProfile> profiles;

        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();

            var countRows = await db.Set<SpeechSegment>()
                .AsNoTracking()
                .Where(s => s.ProjectId == projectGuid && speakerIds.Contains(s.SpeakerId ?? Guid.Empty))
                .GroupBy(s => s.SpeakerId!.Value)
                .Select(g => new { SpeakerId = g.Key, Count = g.Count() })
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            counts = countRows.ToDictionary(r => r.SpeakerId, r => r.Count);

            var assignments = await db.Set<SpeakerVoiceAssignment>()
                .AsNoTracking()
                .Where(a => a.ProjectId == projectGuid && speakerIds.Contains(a.SpeakerId))
                .OrderByDescending(a => a.CreatedAt)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            latest = new Dictionary<Guid, SpeakerVoiceAssignment>();
            foreach (var assignment in assignments)
            {
                if (!latest.ContainsKey(assignment.SpeakerId))
                {
                    latest[assignment.SpeakerId] = assignment;
                }
            }

            var voiceIds = latest.Values.Select(a => a.VoiceProfileId).Distinct().ToList();
            profiles = voiceIds.Count == 0
                ? new Dictionary<Guid, VoiceProfile>()
                : await db.Set<VoiceProfile>()
                    .AsNoTracking()
                    .Where(v => voiceIds.Contains(v.Id))
                    .ToDictionaryAsync(v => v.Id, v => v, cancellationToken)
                    .ConfigureAwait(false);
        }

        var result = new List<SpeakerSummaryResponse>(speakers.Count);
        foreach (var speaker in speakers)
        {
            var count = counts.TryGetValue(speaker.Id, out var c) ? c : 0;
            SpeakerAssignedVoiceResponse? assigned = null;
            if (latest.TryGetValue(speaker.Id, out var assignment)
                && profiles.TryGetValue(assignment.VoiceProfileId, out var voice))
            {
                assigned = new SpeakerAssignedVoiceResponse(
                    PublicIdParser.ToVoiceId(voice.Id), voice.VoiceId,
                    voice.Provider, voice.Language, voice.Type.ToString());
            }

            result.Add(new SpeakerSummaryResponse(
                PublicIdParser.ToSpeakerId(speaker.Id),
                PublicIdParser.ToProjectId(speaker.ProjectId),
                speaker.SpeakerKey, speaker.DisplayName, count, assigned));
        }

        return result;
    }

    private async Task<Speaker> LoadOwnedSpeakerAsync(
        Guid tenantId,
        Guid projectGuid,
        Guid speakerGuid,
        CancellationToken cancellationToken)
    {
        Speaker? speaker;
        using (TenantContext.BeginMaintenanceScope())
        {
            using var db = _contextFactory.CreateDbContext();
            speaker = await db.Set<Speaker>()
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.Id == speakerGuid, cancellationToken).ConfigureAwait(false);
        }

        if (speaker is null || speaker.TenantId != tenantId || speaker.ProjectId != projectGuid)
        {
            throw new NotFoundException($"Speaker '{speakerGuid:D}' was not found.");
        }

        return speaker;
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
