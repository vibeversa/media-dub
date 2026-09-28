// Task 039C: domain entity unit gap closure.
using System;
using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Domain.Enums;
using DubbingPlatform.Domain.Exceptions;

namespace DubbingPlatform.UnitTests.Domain.Entities;

/// <summary>
/// Guard and lifecycle matrix for <see cref="VoicePreviewJob"/>.
/// Pending -> Running -> Completed/Failed, Pending/Running -> Cancelled.
/// Illegal transitions must throw <c>DomainException</c> carrying the
/// <c>PREVIEW_STATE_CONFLICT</c> marker (never a bare invalid-operation throw).
/// All timestamps are frozen.
/// </summary>
public sealed class VoicePreviewJobTests
{
    private static readonly DateTimeOffset Created = new(2026, 5, 1, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Started = Created.AddSeconds(1);
    private static readonly DateTimeOffset Completed = Created.AddSeconds(5);

    private static readonly Guid TenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Id = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid ProjectId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid SpeakerId = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private static readonly Guid UserId = Guid.Parse("66666666-6666-6666-6666-666666666666");
    private static readonly Guid ExecutionId = Guid.Parse("77777777-7777-7777-7777-777777777777");
    private static readonly Guid ArtifactId = Guid.Parse("88888888-8888-8888-8888-888888888888");

    private static string Repeat(char c, int count) => new(c, count);

    private static VoicePreviewJob NewJob(
        Guid? id = null,
        Guid? tenantId = null,
        Guid? projectId = null,
        Guid? speakerId = null,
        string? voiceId = "en-US-JennyNeural",
        string? text = "preview text",
        VoicePreviewStatus status = VoicePreviewStatus.Pending,
        Guid? requestedByUserId = null,
        string? idempotencyKey = null,
        VoicePreviewQuotaCheck quotaCheck = VoicePreviewQuotaCheck.Allowed,
        string? quotaCheckReason = null,
        VoicePreviewConsentState consentState = VoicePreviewConsentState.Verified,
        Guid? providerExecutionId = null,
        Guid? artifactId = null,
        string? errorCode = null,
        string? errorMessage = null,
        DateTimeOffset? createdAt = null,
        DateTimeOffset? startedAt = null,
        DateTimeOffset? completedAt = null) =>
        new(
            id ?? Id,
            tenantId ?? TenantId,
            projectId ?? ProjectId,
            speakerId ?? SpeakerId,
            voiceId!,
            text!,
            status,
            requestedByUserId ?? UserId,
            idempotencyKey,
            quotaCheck,
            quotaCheckReason,
            consentState,
            providerExecutionId,
            artifactId,
            errorCode,
            errorMessage,
            createdAt ?? Created,
            startedAt,
            completedAt);

    // ------------------------------------------------------------------
    // Construction / guards
    // ------------------------------------------------------------------

    [Fact]
    public void VoicePreviewJob_Valid_Construction_Keeps_All_Values()
    {
        var sut = NewJob(status: VoicePreviewStatus.Running, startedAt: Started, quotaCheckReason: "ok");

        Assert.Equal(Id, sut.Id);
        Assert.Equal(TenantId, sut.TenantId);
        Assert.Equal(ProjectId, sut.ProjectId);
        Assert.Equal(SpeakerId, sut.SpeakerId);
        Assert.Equal("en-US-JennyNeural", sut.VoiceId);
        Assert.Equal("preview text", sut.Text);
        Assert.Equal(VoicePreviewStatus.Running, sut.Status);
        Assert.Equal(UserId, sut.RequestedByUserId);
        Assert.Equal(VoicePreviewQuotaCheck.Allowed, sut.QuotaCheck);
        Assert.Equal("ok", sut.QuotaCheckReason);
        Assert.Equal(VoicePreviewConsentState.Verified, sut.ConsentState);
        Assert.Equal(Started, sut.StartedAt);
        Assert.Equal(Created, sut.CreatedAt);
    }

    [Theory]
    [InlineData(0, "VoicePreviewJob Id must not be empty.")]
    [InlineData(1, "VoicePreviewJob TenantId must not be empty.")]
    [InlineData(2, "VoicePreviewJob ProjectId must not be empty.")]
    [InlineData(3, "VoicePreviewJob SpeakerId must not be empty.")]
    [InlineData(4, "VoicePreviewJob VoiceId must not be empty.")]
    [InlineData(5, "VoicePreviewJob VoiceId must be at most 256 chars.")]
    [InlineData(6, "VoicePreviewJob Text must not be empty.")]
    [InlineData(8, "VoicePreviewJob Status is not defined.")]
    [InlineData(9, "VoicePreviewJob RequestedByUserId must not be empty.")]
    [InlineData(10, "VoicePreviewJob QuotaCheck is not defined.")]
    [InlineData(12, "VoicePreviewJob ConsentState is not defined.")]
    [InlineData(13, "VoicePreviewJob ProviderExecutionId must not be empty when set.")]
    [InlineData(14, "VoicePreviewJob ArtifactId must not be empty when set.")]
    [InlineData(16, "VoicePreviewJob StartedAt must not be before CreatedAt.")]
    [InlineData(17, "VoicePreviewJob CompletedAt must not be before StartedAt.")]
    [InlineData(18, "VoicePreviewJob IdempotencyKey must be at most 128 chars.")]
    public void VoicePreviewJob_Rejects_Invalid_Input(int slot, string expected)
    {
        Action act = slot switch
        {
            0 => () => NewJob(id: Guid.Empty),
            1 => () => NewJob(tenantId: Guid.Empty),
            2 => () => NewJob(projectId: Guid.Empty),
            3 => () => NewJob(speakerId: Guid.Empty),
            4 => () => NewJob(voiceId: "   "),
            5 => () => NewJob(voiceId: Repeat('v', VoicePreviewJob.MaxVoiceIdLength + 1)),
            6 => () => NewJob(text: "\t "),
            8 => () => NewJob(status: (VoicePreviewStatus)99),
            9 => () => NewJob(requestedByUserId: Guid.Empty),
            10 => () => NewJob(quotaCheck: (VoicePreviewQuotaCheck)7),
            12 => () => NewJob(consentState: (VoicePreviewConsentState)3),
            13 => () => NewJob(providerExecutionId: Guid.Empty),
            14 => () => NewJob(artifactId: Guid.Empty),
            16 => () => NewJob(startedAt: Created.AddTicks(-1)),
            17 => () => NewJob(startedAt: Started, completedAt: Started.AddTicks(-1)),
            18 => () => NewJob(idempotencyKey: Repeat('k', VoicePreviewJob.MaxIdempotencyKeyLength + 1)),
            _ => throw new InvalidOperationException($"Unhandled slot {slot}."),
        };

        var ex = Assert.Throws<DomainException>(act);
        Assert.Equal(expected, ex.Message);
    }

    [Fact]
    public void VoicePreviewJob_QuotaCheckReason_Blank_Becomes_Null()
    {
        // Normalization runs before Validate(), so a blank reason never reaches
        // the "must not be empty when set" guard.
        Assert.Null(NewJob(quotaCheckReason: "   ").QuotaCheckReason);
        Assert.Null(NewJob(quotaCheckReason: string.Empty).QuotaCheckReason);
    }

    [Fact]
    public void VoicePreviewJob_QuotaCheckReason_Is_Trimmed()
    {
        Assert.Equal("denied by quota", NewJob(quotaCheckReason: "  denied by quota  ").QuotaCheckReason);
    }

    [Fact]
    public void VoicePreviewJob_ErrorCode_Blank_Becomes_Null_And_Non_Blank_Is_Trimmed()
    {
        Assert.Null(NewJob(errorCode: "   ").ErrorCode);
        Assert.Equal("PREVIEW_TEXT_INVALID", NewJob(errorCode: " PREVIEW_TEXT_INVALID ").ErrorCode);
    }

    [Fact]
    public void VoicePreviewJob_ErrorMessage_Is_Truncated_To_Max()
    {
        var sut = NewJob(errorMessage: Repeat('e', VoicePreviewJob.MaxErrorLength + 50));

        Assert.NotNull(sut.ErrorMessage);
        Assert.Equal(VoicePreviewJob.MaxErrorLength, sut.ErrorMessage!.Length);
    }

    [Fact]
    public void VoicePreviewJob_ErrorMessage_Blank_Becomes_Null()
    {
        Assert.Null(NewJob(errorMessage: "   ").ErrorMessage);
        Assert.Null(NewJob(errorMessage: null).ErrorMessage);
    }

    [Fact]
    public void VoicePreviewJob_Text_Is_Trimmed_And_Truncated_To_Max()
    {
        Assert.Equal("hello", NewJob(text: "  hello  ").Text);

        var long1 = NewJob(text: Repeat('t', VoicePreviewJob.MaxTextLength + 1));
        Assert.Equal(VoicePreviewJob.MaxTextLength, long1.Text.Length);
    }

    [Fact]
    public void VoicePreviewJob_Text_At_Max_Length_Is_Not_Truncated()
    {
        var sut = NewJob(text: Repeat('t', VoicePreviewJob.MaxTextLength));
        Assert.Equal(VoicePreviewJob.MaxTextLength, sut.Text.Length);
    }

    [Fact]
    public void VoicePreviewJob_VoiceId_Is_Trimmed_And_Bounded()
    {
        Assert.Equal("voice-1", NewJob(voiceId: "  voice-1 ").VoiceId);
        Assert.Equal(VoicePreviewJob.MaxVoiceIdLength, NewJob(voiceId: Repeat('v', VoicePreviewJob.MaxVoiceIdLength)).VoiceId.Length);
    }

    [Fact]
    public void VoicePreviewJob_Null_Text_And_VoiceId_Normalize_To_Empty_Then_Throw()
    {
        Assert.Throws<DomainException>(() => NewJob(voiceId: null!));
        Assert.Throws<DomainException>(() => NewJob(text: null!));
    }

    [Fact]
    public void VoicePreviewJob_IdempotencyKey_Blank_Becomes_Null()
    {
        Assert.Null(NewJob(idempotencyKey: null).IdempotencyKey);
        Assert.Null(NewJob(idempotencyKey: "   ").IdempotencyKey);
        Assert.Equal("key-1", NewJob(idempotencyKey: " key-1 ").IdempotencyKey);
    }

    [Fact]
    public void VoicePreviewJob_Quota_Denied_And_Consent_Blocked_Are_Persisted_As_Failed_Rows()
    {
        var denied = NewJob(
            status: VoicePreviewStatus.Failed,
            quotaCheck: VoicePreviewQuotaCheck.Denied,
            quotaCheckReason: "quota exhausted",
            consentState: VoicePreviewConsentState.Verified,
            errorCode: "QUOTA_EXCEEDED",
            errorMessage: "quota exhausted",
            completedAt: Completed);

        Assert.Equal(VoicePreviewStatus.Failed, denied.Status);
        Assert.Equal(VoicePreviewQuotaCheck.Denied, denied.QuotaCheck);
        Assert.Null(denied.ProviderExecutionId);
        Assert.Null(denied.ArtifactId);

        var blocked = NewJob(
            status: VoicePreviewStatus.Failed,
            consentState: VoicePreviewConsentState.Blocked,
            errorCode: "CONSENT_REQUIRED",
            completedAt: Completed);

        Assert.Equal(VoicePreviewConsentState.Blocked, blocked.ConsentState);
        Assert.Null(blocked.ArtifactId);
    }

    [Theory]
    [InlineData(VoicePreviewQuotaCheck.Allowed)]
    [InlineData(VoicePreviewQuotaCheck.Denied)]
    public void VoicePreviewJob_Accepts_All_QuotaChecks(VoicePreviewQuotaCheck quotaCheck)
    {
        Assert.Equal(quotaCheck, NewJob(quotaCheck: quotaCheck).QuotaCheck);
    }

    [Theory]
    [InlineData(VoicePreviewConsentState.Verified)]
    [InlineData(VoicePreviewConsentState.Blocked)]
    public void VoicePreviewJob_Accepts_All_ConsentStates(VoicePreviewConsentState consentState)
    {
        Assert.Equal(consentState, NewJob(consentState: consentState).ConsentState);
    }

    // ------------------------------------------------------------------
    // IsTerminal
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(VoicePreviewStatus.Pending, false)]
    [InlineData(VoicePreviewStatus.Running, false)]
    [InlineData(VoicePreviewStatus.Completed, true)]
    [InlineData(VoicePreviewStatus.Failed, true)]
    [InlineData(VoicePreviewStatus.Cancelled, true)]
    public void VoicePreviewJob_IsTerminal_Reflects_Status(VoicePreviewStatus status, bool expected)
    {
        Assert.Equal(expected, NewJob(status: status).IsTerminal);
    }

