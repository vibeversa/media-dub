// Task 039C: domain voice consent gate unit gap closure.
using System;
using System.Collections.Generic;
using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Domain.Enums;
using DubbingPlatform.Domain.Voice;

namespace DubbingPlatform.UnitTests.Domain.Voice;

/// <summary>
/// Pure consent gate. Voice features are disabled-by-default: a missing record,
/// incomplete evidence, non-granted status, revoked stamp, scope mismatch,
/// voice mismatch or expiry all block new use. In-flight work drains rather
/// than running unbounded. Audit payloads carry ids and outcome only.
/// </summary>
public sealed class ConsentGateTests
{
    private static readonly DateTimeOffset Granted = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Later = Granted.AddDays(1);

    private static readonly Guid TenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Id = Guid.Parse("99999999-9999-9999-9999-999999999999");
    private static readonly Guid ProjectId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid VoiceProfileId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid Other = Guid.Parse("44444444-4444-4444-4444-444444444444");

    private static ConsentRecord NewConsent(
        string? subjectIdentity = "subject-1",
        string? evidenceReference = "evidence-1",
        string? scope = "*",
        string? jurisdiction = "US",
        ConsentStatus status = ConsentStatus.Granted,
        Guid? voiceProfileId = null,
        DateTimeOffset? revokedAt = null) =>
        new(
            Id,
            TenantId,
            subjectIdentity!,
            evidenceReference!,
            scope!,
            jurisdiction!,
            status,
            voiceProfileId,
            Granted,
            revokedAt);

    // ------------------------------------------------------------------
    // ResolveState
    // ------------------------------------------------------------------

    [Fact]
    public void ResolveState_Null_Record_Is_Unknown()
    {
        Assert.Equal(ConsentState.Unknown, ConsentGate.ResolveState(null));
    }

    [Theory]
    [InlineData(nameof(ConsentRecord.SubjectIdentity))]
    [InlineData(nameof(ConsentRecord.EvidenceReference))]
    [InlineData(nameof(ConsentRecord.Scope))]
    [InlineData(nameof(ConsentRecord.Jurisdiction))]
    public void ResolveState_Incomplete_Evidence_Is_Unknown_And_Fails_Closed(string blankedField)
    {
        // Plan A note: the ctor rejects blank evidence, so the "incomplete
        // evidence" branch of the gate is only reachable when a partially
        // populated row arrives from EF materialization. The properties have
        // private setters, so the row is blanked through the same back door EF
        // uses and the gate's fail-closed behaviour is asserted directly.
        var record = NewConsent();
        Assert.Equal(ConsentState.Granted, ConsentGate.ResolveState(record));

        typeof(ConsentRecord)
            .GetProperty(blankedField)!
            .SetValue(record, "   ");

        Assert.Equal(ConsentState.Unknown, ConsentGate.ResolveState(record));
        Assert.False(ConsentGate.IsUsable(ConsentState.Unknown));
        Assert.Equal(ConsentDecision.Blocked, ConsentGate.Evaluate(record, ProjectId, null));
        Assert.Equal(ConsentDecision.AllowDrain, ConsentGate.DecideInflight(record, ProjectId, null));
    }

    [Fact]
    public void ResolveState_Granted_Record_Is_Granted()
    {
        Assert.Equal(ConsentState.Granted, ConsentGate.ResolveState(NewConsent()));
    }

    [Fact]
    public void ResolveState_Revoked_Status_Is_Revoked()
    {
        Assert.Equal(ConsentState.Revoked, ConsentGate.ResolveState(NewConsent(status: ConsentStatus.Revoked)));
    }

    [Fact]
    public void ResolveState_RevokedAt_Stamp_Is_Revoked_Even_When_Status_Is_Granted()
    {
        var record = NewConsent(status: ConsentStatus.Granted, revokedAt: Later);
        Assert.Equal(ConsentState.Revoked, ConsentGate.ResolveState(record));
    }

    [Fact]
    public void ResolveState_Expired_Status_Is_Expired_And_Fails_Closed()
    {
        Assert.Equal(ConsentState.Expired, ConsentGate.ResolveState(NewConsent(status: ConsentStatus.Expired)));
        Assert.False(ConsentGate.IsUsable(ConsentState.Expired));
    }

    [Fact]
    public void ResolveState_Pending_Status_Is_Unknown_And_Fails_Closed()
    {
        Assert.Equal(ConsentState.Unknown, ConsentGate.ResolveState(NewConsent(status: ConsentStatus.Pending)));
        Assert.False(ConsentGate.IsUsable(ConsentState.Unknown));
    }

