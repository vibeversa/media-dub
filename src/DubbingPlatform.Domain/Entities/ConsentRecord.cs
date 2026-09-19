using DubbingPlatform.Domain.Enums;
using DubbingPlatform.Domain.Exceptions;

namespace DubbingPlatform.Domain.Entities;

public sealed class ConsentRecord
{
    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public string SubjectIdentity { get; private set; }

    public string EvidenceReference { get; private set; }

    public string Scope { get; private set; }

    public string Jurisdiction { get; private set; }

    public ConsentStatus Status { get; private set; }

    public Guid? VoiceProfileId { get; private set; }

    public DateTimeOffset GrantedAt { get; private set; }

    public DateTimeOffset? RevokedAt { get; private set; }

    private ConsentRecord()
    {
        SubjectIdentity = string.Empty;
        EvidenceReference = string.Empty;
        Scope = string.Empty;
        Jurisdiction = string.Empty;
    }

    public ConsentRecord(
        Guid id,
        Guid tenantId,
        string subjectIdentity,
        string evidenceReference,
        string scope,
        string jurisdiction,
        ConsentStatus status,
        Guid? voiceProfileId,
        DateTimeOffset grantedAt,
        DateTimeOffset? revokedAt)
    {
        Id = id;
        TenantId = tenantId;
        SubjectIdentity = subjectIdentity;
        EvidenceReference = evidenceReference;
        Scope = scope;
        Jurisdiction = jurisdiction;
        Status = status;
        VoiceProfileId = voiceProfileId;
        GrantedAt = grantedAt;
        RevokedAt = revokedAt;

        Validate();
    }

    public void Validate()
    {
        if (Id == Guid.Empty)
        {
            throw new DomainException("ConsentRecord Id must not be empty.");
        }

        if (TenantId == Guid.Empty)
        {
            throw new DomainException("ConsentRecord TenantId must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(SubjectIdentity))
        {
            throw new DomainException("ConsentRecord SubjectIdentity must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(EvidenceReference))
        {
            throw new DomainException("ConsentRecord EvidenceReference must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(Scope))
        {
            throw new DomainException("ConsentRecord Scope must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(Jurisdiction))
        {
            throw new DomainException("ConsentRecord Jurisdiction must not be empty.");
        }

        if (VoiceProfileId.HasValue && VoiceProfileId.Value == Guid.Empty)
        {
            throw new DomainException("ConsentRecord VoiceProfileId must not be empty when set.");
        }

        if (RevokedAt.HasValue && RevokedAt.Value < GrantedAt)
        {
            throw new DomainException("ConsentRecord RevokedAt must not be before GrantedAt.");
        }
    }

    /// <summary>
    /// Revokes a granted consent. Transitions <c>Status</c> to
    /// <c>Revoked</c> and stamps <c>RevokedAt</c>; already-revoked rows are
    /// left untouched by the service (idempotent) and never transition back.
    /// Existing artifacts remain immutable — revocation only blocks new uses
    /// (callers re-validate live before every assignment).
    /// </summary>
    public void Revoke(DateTimeOffset revokedAt)
    {
        if (Status == ConsentStatus.Revoked)
        {
            return;
        }

        if (revokedAt < GrantedAt)
        {
            throw new DomainException("ConsentRecord RevokedAt must not be before GrantedAt.");
        }

        Status = ConsentStatus.Revoked;
        RevokedAt = revokedAt;

        Validate();
    }
}
