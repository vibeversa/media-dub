using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DubbingPlatform.Application.Abstractions.Providers;
using DubbingPlatform.Application.Abstractions.Providers.Dtos;
using DubbingPlatform.Application.Configuration;
using DubbingPlatform.Application.Errors;
using DubbingPlatform.Application.Exceptions;
using DubbingPlatform.Application.MultiTenancy;
using DubbingPlatform.Application.Options;
using DubbingPlatform.Application.Providers;
using DubbingPlatform.Application.Security;
using DubbingPlatform.Application.Services;
using DubbingPlatform.Contracts.Messages;
using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Domain.Enums;
using DubbingPlatform.Domain.Exceptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DubbingPlatform.Application.Previews;

/// <summary>
/// Outcome of <see cref="VoicePreviewService.RequestPreviewAsync"/>.
/// <see cref="IsDuplicate"/> is true when an existing row was returned for a
/// duplicate idempotency key (no second provider call); Task 010 maps that to
/// HTTP 200.
/// </summary>
public sealed record VoicePreviewRequestResult(VoicePreviewJob Job, bool IsDuplicate);

/// <summary>
/// Publishes <see cref="VoicePreviewCompleted"/> terminal events.
/// Implemented in Infrastructure over MassTransit; tests substitute a fake.
/// Application must not reference MassTransit directly.
/// </summary>
public interface IVoicePreviewEventPublisher
{
    Task PublishAsync(VoicePreviewCompleted message, CancellationToken cancellationToken = default);
}

/// <summary>
/// Fast-lane voice preview orchestration, independent of the final dub audio
/// path. <see cref="RequestPreviewAsync"/> validates text, dedupes on the
/// idempotency key (duplicate keys return the existing row, never a second
/// provider call), enforces membership, server-resolves the voice id against
/// the tenant inventory (never a URL), gates cloned voices on recorded
/// consent (<c>VOICE_CONSENT_REQUIRED</c>, no bypass), enforces the per-tenant
/// daily cap plus per-minute throttle (<c>PREVIEW_QUOTA_EXCEEDED</c>, 429,
/// before any provider call), then drives the job
/// <c>Pending → Running → Completed/Failed</c> with a provider execution
/// record (provider name plus latency, never keys) and a tenant-scoped
/// <c>VoicePreviewAudio</c> artifact. Consent denials, quota denials, and
/// provider failures are persisted as rows (Failed with the marker in
/// <c>ErrorCode</c>/<c>ErrorMessage</c>) before the coded exception is thrown,
/// so the audit trail is complete. Provider timeouts fail with
/// <c>PREVIEW_PROVIDER_TIMEOUT</c> and are retryable via a new idempotency
/// key. <see cref="CancelAsync"/> only leaves <c>Pending/Running</c>;
/// terminal cancels throw <c>PREVIEW_STATE_CONFLICT</c> (409) with no state
/// change. Cross-tenant job ids read as 404 without leaking existence. Only
/// ids, durations, counts, and hashes are logged — never preview text.
/// </summary>
public sealed class VoicePreviewService
{
    /// <summary>Sub-code for illegal state transitions; the public code stays CONFLICT (409).</summary>
    public const string PreviewStateConflictMarker = "PREVIEW_STATE_CONFLICT";

    /// <summary>Sub-code for quota/throttle denials; the public code stays QUOTA_EXCEEDED (429).</summary>
    public const string PreviewQuotaExceededMarker = "PREVIEW_QUOTA_EXCEEDED";

    /// <summary>Sub-code for blocked cloned-voice previews; the public code stays CONSENT_REQUIRED (403).</summary>
    public const string VoiceConsentRequiredMarker = "VOICE_CONSENT_REQUIRED";

    /// <summary>Sub-code for empty/over-long preview text; the public code stays VALIDATION_FAILED (400).</summary>
    public const string PreviewTextInvalidMarker = "PREVIEW_TEXT_INVALID";

    /// <summary>Sub-code for provider timeouts; the public code stays PROVIDER_TIMEOUT (504).</summary>
    public const string PreviewProviderTimeoutMarker = "PREVIEW_PROVIDER_TIMEOUT";

    /// <summary>Sub-code for non-timeout provider failures; the public code stays PROVIDER_FAILED (502).</summary>
    public const string PreviewProviderFailedMarker = "PREVIEW_PROVIDER_FAILED";

    /// <summary>Audit action for preview requests.</summary>
    public const string AuditRequested = "voice.preview_requested";

    /// <summary>Audit action for preview completions.</summary>
    public const string AuditCompleted = "voice.preview_completed";

    /// <summary>Audit action for preview failures.</summary>
    public const string AuditFailed = "voice.preview_failed";

    /// <summary>Audit action for quota denials.</summary>
    public const string AuditDenied = "voice.preview_denied";

    /// <summary>Audit action for consent blocks.</summary>
    public const string AuditBlocked = "voice.preview_blocked";

