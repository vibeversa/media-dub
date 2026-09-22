using DubbingPlatform.Domain.Enums;
using DubbingPlatform.Domain.Exceptions;

namespace DubbingPlatform.Domain.Entities;

/// <summary>
/// Fast-lane voice preview job. Previews are produced outside the final dub
/// audio path so UI playback never waits for (or fails with) the pipeline.
/// Lifecycle: <c>Pending → Running → Completed/Failed</c>,
/// <c>Pending/Running → Cancelled</c>; illegal transitions throw with a
/// <c>PREVIEW_STATE_CONFLICT</c> marker. Preview text is trimmed server-side
/// and truncated to <see cref="MaxTextLength"/> (the service rejects empty or
/// over-long input with <c>PREVIEW_TEXT_INVALID</c> before constructing).
/// <c>IdempotencyKey</c> dedupes on <c>(TenantId, IdempotencyKey)</c>:
/// redeliveries return the existing row without a second provider call.
/// Quota denials and consent blocks are persisted as <c>Failed</c> rows
/// (no provider call is made) so the audit trail is complete.
/// No provider secrets are stored: only provider name, model, and latency
/// (via the linked <see cref="ProviderExecution"/>).
/// </summary>
public sealed class VoicePreviewJob
{
    /// <summary>Maximum stored preview text length; longer input is truncated.</summary>
    public const int MaxTextLength = 500;

    /// <summary>Maximum voice identifier length.</summary>
    public const int MaxVoiceIdLength = 256;

    /// <summary>Maximum idempotency key length.</summary>
    public const int MaxIdempotencyKeyLength = 128;

    /// <summary>Maximum stored error detail length.</summary>
    public const int MaxErrorLength = 1024;

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid ProjectId { get; private set; }

    public Guid SpeakerId { get; private set; }

    public string VoiceId { get; private set; }

    public string Text { get; private set; }

    public VoicePreviewStatus Status { get; private set; }

    public Guid RequestedByUserId { get; private set; }

    public string? IdempotencyKey { get; private set; }

    public VoicePreviewQuotaCheck QuotaCheck { get; private set; }

    public string? QuotaCheckReason { get; private set; }

    public VoicePreviewConsentState ConsentState { get; private set; }

    public Guid? ProviderExecutionId { get; private set; }

    public Guid? ArtifactId { get; private set; }

    public string? ErrorCode { get; private set; }

