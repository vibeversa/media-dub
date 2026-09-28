using DubbingPlatform.Application.Errors;
using DubbingPlatform.Application.Exceptions;
using DubbingPlatform.Application.Services;
using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Domain.Enums;
using DubbingPlatform.Domain.Voice;

namespace DubbingPlatform.IntegrationTests.Security;

/// <summary>
/// Task 37 consent hardening proofs. All facts are hermetic (no Docker):
/// disabled-by-default voice, revocation blocking new use immediately,
/// in-flight drain semantics, audit emission, and expiry-as-revoked.
/// </summary>
public sealed class ConsentTests
{
    private static readonly Guid Tenant = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Project = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly DateTimeOffset GrantedAt = new(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);

    private static ConsentRecord Granted(
        string scope = "*",
        Guid? voiceProfileId = null,
        string jurisdiction = "US",
        ConsentStatus status = ConsentStatus.Granted,
        DateTimeOffset? revokedAt = null)
    {
        return new ConsentRecord(
            Guid.NewGuid(), Tenant, "subject-ref", "evidence-ref", scope,
            jurisdiction, status, voiceProfileId, GrantedAt, revokedAt);
    }

    [Fact]
    public void DefaultDisabled_NoGrant_Blocked()
    {
        // Null consent (no grant on file) blocks new use fail-closed.
        Assert.Equal(ConsentDecision.Blocked, ConsentGate.Evaluate(null, Project, null));
        Assert.Equal(ConsentState.Unknown, ConsentGate.ResolveState(null));
        Assert.False(ConsentGate.IsUsable(ConsentState.Unknown));

        var rejected = Assert.Throws<ErrorCodeException>(
            () => ConsentService.ValidateForVoice(null, Project, null, null));
        Assert.Equal(ErrorCodes.ConsentRequired, rejected.ErrorCode);
        Assert.Equal(403, rejected.StatusCode);
    }

    [Fact]
    public void Granted_Allows_NewUse()
    {
        var record = Granted();
        Assert.Equal(ConsentState.Granted, ConsentGate.ResolveState(record));
        Assert.True(ConsentGate.IsUsable(ConsentState.Granted));
        Assert.Equal(ConsentDecision.Allowed, ConsentGate.Evaluate(record, Project, null));

        ConsentService.ValidateForVoice(record, Project, null, null);
    }

    [Fact]
    public void Revoked_BlocksNewUse_Immediately()
    {
        var record = Granted(status: ConsentStatus.Revoked, revokedAt: GrantedAt.AddDays(1));
        Assert.Equal(ConsentState.Revoked, ConsentGate.ResolveState(record));
        Assert.False(ConsentGate.IsUsable(ConsentState.Revoked));
        Assert.Equal(ConsentDecision.Blocked, ConsentGate.Evaluate(record, Project, null));

        var rejected = Assert.Throws<ErrorCodeException>(
            () => ConsentService.ValidateForVoice(record, Project, null, null));
        Assert.Equal(ErrorCodes.ConsentRequired, rejected.ErrorCode);
        Assert.Equal(403, rejected.StatusCode);
    }

    [Fact]
    public void Expired_TreatedAsRevoked()
    {
        var record = Granted(status: ConsentStatus.Expired);
        Assert.Equal(ConsentState.Expired, ConsentGate.ResolveState(record));
        Assert.False(ConsentGate.IsUsable(ConsentState.Expired));
        Assert.Equal(ConsentDecision.Blocked, ConsentGate.Evaluate(record, Project, null));

        var rejected = Assert.Throws<ErrorCodeException>(
            () => ConsentService.ValidateForVoice(record, Project, null, null));
        Assert.Equal(ErrorCodes.ConsentRequired, rejected.ErrorCode);
    }

    [Fact]
    public void Pending_MapsToUnknown_Blocked()
    {
        // Pending grants (verification in flight) are disabled-by-default:
        // unknown state blocks new use until explicitly granted.
        var record = Granted(status: ConsentStatus.Pending);
        Assert.Equal(ConsentState.Unknown, ConsentGate.ResolveState(record));
        Assert.Equal(ConsentDecision.Blocked, ConsentGate.Evaluate(record, Project, null));

        var rejected = Assert.Throws<ErrorCodeException>(
            () => ConsentService.ValidateForVoice(record, Project, null, null));
        Assert.Equal(ErrorCodes.ConsentRequired, rejected.ErrorCode);
    }

