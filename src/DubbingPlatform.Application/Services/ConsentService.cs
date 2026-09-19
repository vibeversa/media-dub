using DubbingPlatform.Application.Errors;
using DubbingPlatform.Application.Exceptions;
using DubbingPlatform.Application.MultiTenancy;
using DubbingPlatform.Application.Services;
using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Domain.Enums;
using DubbingPlatform.Domain.Exceptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DubbingPlatform.Application.Services;

/// <summary>
/// Consent lifecycle for cloned voices. Consent rows are tenant-isolated and
/// append-audited: <see cref="GrantAsync"/> creates a <c>Granted</c> row,
/// <see cref="RevokeAsync"/> transitions to <c>Revoked</c> with
/// <c>RevokedAt</c> (idempotent; already-revoked returns existing). Existing
/// artifacts remain immutable — revocation only blocks new uses because every
/// voice assignment re-validates live via <see cref="ValidateForVoice"/>.
/// No biometric audio is stored here: only subject/evidence references, scope,
/// jurisdiction, and the optional voice relation. Never logs subject identity
/// or evidence content: only ids and status.
/// </summary>
public sealed class ConsentService
{
    private readonly IStageExecutionContextFactory _contextFactory;
    private readonly AuditService _audit;
    private readonly ILogger<ConsentService> _logger;