    /// <summary>Audit action for cancellations.</summary>
    public const string AuditCancelled = "voice.preview_cancelled";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IStageExecutionContextFactory _contextFactory;
    private readonly ITtsProvider _tts;
    private readonly ProviderExecutionRecorder _recorder;
    private readonly ArtifactService _artifacts;
    private readonly AuditService _audit;
    private readonly IVoicePreviewEventPublisher _publisher;
    private readonly PreviewOptions _preview;
    private readonly VoiceOptions _voices;
    private readonly ILogger<VoicePreviewService> _logger;

    public VoicePreviewService(
        IStageExecutionContextFactory contextFactory,
        ITtsProvider tts,
        ProviderExecutionRecorder recorder,
        ArtifactService artifacts,
        AuditService audit,
        IVoicePreviewEventPublisher publisher,
        IOptions<PreviewOptions> previewOptions,
        IOptions<VoiceOptions> voiceOptions,
        ILogger<VoicePreviewService> logger)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        ArgumentNullException.ThrowIfNull(tts);
        ArgumentNullException.ThrowIfNull(recorder);
        ArgumentNullException.ThrowIfNull(artifacts);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(publisher);
        ArgumentNullException.ThrowIfNull(previewOptions);
        ArgumentNullException.ThrowIfNull(voiceOptions);
        ArgumentNullException.ThrowIfNull(logger);
        _contextFactory = contextFactory;
        _tts = tts;
        _recorder = recorder;
        _artifacts = artifacts;
        _audit = audit;
        _publisher = publisher;
        _preview = previewOptions.Value;
        _voices = voiceOptions.Value;
        _logger = logger;
    }

    /// <summary>
    /// Validates preview text (trimmed, non-empty, within length). Pure; throws
    /// <c>VALIDATION_FAILED</c> with a <c>PREVIEW_TEXT_INVALID</c> marker.
    /// </summary>
    public static string RequirePreviewText(string? text)
    {
        var trimmed = text?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            throw new ErrorCodeException(ErrorCodes.ValidationFailed, $"{PreviewTextInvalidMarker}: preview text must not be empty.");
        }

        if (trimmed.Length > VoicePreviewJob.MaxTextLength)
        {
            throw new ErrorCodeException(ErrorCodes.ValidationFailed, $"{PreviewTextInvalidMarker}: preview text must be at most {VoicePreviewJob.MaxTextLength} chars.");
        }

        return trimmed;
    }

    /// <summary>
    /// Validates a server-resolved voice id (inventory key, never a URL).
    /// Pure; rejects empty input and URL-shaped values (SSRF guard).
    /// </summary>
    public static string RequireVoiceId(string? voiceId)
    {
        var trimmed = voiceId?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            throw new ErrorCodeException(ErrorCodes.ValidationFailed, $"{PreviewTextInvalidMarker}: voice id must not be empty.");
        }

        if (trimmed.Contains("://", StringComparison.Ordinal))
        {
            throw new ErrorCodeException(ErrorCodes.ValidationFailed, "Voice ids are server-resolved inventory keys; sample URLs are not accepted.");
        }

        if (trimmed.Length > VoicePreviewJob.MaxVoiceIdLength)
        {
            throw new ErrorCodeException(ErrorCodes.ValidationFailed, "Voice id must be at most 256 chars.");
        }

        return trimmed;
    }

    /// <summary>
    /// Builds the <see cref="VoicePreviewCompleted"/> terminal message.
    /// Pure. Carries ids, status, and error codes only.
    /// </summary>
    public static VoicePreviewCompleted BuildCompletedMessage(
        Guid tenantId,
        Guid projectId,
        Guid runScope,
        VoicePreviewJob job,
        string? errorCode)
    {
        ArgumentNullException.ThrowIfNull(job);
        return new VoicePreviewCompleted(
            Guid.NewGuid(),
            Guid.NewGuid().ToString("N"),
            tenantId,
            projectId,
            runScope,
            null,
            null,
            "VoicePreview",
            job.Id.ToString("N"),
            null,
            MessageVersionPolicy.CurrentVersion,
            DateTimeOffset.UtcNow,
            0,
            null,
            null,
            null,
            job.Id,
            job.Status.ToString(),
            job.ArtifactId,
            job.ProviderExecutionId,
            errorCode);
    }

    /// <summary>
    /// Reads one preview job within the tenant scope (null when missing;
    /// cross-tenant ids read as missing so endpoints can return 404).
    /// </summary>
    public async Task<VoicePreviewJob?> GetAsync(
        Guid tenantId,
        Guid jobId,
        CancellationToken cancellationToken = default)
    {
        RequireTenant(tenantId);
        RequireId(jobId, nameof(jobId));

        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            return await db.Set<VoicePreviewJob>()
                .AsNoTracking()
                .FirstOrDefaultAsync(j => j.Id == jobId, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Requests a voice preview and drives it to a terminal state. Duplicate
    /// idempotency keys return the existing row without a second provider
    /// call. Failures (text, consent, quota, provider) persist a row first
    /// and then throw the coded exception.
    /// </summary>
    public async Task<VoicePreviewRequestResult> RequestPreviewAsync(
        Guid tenantId,
        Guid projectId,
        Guid speakerId,
        string voiceId,
        string text,
        Guid requestedByUserId,
        string? idempotencyKey = null,
        Guid? runId = null,
        CancellationToken cancellationToken = default)
    {
        RequireTenant(tenantId);
        RequireId(projectId, nameof(projectId));
        RequireId(speakerId, nameof(speakerId));
        RequireId(requestedByUserId, nameof(requestedByUserId));
        if (runId.HasValue && runId.Value == Guid.Empty)
        {
            throw new DomainException("RunId must not be empty when set.");
        }

        var resolvedVoiceId = RequireVoiceId(voiceId);
        var cleanedText = RequirePreviewText(text);
        var key = NormalizeIdempotencyKey(idempotencyKey);

        if (key is not null)
        {
            var duplicate = await FindByKeyAsync(tenantId, key, cancellationToken).ConfigureAwait(false);
            if (duplicate is not null)
            {
                return new VoicePreviewRequestResult(duplicate, true);
            }
        }

        var project = await RequireProjectAsync(tenantId, projectId, cancellationToken).ConfigureAwait(false);
        await LoadOwnedSpeakerAsync(tenantId, projectId, speakerId, cancellationToken).ConfigureAwait(false);
        await RequireMembershipAsync(tenantId, projectId, requestedByUserId, project, cancellationToken).ConfigureAwait(false);
        var voice = await LoadVoiceAsync(tenantId, resolvedVoiceId, cancellationToken).ConfigureAwait(false);
        var providerType = RequirePreviewProvider(voice);
        var isCloned = voice.Type == VoiceType.Cloned;

        var consentState = VoicePreviewConsentState.Verified;
        if (isCloned)
        {
            var duplicate = await RequireConsentAsync(
                tenantId, projectId, speakerId, voice, requestedByUserId,
                key, cleanedText, cancellationToken).ConfigureAwait(false);
            if (duplicate is not null)
            {
                return new VoicePreviewRequestResult(duplicate, true);
            }
        }

        {
            var duplicate = await RequireQuotaAsync(
                tenantId, projectId, speakerId, voice.VoiceId, requestedByUserId,
                key, cleanedText, consentState, cancellationToken).ConfigureAwait(false);
            if (duplicate is not null)
            {
                return new VoicePreviewRequestResult(duplicate, true);
            }
        }

        var jobId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var created = new VoicePreviewJob(
            jobId, tenantId, projectId, speakerId, voice.VoiceId, cleanedText,
            VoicePreviewStatus.Pending, requestedByUserId, key,
            VoicePreviewQuotaCheck.Allowed, null, consentState,
            null, null, null, null, now, null, null);

        try
        {
            using (TenantContext.BeginScope(tenantId))
            {
                using var db = _contextFactory.CreateDbContext();
                db.Set<VoicePreviewJob>().Add(created);
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (DomainException ex) when (ex.Message.Contains("CONFLICT", StringComparison.Ordinal) && key is not null)
        {
            var raced = await FindByKeyAsync(tenantId, key, cancellationToken).ConfigureAwait(false);
            if (raced is not null)
            {
                return new VoicePreviewRequestResult(raced, true);
            }

            throw;
        }

        await _audit.LogAsync(
            tenantId, projectId, requestedByUserId.ToString("D"), AuditRequested,
            "VoicePreviewJob", jobId.ToString("N"),
            SecretRedactor.Redact(JsonSerializer.Serialize(new
            {
                jobId = jobId.ToString("N"),
                speakerId = speakerId.ToString("N"),
                voiceId = voice.VoiceId,
                textLength = cleanedText.Length,
                idempotencyKey = key,
            }, JsonOptions)),
            cancellationToken).ConfigureAwait(false);

        await TransitionToRunningAsync(tenantId, jobId, cancellationToken).ConfigureAwait(false);

        var runScope = runId ?? jobId;
        return await SynthesizeAsync(
            tenantId, project, runScope, jobId, speakerId, voice,
            providerType, isCloned, cleanedText, requestedByUserId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Cancels a preview job from <c>Pending/Running</c>. Terminal jobs throw
    /// <c>CONFLICT</c> with a <c>PREVIEW_STATE_CONFLICT</c> marker and are
    /// left unchanged. Cross-tenant ids throw <c>NOT_FOUND</c>.
    /// </summary>
    public async Task<VoicePreviewJob> CancelAsync(
        Guid tenantId,
        Guid jobId,
        Guid actorUserId,
        CancellationToken cancellationToken = default)
    {
        RequireTenant(tenantId);
        RequireId(jobId, nameof(jobId));
        RequireId(actorUserId, nameof(actorUserId));

        VoicePreviewJob job;
        using (TenantContext.BeginMaintenanceScope())
        {
            using var db = _contextFactory.CreateDbContext();
            var current = await db.Set<VoicePreviewJob>()
                .AsNoTracking()
                .FirstOrDefaultAsync(j => j.Id == jobId, cancellationToken)
                .ConfigureAwait(false);
            if (current is null || current.TenantId != tenantId)
            {
                throw new NotFoundException($"Voice preview job '{jobId:D}' was not found.");
            }

            job = current;
        }

        var project = await RequireProjectAsync(tenantId, job.ProjectId, cancellationToken).ConfigureAwait(false);
        await RequireMembershipAsync(tenantId, job.ProjectId, actorUserId, project, cancellationToken).ConfigureAwait(false);

        var now = DateTimeOffset.UtcNow;
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            var tracked = await db.Set<VoicePreviewJob>()
                .FirstOrDefaultAsync(j => j.Id == jobId, cancellationToken)
                .ConfigureAwait(false);
            if (tracked is null)
            {
                throw new NotFoundException($"Voice preview job '{jobId:D}' was not found.");
            }

            if (tracked.IsTerminal)
            {
                throw new ErrorCodeException(ErrorCodes.Conflict, $"{PreviewStateConflictMarker}: voice preview job '{jobId:D}' is '{tracked.Status}' and cannot be cancelled.");
            }

            tracked.MarkCancelled(now);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            job = tracked;
        }

        var completed = BuildCompletedMessage(tenantId, job.ProjectId, jobId, job, null);
        await _publisher.PublishAsync(completed, cancellationToken).ConfigureAwait(false);

        await _audit.LogAsync(
            tenantId, job.ProjectId, actorUserId.ToString("D"), AuditCancelled,
            "VoicePreviewJob", jobId.ToString("N"),
            SecretRedactor.Redact(JsonSerializer.Serialize(new
            {
                jobId = jobId.ToString("N"),
                status = job.Status.ToString(),
            }, JsonOptions)),
            cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Voice preview {JobId} cancelled for tenant {TenantId}.",
            jobId, tenantId);
        return job;
    }

    private async Task<VoicePreviewRequestResult> SynthesizeAsync(
        Guid tenantId,
        DubbingProject project,
        Guid runScope,
        Guid jobId,
        Guid speakerId,
        VoiceProfile voice,
        ProviderType providerType,
        bool isCloned,
        string text,
        Guid actorUserId,
        CancellationToken cancellationToken)
    {
        var request = new TtsRequest(
            tenantId, project.Id, runScope, text,
            project.TargetLanguage, voice.VoiceId, 0, isCloned);
        var requestHash = ConfigurationHashCalculator.Compute(new
        {
            tenant = tenantId.ToString("N"),
            project = project.Id.ToString("N"),
            speaker = speakerId.ToString("N"),
            voice = voice.VoiceId,
            text,
            language = project.TargetLanguage,
        });

        var stopwatch = Stopwatch.StartNew();
        TtsResponse response;
        try
        {
            response = await _tts.SynthesizeAsync(request, cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Caller cancelled: leave the Running row for a later cancel/retry;
            // do not record a provider failure for an aborted call.
            throw;
        }
        catch (Exception ex) when (IsTimeout(ex, cancellationToken))
        {
            stopwatch.Stop();
            return await FailAsync(
                tenantId, project.Id, runScope, jobId, voice, providerType,
                requestHash, null, request.Text.Length, stopwatch.ElapsedMilliseconds,
                OutcomeClass.ProviderTimeout, ErrorCodes.ProviderTimeout,
                $"{PreviewProviderTimeoutMarker}: voice preview provider timed out; retry with a new idempotency key.",
                actorUserId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            var outcome = ex is ErrorCodeException
                ? OutcomeClass.ProviderPermanentFailure
                : OutcomeClass.ProviderTransientFailure;
            return await FailAsync(
                tenantId, project.Id, runScope, jobId, voice, providerType,
                requestHash, null, request.Text.Length, stopwatch.ElapsedMilliseconds,
                outcome, ErrorCodes.ProviderFailed,
                $"{PreviewProviderFailedMarker}: voice preview provider failed.",
                actorUserId, cancellationToken).ConfigureAwait(false);
        }

        if (response.DurationMs <= 0)
        {
            return await FailAsync(
                tenantId, project.Id, runScope, jobId, voice, providerType,
                requestHash, response, request.Text.Length, stopwatch.ElapsedMilliseconds,
                OutcomeClass.ProviderInvalidResponse, ErrorCodes.ProviderInvalidResponse,
                $"{PreviewProviderFailedMarker}: voice preview provider returned an invalid response.",
                actorUserId, cancellationToken).ConfigureAwait(false);
        }

        var audioBytes = ResolveAudioBytes(response);
        var audioHash = Convert.ToHexString(SHA256.HashData(audioBytes)).ToLowerInvariant();
        var responseHash = ConfigurationHashCalculator.Compute(new
        {
            contentId = response.ContentObjectId,
            duration = response.DurationMs,
            model = response.Model,
            audioHash,
        });

        PublishResult published;
        using (var stream = new MemoryStream(audioBytes, writable: false))
        {
            published = await _artifacts.PublishAsync(
                tenantId, project.Id, runScope,
                StageType.VoiceGeneration, ArtifactType.VoicePreviewAudio,
                stream, ".wav", "audio/wav",
                providerType.ToString(), response.Model, requestHash, null,
                [],
                null,
                cancellationToken).ConfigureAwait(false);
        }

        var metadataJson = JsonSerializer.Serialize(new
        {
            voicePreviewJobId = jobId.ToString("N"),
            voiceId = voice.VoiceId,
            durationMs = response.DurationMs,
            sampleRate = PreviewAudio.SampleRateHz,
            language = project.TargetLanguage,
        }, JsonOptions);
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            await db.Database.ExecuteSqlRawAsync(
                "UPDATE artifacts SET metadata_json = {0} WHERE id = {1} AND tenant_id = {2}",
                metadataJson, published.ArtifactId, tenantId).ConfigureAwait(false);
        }

        var executionId = await RecordExecutionAsync(
            tenantId, project.Id, runScope, voice, providerType, response,
            requestHash, responseHash, stopwatch.ElapsedMilliseconds,
            OutcomeClass.Success, jobId, cancellationToken).ConfigureAwait(false);

        var now = DateTimeOffset.UtcNow;
        VoicePreviewJob job;
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            var tracked = await db.Set<VoicePreviewJob>()
                .FirstOrDefaultAsync(j => j.Id == jobId, cancellationToken)
                .ConfigureAwait(false);
            if (tracked is null)
            {
                throw new NotFoundException($"Voice preview job '{jobId:D}' was not found.");
            }

            if (tracked.Status != VoicePreviewStatus.Running)
            {
                throw new ErrorCodeException(ErrorCodes.Conflict, $"{PreviewStateConflictMarker}: voice preview job '{jobId:D}' is '{tracked.Status}'; completion discarded.");
            }

            tracked.MarkCompleted(published.ArtifactId, executionId, now);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            job = tracked;
        }

        var completed = BuildCompletedMessage(tenantId, project.Id, runScope, job, null);
        await _publisher.PublishAsync(completed, cancellationToken).ConfigureAwait(false);

        await _audit.LogAsync(
            tenantId, project.Id, actorUserId.ToString("D"), AuditCompleted,
            "VoicePreviewJob", jobId.ToString("N"),
            SecretRedactor.Redact(JsonSerializer.Serialize(new
            {
                jobId = jobId.ToString("N"),
                artifactId = published.ArtifactId.ToString("N"),
                executionId = executionId.ToString("N"),
                provider = providerType.ToString(),
                model = response.Model,
                durationMs = response.DurationMs,
                latencyMs = stopwatch.ElapsedMilliseconds,
            }, JsonOptions)),
            cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Voice preview {JobId} completed for tenant {TenantId} in {LatencyMs}ms.",
            jobId, tenantId, stopwatch.ElapsedMilliseconds);
        return new VoicePreviewRequestResult(job, false);
    }

    private async Task<VoicePreviewRequestResult> FailAsync(
        Guid tenantId,
        Guid projectId,
        Guid runScope,
        Guid jobId,
        VoiceProfile voice,
        ProviderType providerType,
        string requestHash,
        TtsResponse? response,
        int textLength,
        long latencyMs,
        OutcomeClass outcome,
        string errorCode,
        string errorMessage,
        Guid actorUserId,
        CancellationToken cancellationToken)
    {
        var executionId = await RecordExecutionAsync(
            tenantId, projectId, runScope, voice, providerType, response,
            requestHash, null, latencyMs, outcome, jobId, cancellationToken).ConfigureAwait(false);

        var now = DateTimeOffset.UtcNow;
        VoicePreviewJob job;
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            var tracked = await db.Set<VoicePreviewJob>()
                .FirstOrDefaultAsync(j => j.Id == jobId, cancellationToken)
                .ConfigureAwait(false);
            if (tracked is null)
            {
                throw new NotFoundException($"Voice preview job '{jobId:D}' was not found.");
            }

            if (tracked.Status != VoicePreviewStatus.Running)
            {
                throw new ErrorCodeException(ErrorCodes.Conflict, $"{PreviewStateConflictMarker}: voice preview job '{jobId:D}' is '{tracked.Status}'; failure discarded.");
            }

            tracked.MarkFailed(errorCode, errorMessage, now);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            job = tracked;
        }

        var failed = BuildCompletedMessage(tenantId, projectId, runScope, job, errorCode);
        await _publisher.PublishAsync(failed, cancellationToken).ConfigureAwait(false);

        await _audit.LogAsync(
            tenantId, projectId, actorUserId.ToString("D"), AuditFailed,
            "VoicePreviewJob", jobId.ToString("N"),
            SecretRedactor.Redact(JsonSerializer.Serialize(new
            {
                jobId = jobId.ToString("N"),
                executionId = executionId.ToString("N"),
                provider = providerType.ToString(),
                errorCode,
                latencyMs,
            }, JsonOptions)),
            cancellationToken).ConfigureAwait(false);

        _logger.LogWarning(
            "Voice preview {JobId} failed for tenant {TenantId}: {ErrorCode}.",
            jobId, tenantId, errorCode);
        throw new ErrorCodeException(errorCode, errorMessage);
    }

    private async Task<Guid> RecordExecutionAsync(
        Guid tenantId,
        Guid projectId,
        Guid runScope,
        VoiceProfile voice,
        ProviderType providerType,
        TtsResponse? response,
        string requestHash,
        string? responseHash,
        long latencyMs,
        OutcomeClass outcome,
        Guid jobId,
        CancellationToken cancellationToken)
    {
        string? externalJobId = null;
        if (response?.RawMetadata is not null
            && response.RawMetadata.TryGetValue("mock.job_id", out var jobRef)
            && !string.IsNullOrWhiteSpace(jobRef))
        {
            externalJobId = jobRef.Trim();
        }

        var idempotencyKey = ProviderExecutionRecorder.BuildIdempotencyKey(
            runScope, "VoicePreview", jobId.ToString("N"), 0);
        var row = new ProviderExecution(
            Guid.NewGuid(), tenantId, projectId, runScope,
            null, providerType, ProviderCapability.Tts, response?.Model ?? "mock-1",
            response?.ModelVersion, response?.Deployment, null, null,
            0, requestHash, responseHash, Math.Max(0, latencyMs),
            response?.Usage?.TokensIn, response?.Usage?.TokensOut, response?.Usage?.AudioSeconds,
            response?.Usage?.EstimatedCostUsd, response?.Usage?.EstimatedCostUsd, null,
            outcome, null, null, null, voice.VoiceVersion, externalJobId, idempotencyKey,
            DateTimeOffset.UtcNow);
        return await _recorder.RecordAsync(row, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Enforces the cloned-voice consent gate. Returns null when a covering
    /// granted consent exists (plus the global kill-switch allows cloning);
    /// otherwise persists a <c>Blocked/Failed</c> row, audits it, and throws
    /// <c>CONSENT_REQUIRED</c> with a <c>VOICE_CONSENT_REQUIRED</c> marker.
    /// A key race that loses the insert returns the winning row so the caller
    /// can answer 200 without a provider call.
    /// </summary>
    private async Task<VoicePreviewJob?> RequireConsentAsync(
        Guid tenantId,
        Guid projectId,
        Guid speakerId,
        VoiceProfile voice,
        Guid actorUserId,
        string? idempotencyKey,
        string text,
        CancellationToken cancellationToken)
    {
        ConsentRecord? candidate = null;
        if (_voices.CloningEnabled)
        {
            candidate = await FindCoveringConsentAsync(tenantId, projectId, voice.Id, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            ConsentService.ValidateForVoice(candidate, projectId, voice.Id, null);
            return null;
        }
        catch (AppException)
        {
            var jobId = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;
            var message = $"{VoiceConsentRequiredMarker}: voice '{voice.VoiceId}' requires recorded consent before preview.";
            var blocked = new VoicePreviewJob(
                jobId, tenantId, projectId, speakerId, voice.VoiceId, text,
                VoicePreviewStatus.Failed, actorUserId, idempotencyKey,
                VoicePreviewQuotaCheck.Allowed, null, VoicePreviewConsentState.Blocked,
                null, null, ErrorCodes.ConsentRequired, message, now, null, now);

            try
            {
                using (TenantContext.BeginScope(tenantId))
                {
                    using var db = _contextFactory.CreateDbContext();
                    db.Set<VoicePreviewJob>().Add(blocked);
                    await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            catch (DomainException ex) when (ex.Message.Contains("CONFLICT", StringComparison.Ordinal) && idempotencyKey is not null)
            {
                return await FindByKeyAsync(tenantId, idempotencyKey, cancellationToken).ConfigureAwait(false);
            }

            await _audit.LogAsync(
                tenantId, projectId, actorUserId.ToString("D"), AuditBlocked,
                "VoicePreviewJob", jobId.ToString("N"),
                SecretRedactor.Redact(JsonSerializer.Serialize(new
                {
                    jobId = jobId.ToString("N"),
                    speakerId = speakerId.ToString("N"),
                    voiceId = voice.VoiceId,
                    textLength = text.Length,
                }, JsonOptions)),
                cancellationToken).ConfigureAwait(false);

            _logger.LogWarning(
                "Voice preview blocked for tenant {TenantId}: cloning voice without consent.",
                tenantId);
            throw new ErrorCodeException(ErrorCodes.ConsentRequired, message);
        }
    }

    /// <summary>
    /// Enforces the per-tenant daily cap plus per-minute throttle. Returns
    /// null when allowed; otherwise persists a <c>Denied/Failed</c> row,
    /// audits it, and throws <c>QUOTA_EXCEEDED</c> with a
    /// <c>PREVIEW_QUOTA_EXCEEDED</c> marker before any provider call.
    /// A key race that loses the insert returns the winning row.
    /// </summary>
    private async Task<VoicePreviewJob?> RequireQuotaAsync(
        Guid tenantId,
        Guid projectId,
        Guid speakerId,
        string voiceId,
        Guid actorUserId,
        string? idempotencyKey,
        string text,
        VoicePreviewConsentState consentState,
        CancellationToken cancellationToken)
    {
        string? reason = null;
        var now = DateTimeOffset.UtcNow;
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            var dayStart = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
            var daily = await db.Set<VoicePreviewJob>()
                .AsNoTracking()
                .CountAsync(j => j.CreatedAt >= dayStart, cancellationToken)
                .ConfigureAwait(false);
            if (daily >= _preview.MaxPreviewsPerDayPerTenant)
            {
                reason = $"daily cap of {_preview.MaxPreviewsPerDayPerTenant} previews exceeded";
            }
            else
            {
                var minuteStart = now.AddMinutes(-1);
                var recent = await db.Set<VoicePreviewJob>()
                    .AsNoTracking()
                    .CountAsync(j => j.CreatedAt >= minuteStart, cancellationToken)
                    .ConfigureAwait(false);
                if (recent >= _preview.MaxPreviewsPerMinutePerTenant)
                {
                    reason = $"per-minute throttle of {_preview.MaxPreviewsPerMinutePerTenant} previews exceeded";
                }
            }
        }

        if (reason is null)
        {
            return null;
        }

        var jobId = Guid.NewGuid();
        var at = DateTimeOffset.UtcNow;
        var message = $"{PreviewQuotaExceededMarker}: {reason}; no provider call was made.";
        var denied = new VoicePreviewJob(
            jobId, tenantId, projectId, speakerId, voiceId, text,
            VoicePreviewStatus.Failed, actorUserId, idempotencyKey,
            VoicePreviewQuotaCheck.Denied, reason, consentState,
            null, null, ErrorCodes.QuotaExceeded, message, at, null, at);

        try
        {
            using (TenantContext.BeginScope(tenantId))
            {
                using var db = _contextFactory.CreateDbContext();
                db.Set<VoicePreviewJob>().Add(denied);
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (DomainException ex) when (ex.Message.Contains("CONFLICT", StringComparison.Ordinal) && idempotencyKey is not null)
        {
            return await FindByKeyAsync(tenantId, idempotencyKey, cancellationToken).ConfigureAwait(false);
        }

        await _audit.LogAsync(
            tenantId, projectId, actorUserId.ToString("D"), AuditDenied,
            "VoicePreviewJob", jobId.ToString("N"),
            SecretRedactor.Redact(JsonSerializer.Serialize(new
            {
                jobId = jobId.ToString("N"),
                reason,
            }, JsonOptions)),
            cancellationToken).ConfigureAwait(false);

        _logger.LogWarning(
            "Voice preview denied for tenant {TenantId}: {Reason}.",
            tenantId, reason);
        throw new QuotaExceededException(message);
    }

    private async Task TransitionToRunningAsync(
        Guid tenantId,
        Guid jobId,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            var tracked = await db.Set<VoicePreviewJob>()
                .FirstOrDefaultAsync(j => j.Id == jobId, cancellationToken)
                .ConfigureAwait(false);
            if (tracked is null)
            {
                throw new NotFoundException($"Voice preview job '{jobId:D}' was not found.");
            }

            tracked.MarkRunning(now);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<VoicePreviewJob?> FindByKeyAsync(
        Guid tenantId,
        string key,
        CancellationToken cancellationToken)
    {
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            return await db.Set<VoicePreviewJob>()
                .AsNoTracking()
                .FirstOrDefaultAsync(j => j.IdempotencyKey == key, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task<ConsentRecord?> FindCoveringConsentAsync(
        Guid tenantId,
        Guid projectId,
        Guid voiceProfileId,
        CancellationToken cancellationToken)
    {
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            var records = await db.Set<ConsentRecord>()
                .AsNoTracking()
                .OrderByDescending(c => c.GrantedAt)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            return records.FirstOrDefault(c =>
                c.Status == ConsentStatus.Granted
                && !c.RevokedAt.HasValue
                && ConsentService.ScopeCoversProject(c.Scope, projectId)
                && (!c.VoiceProfileId.HasValue || c.VoiceProfileId.Value == Guid.Empty || c.VoiceProfileId.Value == voiceProfileId));
        }
    }

    private async Task<VoiceProfile> LoadVoiceAsync(
        Guid tenantId,
        string voiceId,
        CancellationToken cancellationToken)
    {
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            var voice = await db.Set<VoiceProfile>()
                .AsNoTracking()
                .Where(v => v.VoiceId == voiceId)
                .OrderByDescending(v => v.CreatedAt)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
            if (voice is null)
            {
                throw new NotFoundException($"Voice '{voiceId}' was not found.");
            }

            return voice;
        }
    }

    private static ProviderType RequirePreviewProvider(VoiceProfile voice)
    {
        if (Enum.TryParse<ProviderType>(voice.Provider.Trim(), ignoreCase: true, out var provider)
            && provider == ProviderType.Mock)
        {
            return provider;
        }

        throw new ErrorCodeException(
            ErrorCodes.ProviderConfigurationError,
            $"No preview adapter for provider '{voice.Provider}'. Only the mock adapter serves voice previews.");
    }

    private static byte[] ResolveAudioBytes(TtsResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (response.RawMetadata is not null
            && response.RawMetadata.TryGetValue("audioBase64", out var encoded)
            && !string.IsNullOrWhiteSpace(encoded))
        {
            try
            {
                return Convert.FromBase64String(encoded.Trim());
            }
            catch (FormatException ex)
            {
                throw new ErrorCodeException(ErrorCodes.ProviderInvalidResponse, "Voice preview provider returned a malformed audio payload.", ex);
            }
        }

        return PreviewAudio.BuildWav(response.DurationMs > 0 ? response.DurationMs : 1000);
    }

    private static bool IsTimeout(Exception exception, CancellationToken callerToken)
    {
        if (exception is OperationCanceledException && callerToken.IsCancellationRequested)
        {
            return false;
        }

        return exception is TimeoutException
            || exception is TaskCanceledException
            || (exception is ErrorCodeException coded
                && string.Equals(coded.ErrorCode, ErrorCodes.ProviderTimeout, StringComparison.Ordinal));
    }

    private async Task LoadOwnedSpeakerAsync(
        Guid tenantId,
        Guid projectId,
        Guid speakerId,
        CancellationToken cancellationToken)
    {
        Speaker? speaker;
        using (TenantContext.BeginMaintenanceScope())
        {
            using var db = _contextFactory.CreateDbContext();
            speaker = await db.Set<Speaker>()
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.Id == speakerId, cancellationToken)
                .ConfigureAwait(false);
        }

        if (speaker is null)
        {
            throw new NotFoundException($"Speaker '{speakerId:D}' was not found.");
        }

        if (speaker.TenantId != tenantId || speaker.ProjectId != projectId)
        {
            throw new ForbiddenException($"Speaker '{speakerId:D}' does not belong to the current tenant/project.");
        }
    }

    private async Task RequireMembershipAsync(
        Guid tenantId,
        Guid projectId,
        Guid actorUserId,
        DubbingProject project,
        CancellationToken cancellationToken)
    {
        if (project.OwnerUserId.HasValue && project.OwnerUserId.Value == actorUserId)
        {
            return;
        }

        bool isMember;
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            isMember = await db.Set<ProjectMembership>()
                .AsNoTracking()
                .AnyAsync(m => m.ProjectId == projectId && m.UserId == actorUserId, cancellationToken)
                .ConfigureAwait(false);
        }

        if (!isMember)
        {
            throw new ForbiddenException($"User '{actorUserId:D}' is not a member of project '{projectId:D}'.");
        }
    }

    private async Task<DubbingProject> RequireProjectAsync(
        Guid tenantId,
        Guid projectId,
        CancellationToken cancellationToken)
    {
        DubbingProject? project;
        bool isDeleted;
        using (TenantContext.BeginMaintenanceScope())
        {
            using var db = _contextFactory.CreateDbContext();
            project = await db.Set<DubbingProject>()
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken)
                .ConfigureAwait(false);
            if (project is null)
            {
                throw new NotFoundException($"Project '{projectId:D}' was not found.");
            }

            isDeleted = await db.Set<DubbingProject>()
                .Where(p => p.Id == projectId)
                .Select(p => EF.Property<bool>(p, "IsDeleted"))
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        if (project.TenantId != tenantId)
        {
            throw new ForbiddenException($"Project '{projectId:D}' does not belong to the current tenant.");
        }

        if (isDeleted)
        {
            throw new NotFoundException($"Project '{projectId:D}' was not found.");
        }

        return project;
    }

    private static string? NormalizeIdempotencyKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        var trimmed = key.Trim();
        if (trimmed.Length > VoicePreviewJob.MaxIdempotencyKeyLength)
        {
            throw new ErrorCodeException(ErrorCodes.ValidationFailed, "Idempotency key must be at most 128 chars.");
        }

        return trimmed;
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
            throw new DomainException(string.Concat(name, " must not be empty."));
        }
    }
}