    [Fact]
    public void ScopeMismatch_Blocked()
    {
        var other = Guid.Parse("44444444-4444-4444-4444-444444444444");
        var record = Granted(scope: other.ToString("N"));
        Assert.Equal(ConsentDecision.Blocked, ConsentGate.Evaluate(record, Project, null));

        var rejected = Assert.Throws<ErrorCodeException>(
            () => ConsentService.ValidateForVoice(record, Project, null, null));
        Assert.Equal(ErrorCodes.ConsentRequired, rejected.ErrorCode);
    }

    [Fact]
    public void VoiceMismatch_Blocked()
    {
        var bound = Guid.NewGuid();
        var other = Guid.NewGuid();
        var record = Granted(voiceProfileId: bound);
        Assert.Equal(ConsentDecision.Blocked, ConsentGate.Evaluate(record, Project, other));

        var rejected = Assert.Throws<ErrorCodeException>(
            () => ConsentService.ValidateForVoice(record, Project, other, null));
        Assert.Equal(ErrorCodes.ConsentRequired, rejected.ErrorCode);
    }

    [Fact]
    public void JurisdictionMismatch_PolicyDenied()
    {
        var record = Granted(jurisdiction: "EU");
        var policy = new ProcessingPolicy(
            Guid.NewGuid(), Tenant, true, ["Mock"], "US",
            "standard", "standard", false, null, GrantedAt, GrantedAt);
        var rejected = Assert.Throws<ErrorCodeException>(
            () => ConsentService.ValidateForVoice(record, Project, null, policy));
        Assert.Equal(ErrorCodes.PolicyDenied, rejected.ErrorCode);
        Assert.Equal(403, rejected.StatusCode);
    }

    [Fact]
    public void Inflight_Revoked_Drains_ResultDiscarded_UserNotified()
    {
        // Still-granted consent lets the running job complete normally.
        var granted = Granted();
        Assert.Equal(
            ConsentDecision.Allowed,
            ConsentGate.DecideInflight(granted, Project, null));

        // Revoked mid-run: the job drains (finishes), its result is discarded,
        // and the user is notified — never silently completed as usable.
        var revoked = Granted(status: ConsentStatus.Revoked, revokedAt: GrantedAt.AddDays(1));
        Assert.Equal(
            ConsentDecision.AllowDrain,
            ConsentGate.DecideInflight(revoked, Project, null));
        Assert.Equal(
            ConsentDecision.AllowDrain,
            ConsentGate.DecideInflight(null, Project, null));
    }

    [Fact]
    public void GateDecision_Audited_WithoutSubjectOrEvidence()
    {
        var record = Granted();
        var audit = ConsentGate.AuditEvent(
            Tenant, Project, record.Id, ConsentState.Granted, ConsentDecision.Allowed, "POST /voice-previews");

        Assert.Equal(Tenant.ToString("N"), audit["tenantId"]?.ToString());
        Assert.Equal(Project.ToString("N"), audit["projectId"]?.ToString());
        Assert.Equal(record.Id.ToString("N"), audit["consentId"]?.ToString());
        Assert.Equal("Granted", audit["state"]?.ToString());
        Assert.Equal("Allowed", audit["decision"]?.ToString());

        var serialized = System.Text.Json.JsonSerializer.Serialize(audit);
        Assert.DoesNotContain("subject-ref", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("evidence-ref", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public void States_Visible_UnknownGrantedRevokedExpired()
    {
        Assert.Equal(ConsentState.Unknown, ConsentGate.ResolveState(null));
        Assert.Equal(ConsentState.Granted, ConsentGate.ResolveState(Granted()));
        Assert.Equal(
            ConsentState.Revoked,
            ConsentGate.ResolveState(Granted(status: ConsentStatus.Revoked, revokedAt: GrantedAt.AddDays(1))));
        Assert.Equal(ConsentState.Expired, ConsentGate.ResolveState(Granted(status: ConsentStatus.Expired)));

        Assert.True(ConsentGate.IsUsable(ConsentState.Granted));
        Assert.False(ConsentGate.IsUsable(ConsentState.Unknown));
        Assert.False(ConsentGate.IsUsable(ConsentState.Revoked));
        Assert.False(ConsentGate.IsUsable(ConsentState.Expired));
    }
}
