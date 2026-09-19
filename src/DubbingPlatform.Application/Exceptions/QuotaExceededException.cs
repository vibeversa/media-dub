using DubbingPlatform.Application.Errors;

namespace DubbingPlatform.Application.Exceptions;

/// <summary>
/// A quota budget is exhausted. Maps to 429 QUOTA_EXCEEDED.
/// </summary>
public sealed class QuotaExceededException : AppException
{
    public QuotaExceededException(string message)
        : base(ErrorCodes.QuotaExceeded, message)
    {
    }

    public QuotaExceededException(string message, Exception innerException)
        : base(ErrorCodes.QuotaExceeded, message, innerException)
    {
    }

    public override int StatusCode => ErrorCodes.StatusFor(ErrorCode);
}
