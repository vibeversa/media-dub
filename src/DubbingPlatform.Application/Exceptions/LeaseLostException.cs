using DubbingPlatform.Application.Errors;

namespace DubbingPlatform.Application.Exceptions;

/// <summary>
/// A stage commit lost its lease (stale owner/token or non-Running status).
/// Maps to 409 LEASE_LOST. Callers must discard the result and publish nothing.
/// </summary>
public sealed class LeaseLostException : AppException
{
    public LeaseLostException(string message)
        : base(ErrorCodes.LeaseLost, message)
    {
    }

    public LeaseLostException(string message, Exception innerException)
        : base(ErrorCodes.LeaseLost, message, innerException)
    {
    }

    public override int StatusCode => ErrorCodes.StatusFor(ErrorCode);
}