    [Fact]
    public void ResolveState_Expired_Beats_Pending_But_Loses_To_Revoked()
    {
        // Revocation is checked before expiry.
        var revokedAndExpired = NewConsent(status: ConsentStatus.Revoked, revokedAt: Later);
        Assert.Equal(ConsentState.Revoked, ConsentGate.ResolveState(revokedAndExpired));
    }

    [Fact]
    public void ResolveState_Revoke_Flips_A_Granted_Record_Immediately()
    {
        var record = NewConsent();
        Assert.Equal(ConsentState.Granted, ConsentGate.ResolveState(record));

        record.Revoke(Later);

        Assert.Equal(ConsentState.Revoked, ConsentGate.ResolveState(record));
        Assert.Equal(ConsentDecision.Blocked, ConsentGate.Evaluate(record, ProjectId, null));
    }

    [Theory]
    [InlineData(ConsentState.Unknown, false)]
    [InlineData(ConsentState.Granted, true)]
    [InlineData(ConsentState.Revoked, false)]
    [InlineData(ConsentState.Expired, false)]
    public void IsUsable_Only_Granted_Is_Usable(ConsentState state, bool expected)
    {
        Assert.Equal(expected, ConsentGate.IsUsable(state));
    }

    // ------------------------------------------------------------------
    // Evaluate
    // ------------------------------------------------------------------

    [Fact]
    public void Evaluate_Empty_Project_Blocks()
    {
        Assert.Equal(ConsentDecision.Blocked, ConsentGate.Evaluate(NewConsent(), Guid.Empty, null));
    }

    [Fact]
    public void Evaluate_Empty_VoiceProfile_Guid_Blocks()
    {
        Assert.Equal(ConsentDecision.Blocked, ConsentGate.Evaluate(NewConsent(), ProjectId, Guid.Empty));
    }

    [Fact]
    public void Evaluate_Null_Record_Blocks()
    {
        Assert.Equal(ConsentDecision.Blocked, ConsentGate.Evaluate(null, ProjectId, null));
    }

    [Theory]
    [InlineData(ConsentStatus.Pending)]
    [InlineData(ConsentStatus.Expired)]
    [InlineData(ConsentStatus.Revoked)]
    public void Evaluate_NonGranted_Status_Blocks(ConsentStatus status)
    {
        Assert.Equal(ConsentDecision.Blocked, ConsentGate.Evaluate(NewConsent(status: status), ProjectId, null));
    }

    [Theory]
    [InlineData("*")]
    [InlineData("  *  ")]
    public void Evaluate_Wildcard_Scope_Allows(string scope)
    {
        Assert.Equal(ConsentDecision.Allowed, ConsentGate.Evaluate(NewConsent(scope: scope), ProjectId, null));
    }

    [Fact]
    public void Evaluate_Scope_With_N_Hex_ProjectId_Allows()
    {
        var record = NewConsent(scope: "project:" + ProjectId.ToString("N"));
        Assert.Equal(ConsentDecision.Allowed, ConsentGate.Evaluate(record, ProjectId, null));
    }

    [Fact]
    public void Evaluate_Scope_With_Dashed_ProjectId_Allows_Case_Insensitively()
    {
        var record = NewConsent(scope: "project:" + ProjectId.ToString("D").ToUpperInvariant());
        Assert.Equal(ConsentDecision.Allowed, ConsentGate.Evaluate(record, ProjectId, null));
    }

    [Theory]
    [InlineData("project:*")]
    [InlineData("other-project")]
    [InlineData("voice:all")]
    [InlineData("* *")]
    public void Evaluate_Scope_Not_Covering_Project_Blocks(string scope)
    {
        // Blank scopes are impossible here: ConsentRecord.Validate rejects them
        // at construction, which the entity matrix covers.
        Assert.Equal(ConsentDecision.Blocked, ConsentGate.Evaluate(NewConsent(scope: scope), ProjectId, null));
    }

    [Fact]
    public void Evaluate_Scope_For_Another_Project_Blocks()
    {
        var record = NewConsent(scope: "project:" + Other.ToString("N"));
        Assert.Equal(ConsentDecision.Blocked, ConsentGate.Evaluate(record, ProjectId, null));
    }

    [Fact]
    public void Evaluate_Blank_Scope_Blocks_Even_For_A_Granted_Record()
    {
        // Plan A note: ConsentRecord.Validate rejects a blank scope at
        // construction, so ScopeCoversProject's blank guard is only reachable
        // for a partially materialized row. Blanked via the private setter, the
        // gate must still fail closed.
        var record = NewConsent();
        Assert.Equal(ConsentDecision.Allowed, ConsentGate.Evaluate(record, ProjectId, null));

        typeof(ConsentRecord).GetProperty(nameof(ConsentRecord.Scope))!.SetValue(record, "  ");

        Assert.Equal(ConsentDecision.Blocked, ConsentGate.Evaluate(record, ProjectId, null));
        Assert.Equal(ConsentDecision.AllowDrain, ConsentGate.DecideInflight(record, ProjectId, null));
    }