    public string? ErrorMessage { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? StartedAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    private VoicePreviewJob()
    {
        VoiceId = string.Empty;
        Text = string.Empty;
    }

    public VoicePreviewJob(
        Guid id,
        Guid tenantId,
        Guid projectId,
        Guid speakerId,
        string voiceId,
        string text,
        VoicePreviewStatus status,
        Guid requestedByUserId,
        string? idempotencyKey,
        VoicePreviewQuotaCheck quotaCheck,
        string? quotaCheckReason,
        VoicePreviewConsentState consentState,
        Guid? providerExecutionId,
        Guid? artifactId,
        string? errorCode,
        string? errorMessage,
        DateTimeOffset createdAt,
        DateTimeOffset? startedAt,
        DateTimeOffset? completedAt)
    {
        Id = id;
        TenantId = tenantId;
        ProjectId = projectId;
        SpeakerId = speakerId;
        VoiceId = voiceId?.Trim() ?? string.Empty;
        Text = Truncate(text?.Trim() ?? string.Empty);
        Status = status;
        RequestedByUserId = requestedByUserId;
        IdempotencyKey = NormalizeKey(idempotencyKey);
        QuotaCheck = quotaCheck;
        QuotaCheckReason = string.IsNullOrWhiteSpace(quotaCheckReason) ? null : quotaCheckReason.Trim();
        ConsentState = consentState;
        ProviderExecutionId = providerExecutionId;
        ArtifactId = artifactId;
        ErrorCode = string.IsNullOrWhiteSpace(errorCode) ? null : errorCode.Trim();
        ErrorMessage = TruncateError(errorMessage);
        CreatedAt = createdAt;
        StartedAt = startedAt;
        CompletedAt = completedAt;

        Validate();
    }

    public void Validate()
    {
        if (Id == Guid.Empty)
        {
            throw new DomainException("VoicePreviewJob Id must not be empty.");
        }

        if (TenantId == Guid.Empty)
        {
            throw new DomainException("VoicePreviewJob TenantId must not be empty.");
        }

        if (ProjectId == Guid.Empty)
        {
            throw new DomainException("VoicePreviewJob ProjectId must not be empty.");
        }

        if (SpeakerId == Guid.Empty)
        {
            throw new DomainException("VoicePreviewJob SpeakerId must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(VoiceId))
        {
            throw new DomainException("VoicePreviewJob VoiceId must not be empty.");
        }

        if (VoiceId.Length > MaxVoiceIdLength)
        {
            throw new DomainException("VoicePreviewJob VoiceId must be at most 256 chars.");
        }

        if (string.IsNullOrWhiteSpace(Text))
        {
            throw new DomainException("VoicePreviewJob Text must not be empty.");
        }

        if (Text.Length > MaxTextLength)
        {
            throw new DomainException("VoicePreviewJob Text must be at most 500 chars.");
        }

        if (!Enum.IsDefined(Status))
        {
            throw new DomainException("VoicePreviewJob Status is not defined.");
        }

        if (RequestedByUserId == Guid.Empty)
        {
            throw new DomainException("VoicePreviewJob RequestedByUserId must not be empty.");
        }

        if (!Enum.IsDefined(QuotaCheck))
        {
            throw new DomainException("VoicePreviewJob QuotaCheck is not defined.");
        }

        if (QuotaCheckReason is not null && string.IsNullOrWhiteSpace(QuotaCheckReason))
        {
            throw new DomainException("VoicePreviewJob QuotaCheckReason must not be empty when set.");
        }

        if (!Enum.IsDefined(ConsentState))
        {
            throw new DomainException("VoicePreviewJob ConsentState is not defined.");
        }

        if (ProviderExecutionId.HasValue && ProviderExecutionId.Value == Guid.Empty)
        {
            throw new DomainException("VoicePreviewJob ProviderExecutionId must not be empty when set.");
        }

        if (ArtifactId.HasValue && ArtifactId.Value == Guid.Empty)
        {
            throw new DomainException("VoicePreviewJob ArtifactId must not be empty when set.");
        }

        if (ErrorCode is not null && string.IsNullOrWhiteSpace(ErrorCode))
        {
            throw new DomainException("VoicePreviewJob ErrorCode must not be empty when set.");
        }

        if (StartedAt.HasValue && StartedAt.Value < CreatedAt)
        {
            throw new DomainException("VoicePreviewJob StartedAt must not be before CreatedAt.");
        }

        if (CompletedAt.HasValue && StartedAt.HasValue && CompletedAt.Value < StartedAt.Value)
        {
            throw new DomainException("VoicePreviewJob CompletedAt must not be before StartedAt.");
        }
    }

    /// <summary>
    /// Whether the job is in a terminal state (no further transitions allowed).
    /// </summary>
    public bool IsTerminal => Status is VoicePreviewStatus.Completed or VoicePreviewStatus.Failed or VoicePreviewStatus.Cancelled;

    /// <summary>
    /// Transitions <c>Pending → Running</c> and stamps <c>StartedAt</c>.
    /// </summary>
    public void MarkRunning(DateTimeOffset startedAt)
    {
        if (Status != VoicePreviewStatus.Pending)
        {
            throw new DomainException($"PREVIEW_STATE_CONFLICT: voice preview job '{Id:D}' is '{Status}', only Pending jobs can start.");
        }

        if (startedAt < CreatedAt)
        {
            throw new DomainException("VoicePreviewJob StartedAt must not be before CreatedAt.");
        }

        Status = VoicePreviewStatus.Running;
        StartedAt = startedAt;

        Validate();
    }

    /// <summary>
    /// Transitions <c>Running → Completed</c> and links the preview artifact
    /// plus the provider execution record.
    /// </summary>
    public void MarkCompleted(Guid artifactId, Guid providerExecutionId, DateTimeOffset completedAt)
    {
        if (Status != VoicePreviewStatus.Running)
        {
            throw new DomainException($"PREVIEW_STATE_CONFLICT: voice preview job '{Id:D}' is '{Status}', only Running jobs can complete.");
        }

        if (artifactId == Guid.Empty)
        {
            throw new DomainException("VoicePreviewJob ArtifactId must not be empty.");
        }

        if (providerExecutionId == Guid.Empty)
        {
            throw new DomainException("VoicePreviewJob ProviderExecutionId must not be empty.");
        }

        ArtifactId = artifactId;
        ProviderExecutionId = providerExecutionId;
        CompletedAt = completedAt;

        Status = VoicePreviewStatus.Completed;

        Validate();
    }

    /// <summary>
    /// Transitions <c>Running → Failed</c> with a catalogued error code and a
    /// redacted detail message (callers redact secrets before passing).
    /// </summary>
    public void MarkFailed(string errorCode, string? errorMessage, DateTimeOffset completedAt)
    {
        if (Status != VoicePreviewStatus.Running)
        {
            throw new DomainException($"PREVIEW_STATE_CONFLICT: voice preview job '{Id:D}' is '{Status}', only Running jobs can fail.");
        }

        if (string.IsNullOrWhiteSpace(errorCode))
        {
            throw new DomainException("VoicePreviewJob ErrorCode must not be empty.");
        }

        ErrorCode = errorCode.Trim();
        ErrorMessage = TruncateError(errorMessage);
        CompletedAt = completedAt;

        Status = VoicePreviewStatus.Failed;

        Validate();
    }

    /// <summary>
    /// Transitions <c>Pending/Running → Cancelled</c>. Terminal jobs throw
    /// with a <c>PREVIEW_STATE_CONFLICT</c> marker and are left unchanged.
    /// </summary>
    public void MarkCancelled(DateTimeOffset completedAt)
    {
        if (Status is not (VoicePreviewStatus.Pending or VoicePreviewStatus.Running))
        {
            throw new DomainException($"PREVIEW_STATE_CONFLICT: voice preview job '{Id:D}' is '{Status}' and cannot be cancelled.");
        }

        CompletedAt = completedAt;

        Status = VoicePreviewStatus.Cancelled;

        Validate();
    }

    private static string Truncate(string text)
    {
        return text.Length > MaxTextLength ? text.Substring(0, MaxTextLength) : text;
    }

    private static string? TruncateError(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return null;
        }

        var trimmed = message.Trim();
        return trimmed.Length > MaxErrorLength ? trimmed.Substring(0, MaxErrorLength) : trimmed;
    }

    private static string? NormalizeKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        var trimmed = key.Trim();
        if (trimmed.Length > MaxIdempotencyKeyLength)
        {
            throw new DomainException("VoicePreviewJob IdempotencyKey must be at most 128 chars.");
        }

        return trimmed;
    }
}
