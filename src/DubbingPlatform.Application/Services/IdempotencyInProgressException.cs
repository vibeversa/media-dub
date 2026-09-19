using DubbingPlatform.Application.Errors;
using DubbingPlatform.Application.Exceptions;

namespace DubbingPlatform.Application.Services;

/// <summary>
/// Thrown when an idempotency key is already claimed by an in-flight request
/// (Stored <c>Started</c> younger than <see cref="IdempotencyService.InProgressWindow"/>).
/// Maps to 409 CONFLICT with a <c>Retry-After</c> response header so clients
/// retry after the first attempt completes and replays.
/// </summary>
public sealed class IdempotencyInProgressException : AppException
{
    public IdempotencyInProgressException(string message, int retryAfterSeconds)
        : base(ErrorCodes.Conflict, message)
    {
        RetryAfterSeconds = retryAfterSeconds;
    }

    /// <summary>
    /// Gets the Retry-After delay in seconds.
    /// </summary>
    public int RetryAfterSeconds { get; }

    public override int StatusCode => ErrorCodes.StatusFor(ErrorCode);
}