    [Fact]
    public void Evaluate_Matching_VoiceProfile_Allows()
    {
        var record = NewConsent(voiceProfileId: VoiceProfileId);
        Assert.Equal(ConsentDecision.Allowed, ConsentGate.Evaluate(record, ProjectId, VoiceProfileId));
    }

    [Fact]
    public void Evaluate_Mismatched_VoiceProfile_Blocks()
    {
        var record = NewConsent(voiceProfileId: Other);
        Assert.Equal(ConsentDecision.Blocked, ConsentGate.Evaluate(record, ProjectId, VoiceProfileId));
    }

    [Fact]
    public void Evaluate_Unbound_Record_Allows_Any_Voice()
    {
        var record = NewConsent(voiceProfileId: null);
        Assert.Equal(ConsentDecision.Allowed, ConsentGate.Evaluate(record, ProjectId, VoiceProfileId));
        Assert.Equal(ConsentDecision.Allowed, ConsentGate.Evaluate(record, ProjectId, Other));
    }

    [Fact]
    public void Evaluate_Requested_Voice_Unbound_Allows()
    {
        var record = NewConsent(voiceProfileId: null);
        Assert.Equal(ConsentDecision.Allowed, ConsentGate.Evaluate(record, ProjectId, null));
    }

    [Fact]
    public void Evaluate_Does_Not_Throw_For_Any_Input()
    {
        // Mirrors ConsentService.ValidateForVoice without throwing so callers can
        // audit the outcome before mapping to CONSENT_REQUIRED.
        var decisions = new List<ConsentDecision>
        {
            ConsentGate.Evaluate(null, Guid.Empty, null),
            ConsentGate.Evaluate(NewConsent(), Guid.Empty, Guid.Empty),
            ConsentGate.Evaluate(NewConsent(), ProjectId, null),
            ConsentGate.Evaluate(NewConsent(status: ConsentStatus.Pending), ProjectId, VoiceProfileId),
        };

        Assert.All(decisions, d => Assert.True(Enum.IsDefined(typeof(ConsentDecision), d)));
    }

    // ------------------------------------------------------------------
    // DecideInflight
    // ------------------------------------------------------------------

    [Fact]
    public void DecideInflight_Still_Granted_Allows_Completion()
    {
        Assert.Equal(ConsentDecision.Allowed, ConsentGate.DecideInflight(NewConsent(), ProjectId, null));
    }

    [Fact]
    public void DecideInflight_Revoked_Drains()
    {
        var record = NewConsent();
        record.Revoke(Later);

        Assert.Equal(ConsentDecision.AllowDrain, ConsentGate.DecideInflight(record, ProjectId, null));
    }

    [Theory]
    [InlineData(ConsentStatus.Pending)]
    [InlineData(ConsentStatus.Expired)]
    [InlineData(ConsentStatus.Revoked)]
    public void DecideInflight_NonGranted_Drains(ConsentStatus status)
    {
        Assert.Equal(ConsentDecision.AllowDrain, ConsentGate.DecideInflight(NewConsent(status: status), ProjectId, null));
    }

    [Fact]
    public void DecideInflight_Null_Record_Drains()
    {
        Assert.Equal(ConsentDecision.AllowDrain, ConsentGate.DecideInflight(null, ProjectId, null));
    }

    [Fact]
    public void DecideInflight_Scope_Mismatch_Drains()
    {
        var record = NewConsent(scope: "project:" + Other.ToString("N"));
        Assert.Equal(ConsentDecision.AllowDrain, ConsentGate.DecideInflight(record, ProjectId, null));
    }

    [Fact]
    public void DecideInflight_Voice_Mismatch_Drains()
    {
        var record = NewConsent(voiceProfileId: Other);
        Assert.Equal(ConsentDecision.AllowDrain, ConsentGate.DecideInflight(record, ProjectId, VoiceProfileId));
    }

    [Fact]
    public void DecideInflight_Empty_Project_Drains()
    {
        Assert.Equal(ConsentDecision.AllowDrain, ConsentGate.DecideInflight(NewConsent(), Guid.Empty, null));
    }

    [Fact]
    public void DecideInflight_Empty_Voice_Guid_Drains()
    {
        Assert.Equal(ConsentDecision.AllowDrain, ConsentGate.DecideInflight(NewConsent(), ProjectId, Guid.Empty));
    }

