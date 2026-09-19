using DubbingPlatform.Application.Errors;

namespace DubbingPlatform.Application.Exceptions;

/// <summary>
/// The request conflicts with current state. Maps to 409 CONFLICT.
/// </summary>
public sealed class ConflictException : AppException
{
    public ConflictException(string message)
        : base(ErrorCodes.Conflict, message)
    {
    }

    public ConflictException(string message, Exception innerException)
        : base(ErrorCodes.Conflict, message, innerException)
    {
    }

    public override int StatusCode => ErrorCodes.StatusFor(ErrorCode);
}