    public ConsentService(
        IStageExecutionContextFactory contextFactory,
        AuditService audit,
        ILogger<ConsentService> logger)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(logger);
        _contextFactory = contextFactory;
        _audit = audit;
        _logger = logger;
    }

    /// <summary>
    /// Whether <paramref name="scope"/> covers <paramref name="projectId"/>.
    /// Pure. True when the trimmed scope is <c>*</c>, or contains the project
    /// id in <c>N</c> or <c>D</c> form (case-insensitive). All other shapes
    /// (including empty) do not cover the project.
    /// </summary>
    public static bool ScopeCoversProject(string? scope, Guid projectId)
    {
        if (projectId == Guid.Empty)
        {
            throw new DomainException("ProjectId must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(scope))
        {
            return false;
        }

        var trimmed = scope.Trim();
        if (string.Equals(trimmed, "*", StringComparison.Ordinal))
        {
            return true;
        }

        return trimmed.Contains(projectId.ToString("N"), StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains(projectId.ToString("D"), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Live consent check for one cloned-voice use. Pure. Throws
    /// <c>CONSENT_REQUIRED</c> when evidence is missing, the status is not
    /// <c>Granted</c>, the row is revoked, the scope does not cover
    /// <paramref name="projectId"/>, or the voice relation (when set) does not
    /// match <paramref name="voiceProfileId"/>; throws <c>POLICY_DENIED</c>
    /// only when the jurisdiction mismatches
    /// <c>ProcessingPolicy.ResidencyConstraint</c>. A null policy skips the
    /// jurisdiction check (fail open on jurisdiction, still fail closed on
    /// consent itself). A null <paramref name="voiceProfileId"/> skips the
    /// voice-relation check.
    /// </summary>
    public static void ValidateForVoice(
        ConsentRecord? consent,
        Guid projectId,
        Guid? voiceProfileId,
        ProcessingPolicy? policy)
    {
        if (projectId == Guid.Empty)
        {
            throw new DomainException("ProjectId must not be empty.");
        }

        if (voiceProfileId.HasValue && voiceProfileId.Value == Guid.Empty)
        {
            throw new DomainException("VoiceProfileId must not be empty when set.");
        }

        if (consent is null)
        {
            throw new ErrorCodeException(ErrorCodes.ConsentRequired, "No granted consent covers this cloned voice use.");
        }

        if (string.IsNullOrWhiteSpace(consent.SubjectIdentity)
            || string.IsNullOrWhiteSpace(consent.EvidenceReference)
            || string.IsNullOrWhiteSpace(consent.Scope)
            || string.IsNullOrWhiteSpace(consent.Jurisdiction))
        {
            throw new ErrorCodeException(ErrorCodes.ConsentRequired, "Consent evidence is incomplete for this cloned voice use.");
        }

        if (consent.Status != ConsentStatus.Granted || consent.RevokedAt.HasValue)
        {
            throw new ErrorCodeException(ErrorCodes.ConsentRequired, $"Consent '{consent.Id:D}' is '{consent.Status}'; a granted consent is required for cloned voices.");
        }

        if (!ScopeCoversProject(consent.Scope, projectId))
        {
            throw new ErrorCodeException(ErrorCodes.ConsentRequired, $"Consent '{consent.Id:D}' scope does not cover this project.");
        }

        if (voiceProfileId.HasValue
            && consent.VoiceProfileId.HasValue
            && consent.VoiceProfileId.Value != Guid.Empty
            && consent.VoiceProfileId.Value != voiceProfileId.Value)
        {
            throw new ErrorCodeException(ErrorCodes.ConsentRequired, $"Consent '{consent.Id:D}' does not cover this voice.");
        }

        var constraint = policy?.ResidencyConstraint?.Trim();
        if (!string.IsNullOrWhiteSpace(constraint)
            && !string.Equals(consent.Jurisdiction.Trim(), constraint, StringComparison.OrdinalIgnoreCase))
        {
            throw new ErrorCodeException(ErrorCodes.PolicyDenied, $"Consent jurisdiction '{consent.Jurisdiction.Trim()}' does not satisfy policy residency '{constraint}'.");
        }
    }

    /// <summary>
    /// Grants a new consent row (status <c>Granted</c>) and audits
    /// <c>consent.granted</c>. Evidence, scope, and jurisdiction are required;
    /// <paramref name="voiceProfileId"/> optionally binds the consent to one
    /// voice (null means any voice for the scope).
    /// </summary>
    public async Task<ConsentRecord> GrantAsync(
        Guid tenantId,
        string subjectIdentity,
        string evidenceReference,
        string scope,
        string jurisdiction,
        Guid? voiceProfileId,
        CancellationToken cancellationToken = default)
    {
        RequireTenant(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(subjectIdentity);
        ArgumentException.ThrowIfNullOrWhiteSpace(evidenceReference);
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(jurisdiction);
        if (voiceProfileId.HasValue && voiceProfileId.Value == Guid.Empty)
        {
            throw new DomainException("VoiceProfileId must not be empty when set.");
        }

        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var record = new ConsentRecord(
            id, tenantId,
            subjectIdentity.Trim(), evidenceReference.Trim(),
            scope.Trim(), jurisdiction.Trim(),
            ConsentStatus.Granted, voiceProfileId, now, null);

        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            db.Set<ConsentRecord>().Add(record);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        await _audit.LogAsync(
            tenantId, null, "consent-service", "consent.granted",
            "ConsentRecord", id.ToString("N"),
            System.Text.Json.JsonSerializer.Serialize(new
            {
                consentId = id.ToString("N"),
                scope = scope.Trim(),
                jurisdiction = jurisdiction.Trim(),
                voiceProfileId = voiceProfileId?.ToString("N"),
            }),
            cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Consent {ConsentId} granted for tenant {TenantId}.", id, tenantId);
        return record;
    }

    /// <summary>
    /// Revokes a consent row (status <c>Revoked</c> plus <c>RevokedAt</c>) and
    /// audits <c>consent.revoked</c>. Idempotent: already-revoked rows return
    /// existing without a second audit event. Existing artifacts remain
    /// immutable; revocation blocks only new uses via live validation.
    /// </summary>
    public async Task<ConsentRecord> RevokeAsync(
        Guid tenantId,
        Guid consentId,
        string actor,
        CancellationToken cancellationToken = default)
    {
        RequireTenant(tenantId);
        RequireId(consentId, nameof(consentId));
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);

        ConsentRecord record;
        bool alreadyRevoked;
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            var current = await db.Set<ConsentRecord>()
                .FirstOrDefaultAsync(c => c.Id == consentId, cancellationToken).ConfigureAwait(false);
            if (current is null)
            {
                throw new NotFoundException($"Consent '{consentId}' was not found.");
            }

            if (current.TenantId != tenantId)
            {
                throw new ForbiddenException($"Consent '{consentId}' does not belong to the current tenant.");
            }

            alreadyRevoked = current.Status == ConsentStatus.Revoked;
            if (!alreadyRevoked)
            {
                current.Revoke(DateTimeOffset.UtcNow);
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }

            record = current;
        }

        if (!alreadyRevoked)
        {
            await _audit.LogAsync(
                tenantId, null, actor.Trim(), "consent.revoked",
                "ConsentRecord", consentId.ToString("N"),
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    consentId = consentId.ToString("N"),
                }),
                cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Consent {ConsentId} revoked for tenant {TenantId}.", consentId, tenantId);
        }

        return record;
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