    // ------------------------------------------------------------------
    // MarkRunning
    // ------------------------------------------------------------------

    [Fact]
    public void VoicePreviewJob_MarkRunning_Transitions_Pending_To_Running()
    {
        var sut = NewJob();

        sut.MarkRunning(Started);

        Assert.Equal(VoicePreviewStatus.Running, sut.Status);
        Assert.Equal(Started, sut.StartedAt);
        Assert.False(sut.IsTerminal);
    }

    [Fact]
    public void VoicePreviewJob_MarkRunning_At_CreatedAt_Is_Allowed()
    {
        var sut = NewJob();
        sut.MarkRunning(Created);
        Assert.Equal(Created, sut.StartedAt);
    }

    [Fact]
    public void VoicePreviewJob_MarkRunning_Before_Created_Throws()
    {
        var sut = NewJob();

        var ex = Assert.Throws<DomainException>(() => sut.MarkRunning(Created.AddTicks(-1)));
        Assert.Equal("VoicePreviewJob StartedAt must not be before CreatedAt.", ex.Message);
        Assert.Equal(VoicePreviewStatus.Pending, sut.Status);
    }

    [Theory]
    [InlineData(VoicePreviewStatus.Running)]
    [InlineData(VoicePreviewStatus.Completed)]
    [InlineData(VoicePreviewStatus.Failed)]
    [InlineData(VoicePreviewStatus.Cancelled)]
    public void VoicePreviewJob_MarkRunning_From_NonPending_Throws_StateConflict(VoicePreviewStatus status)
    {
        var sut = NewJob(status: status, startedAt: status == VoicePreviewStatus.Pending ? null : Started);

        var ex = Assert.Throws<DomainException>(() => sut.MarkRunning(Started.AddSeconds(1)));
        Assert.Contains("PREVIEW_STATE_CONFLICT", ex.Message, StringComparison.Ordinal);
        Assert.Equal(status, sut.Status);
    }

