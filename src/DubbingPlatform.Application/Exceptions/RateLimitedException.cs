using DubbingPlatform.Application.Errors;

namespace DubbingPlatform.Application.Exceptions;

/// <summary>
/// A rate limit was hit. Maps to 429 RATE_LIMITED.
/// </summary>
public sealed class RateLimitedException : AppException
{
    public RateLimitedException(string message)
        : base(ErrorCodes.RateLimited, message)
    {
    }

    public RateLimitedException(string message, Exception innerException)
        : base(ErrorCodes.RateLimited, message, innerException)
    {
    }

    public override int StatusCode => ErrorCodes.StatusFor(ErrorCode);
}
