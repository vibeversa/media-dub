using System.Text.Json;
using System.Text.RegularExpressions;
using DubbingPlatform.Application.Configuration;
using DubbingPlatform.Application.MultiTenancy;
using DubbingPlatform.Application.Options;
using DubbingPlatform.Application.Security;
using DubbingPlatform.Application.Services;
using DubbingPlatform.Contracts.Messages;
using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Domain.Enums;
using DubbingPlatform.Domain.Exceptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DubbingPlatform.Application.Voices;

/// <summary>
/// Publishes <see cref="SpeakerVoiceChanged"/> invalidation events.
/// Implemented in Infrastructure over MassTransit; tests substitute a fake.
/// Application must not reference MassTransit directly.
/// </summary>
public interface ISpeakerVoiceEventPublisher
{
    Task PublishAsync(SpeakerVoiceChanged message, CancellationToken cancellationToken = default);
}

/// <summary>
/// Outcome of one explicit voice assignment: whether the voice changed, the
/// assignment row, whether final output already exists for the project
/// (<see cref="OutputStale"/> with <see cref="WarningCode"/> set to
/// <c>OUTPUT_STALE</c>), and whether the speaker has zero segments
/// (<see cref="UnusedSpeaker"/>). Only ids and flags are returned — never
/// audio, keys, or subject identity.
/// </summary>
public sealed record SpeakerVoiceAssignmentResult(
    Guid SpeakerId,
    Guid AssignmentId,
    Guid VoiceProfileId,
    string VoiceId,
    string Provider,
    bool Changed,
    bool OutputStale,
    string? WarningCode,
    bool UnusedSpeaker,
    Guid? OldVoiceProfileId);

/// <summary>
/// Explicit project-stable voice assignment per speaker (Task 010 HTTP layer).
/// Unlike the per-run deterministic <see cref="VoiceAssignmentService"/>, this
/// service assigns a user-chosen voice and keeps exactly one row per
/// (tenant, project, speaker): reassignment deletes prior rows and inserts a
/// single replacement scoped to the latest run (or the project id as a
/// pseudo-run when no runs exist), so readers never see duplicates. Consent
/// and <see cref="VoiceCompatibility"/> are enforced server-side regardless
/// of client filtering; cloning voices without a covering granted consent (or
/// with the global kill-switch off) fail with 403
/// <c>VOICE_CONSENT_REQUIRED</c>, incompatible voices fail with 422
/// <c>VOICE_INCOMPATIBLE</c>. Successful changes publish
/// <see cref="SpeakerVoiceChanged"/> and flag <c>outputStale</c> when final
/// output exists (no recompute). Every change audits actor + old/new voice +
/// reason + correlationId. Same-voice calls are a 200 no-op
/// (<c>Changed=false</c>) with no publish and no audit row. Only ids and
/// correlationId are logged — never audio or subject identity.
/// </summary>
public sealed class SpeakerVoiceService
{
    /// <summary>Audit action for voice assignment changes.</summary>
    public const string AuditAction = "voice.assignment_changed";

    /// <summary>Warning code when final output exists (success with warning).</summary>
    public const string OutputStaleWarningCode = "OUTPUT_STALE";

    /// <summary>Maximum reason length.</summary>
    public const int MaxReasonLength = 500;

    private static readonly Regex HtmlTagRegex = new("<.*?>", RegexOptions.Compiled);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IStageExecutionContextFactory _contextFactory;
    private readonly AuditService _audit;
    private readonly ISpeakerVoiceEventPublisher _publisher;
    private readonly VoiceOptions _voices;
    private readonly ILogger<SpeakerVoiceService> _logger;