    // ------------------------------------------------------------------
    // MarkCompleted
    // ------------------------------------------------------------------

    [Fact]
    public void VoicePreviewJob_MarkCompleted_Links_Artifact_And_Execution()
    {
        var sut = NewJob(status: VoicePreviewStatus.Running, startedAt: Started);

        sut.MarkCompleted(ArtifactId, ExecutionId, Completed);

        Assert.Equal(VoicePreviewStatus.Completed, sut.Status);
        Assert.Equal(ArtifactId, sut.ArtifactId);
        Assert.Equal(ExecutionId, sut.ProviderExecutionId);
        Assert.Equal(Completed, sut.CompletedAt);
        Assert.True(sut.IsTerminal);
    }

    [Theory]
    [InlineData(VoicePreviewStatus.Pending)]
    [InlineData(VoicePreviewStatus.Completed)]
    [InlineData(VoicePreviewStatus.Failed)]
    [InlineData(VoicePreviewStatus.Cancelled)]
    public void VoicePreviewJob_MarkCompleted_From_NonRunning_Throws_StateConflict(VoicePreviewStatus status)
    {
        var sut = NewJob(status: status, startedAt: status == VoicePreviewStatus.Pending ? null : Started);

        var ex = Assert.Throws<DomainException>(() => sut.MarkCompleted(ArtifactId, ExecutionId, Completed));
        Assert.Contains("PREVIEW_STATE_CONFLICT", ex.Message, StringComparison.Ordinal);
        Assert.Null(sut.ArtifactId);
    }