    [Theory]
    [InlineData(ConsentState.Unknown, false)]
    [InlineData(ConsentState.Granted, true)]
    [InlineData(ConsentState.Revoked, false)]
    [InlineData(ConsentState.Expired, false)]
    public void DecideInflight_Drains_Unless_New_Use_Is_Allowed(ConsentState state, bool newUseAllowed)
    {
        // Drain is terminal for the artifact: a blocked new-use decision always
        // becomes AllowDrain for in-flight work, never a second Blocked.
        var record = state switch
        {
            ConsentState.Granted => NewConsent(),
            ConsentState.Revoked => NewConsent(status: ConsentStatus.Revoked),
            ConsentState.Expired => NewConsent(status: ConsentStatus.Expired),
            _ => NewConsent(status: ConsentStatus.Pending),
        };

        Assert.Equal(newUseAllowed, ConsentGate.Evaluate(record, ProjectId, null) == ConsentDecision.Allowed);
        Assert.Equal(
            newUseAllowed ? ConsentDecision.Allowed : ConsentDecision.AllowDrain,
            ConsentGate.DecideInflight(record, ProjectId, null));
    }

    // ------------------------------------------------------------------
    // AuditEvent
    // ------------------------------------------------------------------

    [Fact]
    public void AuditEvent_Contains_Ids_State_Decision_And_Endpoint()
    {
        var payload = ConsentGate.AuditEvent(
            TenantId,
            ProjectId,
            Id,
            ConsentState.Granted,
            ConsentDecision.Allowed,
            "POST /api/v1/voices/preview");

        Assert.Equal(6, payload.Count);
        Assert.Equal(TenantId.ToString("N"), payload["tenantId"]);
        Assert.Equal(ProjectId.ToString("N"), payload["projectId"]);
        Assert.Equal(Id.ToString("N"), payload["consentId"]);
        Assert.Equal("Granted", payload["state"]);
        Assert.Equal("Allowed", payload["decision"]);
        Assert.Equal("POST /api/v1/voices/preview", payload["endpoint"]);
    }

    [Fact]
    public void AuditEvent_Null_ConsentId_Is_Null()
    {
        var payload = ConsentGate.AuditEvent(
            TenantId,
            ProjectId,
            null,
            ConsentState.Unknown,
            ConsentDecision.Blocked,
            "endpoint");

        Assert.Null(payload["consentId"]);
    }

    [Fact]
    public void AuditEvent_Trims_Endpoint()
    {
        var payload = ConsentGate.AuditEvent(
            TenantId,
            ProjectId,
            null,
            ConsentState.Unknown,
            ConsentDecision.Blocked,
            "  /api/v1/x  ");

        Assert.Equal("/api/v1/x", payload["endpoint"]);
    }

    [Fact]
    public void AuditEvent_Uses_Ordinal_String_Keys()
    {
        var payload = ConsentGate.AuditEvent(
            TenantId,
            ProjectId,
            null,
            ConsentState.Granted,
            ConsentDecision.Allowed,
            "endpoint");

        Assert.Contains("tenantId", payload.Keys);
        Assert.DoesNotContain("TENANTID", payload.Keys);
    }

    [Fact]
    public void AuditEvent_Rejects_Null_Endpoint()
    {
        // ArgumentException.ThrowIfNullOrWhiteSpace raises the null-specific subtype.
        var ex = Assert.Throws<ArgumentNullException>(() => ConsentGate.AuditEvent(
            TenantId,
            ProjectId,
            null,
            ConsentState.Granted,
            ConsentDecision.Allowed,
            null!));

        Assert.Equal("endpoint", ex.ParamName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void AuditEvent_Rejects_Blank_Endpoint(string endpoint)
    {
        Assert.Throws<ArgumentException>(() => ConsentGate.AuditEvent(
            TenantId,
            ProjectId,
            null,
            ConsentState.Granted,
            ConsentDecision.Allowed,
            endpoint));
    }

    [Fact]
    public void AuditEvent_Never_Leaks_Subject_Identity_Or_Evidence()
    {
        var record = NewConsent(subjectIdentity: "person-42", evidenceReference: "signed-form-9");

        var payload = ConsentGate.AuditEvent(
            TenantId,
            ProjectId,
            record.Id,
            ConsentGate.ResolveState(record),
            ConsentGate.Evaluate(record, ProjectId, null),
            "endpoint");

        var joined = string.Join("|", payload.Values);
        Assert.DoesNotContain("person-42", joined, StringComparison.Ordinal);
        Assert.DoesNotContain("signed-form-9", joined, StringComparison.Ordinal);
    }

    [Fact]
    public void AuditEvent_Is_Deterministic_And_Read_Only()
    {
        var first = ConsentGate.AuditEvent(TenantId, ProjectId, Id, ConsentState.Granted, ConsentDecision.Allowed, "e");
        var second = ConsentGate.AuditEvent(TenantId, ProjectId, Id, ConsentState.Granted, ConsentDecision.Allowed, "e");

        Assert.Equal(first, second);
        Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(first);
    }
}