    public SpeakerVoiceService(
        IStageExecutionContextFactory contextFactory,
        AuditService audit,
        ISpeakerVoiceEventPublisher publisher,
        IOptions<VoiceOptions> voiceOptions,
        ILogger<SpeakerVoiceService> logger)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(publisher);
        ArgumentNullException.ThrowIfNull(voiceOptions);
        ArgumentNullException.ThrowIfNull(logger);
        _contextFactory = contextFactory;
        _audit = audit;
        _publisher = publisher;
        _voices = voiceOptions.Value;
        _logger = logger;
    }

    /// <summary>
    /// Sanitizes an assignment reason (nullable, max 500, HTML stripped).
    /// Pure; returns null for empty input.
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

        return stripped.Length > MaxReasonLength ? stripped.Substring(0, MaxReasonLength) : stripped;
    }

    /// <summary>
    /// Resolves a voice reference to a tenant-owned profile. Accepts public
    /// ids (<c>voice_</c>), raw GUIDs (<c>D</c>/<c>N</c>), or inventory
    /// <c>VoiceId</c> strings. Unknown ids throw 404 <c>VOICE_NOT_FOUND</c>;
    /// cross-tenant rows read as 404 without leaking existence.
    /// </summary>
    public async Task<VoiceProfile> ResolveVoiceAsync(
        Guid tenantId,
        string voiceRef,
        CancellationToken cancellationToken = default)
    {
        RequireTenant(tenantId);
        if (string.IsNullOrWhiteSpace(voiceRef))
        {
            throw new VoiceNotFoundException("Voice id must not be empty.");
        }

        var trimmed = voiceRef.Trim();

        // Try GUID / public id first.
        var guid = TryParseVoiceGuid(trimmed);
        if (guid.HasValue)
        {
            VoiceProfile? byId;
            using (TenantContext.BeginMaintenanceScope())
            {
                using var db = _contextFactory.CreateDbContext();
                byId = await db.Set<VoiceProfile>()
                    .AsNoTracking()
                    .FirstOrDefaultAsync(v => v.Id == guid.Value, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (byId is null || byId.TenantId != tenantId)
            {
                throw new VoiceNotFoundException($"Voice '{trimmed}' was not found.");
            }

            return byId;
        }

        // Fall back to inventory VoiceId string (latest row wins).
        VoiceProfile? byVoiceId;
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            byVoiceId = await db.Set<VoiceProfile>()
                .AsNoTracking()
                .Where(v => v.VoiceId == trimmed)
                .OrderByDescending(v => v.CreatedAt)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        if (byVoiceId is null)
        {
            throw new VoiceNotFoundException($"Voice '{trimmed}' was not found.");
        }

        return byVoiceId;
    }

    /// <summary>
    /// Whether a covering granted consent exists for (project, voice).
    /// A null/empty voice relation on the consent covers any voice in scope.
    /// </summary>
    public async Task<bool> HasCoveringConsentAsync(
        Guid tenantId,
        Guid projectId,
        Guid voiceProfileId,
        CancellationToken cancellationToken = default)
    {
        RequireTenant(tenantId);
        RequireId(projectId, nameof(projectId));
        RequireId(voiceProfileId, nameof(voiceProfileId));

        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            var records = await db.Set<ConsentRecord>()
                .AsNoTracking()
                .Where(c => c.Status == ConsentStatus.Granted && c.RevokedAt == null)
                .OrderByDescending(c => c.GrantedAt)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            foreach (var record in records)
            {
                if (!ConsentService.ScopeCoversProject(record.Scope, projectId))
                {
                    continue;
                }

                if (record.VoiceProfileId.HasValue
                    && record.VoiceProfileId.Value != Guid.Empty
                    && record.VoiceProfileId.Value != voiceProfileId)
                {
                    continue;
                }

                return true;
            }

            return false;
        }
    }

    /// <summary>
    /// Assigns an explicit voice to one speaker, enforcing compatibility and
    /// consent server-side and keeping exactly one row per speaker.
    /// </summary>
    public async Task<SpeakerVoiceAssignmentResult> AssignExplicitAsync(
        Guid tenantId,
        Guid projectId,
        Guid speakerId,
        string voiceRef,
        string? reason,
        Guid actorUserId,
        string? correlationId = null,
        CancellationToken cancellationToken = default)
    {
        RequireTenant(tenantId);
        RequireId(projectId, nameof(projectId));
        RequireId(speakerId, nameof(speakerId));
        RequireId(actorUserId, nameof(actorUserId));
        if (string.IsNullOrWhiteSpace(voiceRef))
        {
            throw new VoiceNotFoundException("Voice id must not be empty.");
        }

        var project = await RequireProjectAsync(tenantId, projectId, cancellationToken).ConfigureAwait(false);
        await RequireMembershipAsync(tenantId, projectId, actorUserId, project, cancellationToken).ConfigureAwait(false);
        await LoadOwnedSpeakerAsync(tenantId, projectId, speakerId, cancellationToken).ConfigureAwait(false);

        var voice = await ResolveVoiceAsync(tenantId, voiceRef, cancellationToken).ConfigureAwait(false);
        var cleanedReason = SanitizeReason(reason);
        var correlation = string.IsNullOrWhiteSpace(correlationId) ? null : correlationId.Trim();

        var needsConsent = VoiceCompatibility.RequiresConsent(voice);
        var hasConsent = needsConsent
            && _voices.CloningEnabled
            && await HasCoveringConsentAsync(tenantId, projectId, voice.Id, cancellationToken).ConfigureAwait(false);

        if (needsConsent)
        {
            if (!_voices.CloningEnabled)
            {
                throw new VoiceConsentRequiredException(
                    $"Voice '{voice.VoiceId}' is a cloning voice and Voices:CloningEnabled is off.");
            }

            if (!hasConsent)
            {
                throw new VoiceConsentRequiredException(
                    $"Voice '{voice.VoiceId}' requires recorded consent before assignment.");
            }
        }

        var exclusions = VoiceCompatibility.Check(voice, project, hasConsent || !needsConsent, _voices.CloningEnabled);
        if (exclusions.Count > 0)
        {
            throw new VoiceIncompatibleException(voice.VoiceId, exclusions);
        }

        var segmentCount = await CountSegmentsAsync(tenantId, projectId, speakerId, cancellationToken).ConfigureAwait(false);
        var unusedSpeaker = segmentCount == 0;

        List<SpeakerVoiceAssignment> existing;
        Guid runScope;
        bool outputStale;
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            existing = await db.Set<SpeakerVoiceAssignment>()
                .AsNoTracking()
                .Where(a => a.ProjectId == projectId && a.SpeakerId == speakerId)
                .OrderByDescending(a => a.CreatedAt)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            var latestRun = await db.Set<ProcessingRun>()
                .AsNoTracking()
                .Where(r => r.ProjectId == projectId)
                .OrderByDescending(r => r.CreatedAt)
                .Select(r => (Guid?)r.Id)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
            runScope = latestRun ?? projectId;

            outputStale = await db.Set<OutputAsset>()
                .AsNoTracking()
                .AnyAsync(o => o.ProjectId == projectId, cancellationToken)
                .ConfigureAwait(false);
        }

        var current = existing.Count > 0 ? existing[0] : null;
        if (current is not null && current.VoiceProfileId == voice.Id)
        {
            return new SpeakerVoiceAssignmentResult(
                speakerId, current.Id, voice.Id, voice.VoiceId, voice.Provider,
                false, false, null, unusedSpeaker, current.VoiceProfileId);
        }

        var oldVoiceId = current?.VoiceProfileId;
        var policyHash = ConfigurationHashCalculator.Compute(new
        {
            tenant = tenantId.ToString("N"),
            project = projectId.ToString("N"),
            speaker = speakerId.ToString("N"),
            voice = voice.Id.ToString("N"),
        });

        var now = DateTimeOffset.UtcNow;
        SpeakerVoiceAssignment created;
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var tracked = await db.Set<SpeakerVoiceAssignment>()
                    .Where(a => a.ProjectId == projectId && a.SpeakerId == speakerId)
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (tracked.Count > 0)
                {
                    db.Set<SpeakerVoiceAssignment>().RemoveRange(tracked);
                    await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                }

                created = new SpeakerVoiceAssignment(
                    Guid.NewGuid(), tenantId, projectId, runScope,
                    speakerId, voice.Id, string.IsNullOrWhiteSpace(cleanedReason) ? "manual" : cleanedReason!,
                    policyHash, now);
                db.Set<SpeakerVoiceAssignment>().Add(created);
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

                var details = SecretRedactor.Redact(JsonSerializer.Serialize(new
                {
                    assignmentId = created.Id.ToString("N"),
                    speakerId = speakerId.ToString("N"),
                    oldVoiceProfileId = oldVoiceId?.ToString("N"),
                    newVoiceProfileId = voice.Id.ToString("N"),
                    voiceId = voice.VoiceId,
                    reason = cleanedReason,
                    correlationId = correlation,
                    outputStale,
                }, JsonOptions));

                db.Set<AuditEvent>().Add(new AuditEvent(
                    Guid.NewGuid(), tenantId, projectId,
                    actorUserId.ToString("D"), AuditAction,
                    "SpeakerVoiceAssignment", created.Id.ToString("N"),
                    details, now));
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

        var message = new SpeakerVoiceChanged(
            Guid.NewGuid(), correlation ?? now.ToString("O"),
            tenantId, projectId, runScope,
            null, null, "Speaker", speakerId.ToString("N"), null,
            MessageVersionPolicy.CurrentVersion, now, 0, null, policyHash, null,
            speakerId,
            oldVoiceId ?? Guid.Empty, voice.Id,
            cleanedReason, actorUserId, outputStale);
        await _publisher.PublishAsync(message, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Voice assignment changed. {TenantId} {ProjectId} {SpeakerId} {VoiceId} {CorrelationId}",
            tenantId, projectId, speakerId, voice.VoiceId, correlation);

        return new SpeakerVoiceAssignmentResult(
            speakerId, created.Id, voice.Id, voice.VoiceId, voice.Provider,
            true, outputStale, outputStale ? OutputStaleWarningCode : null,
            unusedSpeaker, oldVoiceId);
    }

    private static Guid? TryParseVoiceGuid(string trimmed)
    {
        if (Guid.TryParse(trimmed, out var guid) && guid != Guid.Empty)
        {
            return guid;
        }

        var compact = trimmed.Replace("-", string.Empty, StringComparison.Ordinal);
        if (Guid.TryParseExact(compact, "N", out var compactGuid) && compactGuid != Guid.Empty)
        {
            return compactGuid;
        }

        const string prefix = "voice_";
        if (trimmed.StartsWith(prefix, StringComparison.Ordinal))
        {
            var hex = trimmed.Substring(prefix.Length);
            if (hex.Length == 32 && Guid.TryParseExact(hex, "N", out var prefixed) && prefixed != Guid.Empty)
            {
                return prefixed;
            }
        }

        return null;
    }

    private async Task<int> CountSegmentsAsync(
        Guid tenantId, Guid projectId, Guid speakerId, CancellationToken cancellationToken)
    {
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            return await db.Set<SpeechSegment>()
                .AsNoTracking()
                .CountAsync(s => s.ProjectId == projectId && s.SpeakerId == speakerId, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task LoadOwnedSpeakerAsync(
        Guid tenantId, Guid projectId, Guid speakerId, CancellationToken cancellationToken)
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

        if (speaker is null || speaker.TenantId != tenantId || speaker.ProjectId != projectId)
        {
            throw new Exceptions.NotFoundException($"Speaker '{speakerId:D}' was not found.");
        }
    }

    private async Task RequireMembershipAsync(
        Guid tenantId, Guid projectId, Guid actorUserId, DubbingProject project, CancellationToken cancellationToken)
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
            throw new Exceptions.ForbiddenException($"User '{actorUserId:D}' is not a member of project '{projectId:D}'.");
        }
    }

    private async Task<DubbingProject> RequireProjectAsync(
        Guid tenantId, Guid projectId, CancellationToken cancellationToken)
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
                throw new Exceptions.NotFoundException($"Project '{projectId:D}' was not found.");
            }

            isDeleted = await db.Set<DubbingProject>()
                .Where(p => p.Id == projectId)
                .Select(p => EF.Property<bool>(p, "IsDeleted"))
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        if (project.TenantId != tenantId)
        {
            throw new Exceptions.ForbiddenException($"Project '{projectId:D}' does not belong to the current tenant.");
        }

        if (isDeleted)
        {
            throw new Exceptions.NotFoundException($"Project '{projectId:D}' was not found.");
        }

        return project;
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