    [Fact]
    public void VoicePreviewJob_MarkCompleted_Rejects_Empty_Guids()
    {
        var sut = NewJob(status: VoicePreviewStatus.Running, startedAt: Started);

        Assert.Equal(
            "VoicePreviewJob ArtifactId must not be empty.",
            Assert.Throws<DomainException>(() => sut.MarkCompleted(Guid.Empty, ExecutionId, Completed)).Message);
        Assert.Equal(
            "VoicePreviewJob ProviderExecutionId must not be empty.",
            Assert.Throws<DomainException>(() => sut.MarkCompleted(ArtifactId, Guid.Empty, Completed)).Message);
        Assert.Equal(VoicePreviewStatus.Running, sut.Status);
    }

    [Fact]
    public void VoicePreviewJob_MarkCompleted_Rejects_Completion_Before_Start()
    {
        var sut = NewJob(status: VoicePreviewStatus.Running, startedAt: Started);

        var ex = Assert.Throws<DomainException>(() => sut.MarkCompleted(ArtifactId, ExecutionId, Started.AddTicks(-1)));
        Assert.Equal("VoicePreviewJob CompletedAt must not be before StartedAt.", ex.Message);
    }

    // ------------------------------------------------------------------
    // MarkFailed
    // ------------------------------------------------------------------

    [Fact]
    public void VoicePreviewJob_MarkFailed_Records_Code_And_Truncated_Message()
    {
        var sut = NewJob(status: VoicePreviewStatus.Running, startedAt: Started);

        sut.MarkFailed("  PROVIDER_TIMEOUT  ", Repeat('m', VoicePreviewJob.MaxErrorLength + 1), Completed);

        Assert.Equal(VoicePreviewStatus.Failed, sut.Status);
        Assert.Equal("PROVIDER_TIMEOUT", sut.ErrorCode);
        Assert.Equal(VoicePreviewJob.MaxErrorLength, sut.ErrorMessage!.Length);
        Assert.Equal(Completed, sut.CompletedAt);
        Assert.True(sut.IsTerminal);
    }

