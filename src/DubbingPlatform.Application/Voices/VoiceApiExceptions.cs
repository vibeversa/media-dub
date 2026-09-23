using DubbingPlatform.Application.Errors;

namespace DubbingPlatform.Application.Voices;

/// <summary>
/// Task 010 speaker/voice API errors. Each carries a catalogued code; the
/// incompatible-voice error additionally carries structured exclusion reasons
/// so clients can explain why a voice is not selectable.
/// </summary>
public sealed class VoiceIncompatibleException : Exceptions.AppException, Exceptions.IErrorDetailsProvider
{
    public VoiceIncompatibleException(string voiceId, IReadOnlyList<string> reasons)
        : base(
            ErrorCodes.VoiceIncompatible,
            string.Concat("Voice '", voiceId, "' is incompatible with this project."))
    {
        VoiceId = voiceId;
        Reasons = reasons ?? [];
    }

    public string VoiceId { get; }

    public IReadOnlyList<string> Reasons { get; }

    public override int StatusCode => ErrorCodes.StatusFor(ErrorCode);

    public IReadOnlyDictionary<string, object?> GetErrorDetails()
    {
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["voiceId"] = VoiceId,
            ["reasons"] = Reasons.ToArray(),
        };
    }
}

/// <summary>
/// Cloning-voice use without recorded consent (403 VOICE_CONSENT_REQUIRED).
/// </summary>
public sealed class VoiceConsentRequiredException : Exceptions.AppException
{
    public VoiceConsentRequiredException(string message)
        : base(ErrorCodes.VoiceConsentRequired, message)
    {
    }

    public override int StatusCode => ErrorCodes.StatusFor(ErrorCode);
}

/// <summary>
/// Preview quota/throttle denial (429 PREVIEW_QUOTA_EXCEEDED).
/// </summary>
public sealed class PreviewQuotaExceededException : Exceptions.AppException
{
    public PreviewQuotaExceededException(string message)
        : base(ErrorCodes.PreviewQuotaExceeded, message)
    {
    }

    public override int StatusCode => ErrorCodes.StatusFor(ErrorCode);
}

/// <summary>
/// Unknown voice id (404 VOICE_NOT_FOUND).
/// </summary>
public sealed class VoiceNotFoundException : Exceptions.AppException
{
    public VoiceNotFoundException(string message)
        : base(ErrorCodes.VoiceNotFound, message)
    {
    }

    public override int StatusCode => ErrorCodes.StatusFor(ErrorCode);
}

/// <summary>
/// Empty/over-long preview text (400 PREVIEW_TEXT_INVALID).
/// </summary>
public sealed class PreviewTextInvalidException : Exceptions.AppException
{
    public PreviewTextInvalidException(string message)
        : base(ErrorCodes.PreviewTextInvalid, message)
    {
    }

    public override int StatusCode => ErrorCodes.StatusFor(ErrorCode);
}