    [Fact]
    public void VoicePreviewJob_MarkFailed_With_Null_Message_Nulls_ErrorMessage()
    {
        var sut = NewJob(status: VoicePreviewStatus.Running, startedAt: Started);

        sut.MarkFailed("PROVIDER_TIMEOUT", null, Completed);

        Assert.Null(sut.ErrorMessage);
        Assert.Equal("PROVIDER_TIMEOUT", sut.ErrorCode);
    }

    [Theory]
    [InlineData(VoicePreviewStatus.Pending)]
    [InlineData(VoicePreviewStatus.Completed)]
    [InlineData(VoicePreviewStatus.Cancelled)]
    public void VoicePreviewJob_MarkFailed_From_NonRunning_Throws_StateConflict(VoicePreviewStatus status)
    {
        var sut = NewJob(status: status, startedAt: status == VoicePreviewStatus.Pending ? null : Started);

        var ex = Assert.Throws<DomainException>(() => sut.MarkFailed("X", null, Completed));
        Assert.Contains("PREVIEW_STATE_CONFLICT", ex.Message, StringComparison.Ordinal);
        Assert.Null(sut.ErrorCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void VoicePreviewJob_MarkFailed_Rejects_Blank_ErrorCode(string? errorCode)
    {
        var sut = NewJob(status: VoicePreviewStatus.Running, startedAt: Started);

        var ex = Assert.Throws<DomainException>(() => sut.MarkFailed(errorCode!, null, Completed));
        Assert.Equal("VoicePreviewJob ErrorCode must not be empty.", ex.Message);
        Assert.Equal(VoicePreviewStatus.Running, sut.Status);
    }

    [Fact]
    public void VoicePreviewJob_MarkFailed_Rejects_Completion_Before_Start()
    {
        var sut = NewJob(status: VoicePreviewStatus.Running, startedAt: Started);

        var ex = Assert.Throws<DomainException>(() => sut.MarkFailed("X", null, Started.AddTicks(-1)));
        Assert.Equal("VoicePreviewJob CompletedAt must not be before StartedAt.", ex.Message);
    }

    // ------------------------------------------------------------------
    // MarkCancelled
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(VoicePreviewStatus.Pending, false)]
    [InlineData(VoicePreviewStatus.Running, true)]
    public void VoicePreviewJob_MarkCancelled_Allowed_From_Pending_And_Running(VoicePreviewStatus status, bool alreadyStarted)
    {
        var sut = NewJob(
            status: status,
            startedAt: alreadyStarted ? Started : null);

        sut.MarkCancelled(Completed);

        Assert.Equal(VoicePreviewStatus.Cancelled, sut.Status);
        Assert.Equal(Completed, sut.CompletedAt);
        Assert.True(sut.IsTerminal);
    }

    [Theory]
    [InlineData(VoicePreviewStatus.Completed)]
    [InlineData(VoicePreviewStatus.Failed)]
    [InlineData(VoicePreviewStatus.Cancelled)]
    public void VoicePreviewJob_MarkCancelled_From_Terminal_Throws_StateConflict(VoicePreviewStatus status)
    {
        var sut = NewJob(status: status, startedAt: Started);

        var ex = Assert.Throws<DomainException>(() => sut.MarkCancelled(Completed.AddMinutes(1)));
        Assert.Contains("PREVIEW_STATE_CONFLICT", ex.Message, StringComparison.Ordinal);
        Assert.Equal(status, sut.Status);
    }

    // ------------------------------------------------------------------
    // Full happy path + tenant scoping
    // ------------------------------------------------------------------

    [Fact]
    public void VoicePreviewJob_Happy_Path_Pending_Running_Completed()
    {
        var sut = NewJob(idempotencyKey: "preview-1");

        sut.MarkRunning(Started);
        sut.MarkCompleted(ArtifactId, ExecutionId, Completed);

        Assert.Equal(VoicePreviewStatus.Completed, sut.Status);
        Assert.Equal("preview-1", sut.IdempotencyKey);
        Assert.Null(sut.ErrorCode);
    }

    [Fact]
    public void VoicePreviewJob_Is_Tenant_Scoped()
    {
        var tenantA = NewJob();
        var tenantB = NewJob(tenantId: Guid.Parse("22222222-2222-2222-2222-222222222222"), id: ArtifactId);

        Assert.Equal(TenantId, tenantA.TenantId);
        Assert.NotEqual(tenantA.TenantId, tenantB.TenantId);
    }
}
