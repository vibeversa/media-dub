using DubbingPlatform.Application.Errors;

namespace DubbingPlatform.Application.Exceptions;

/// <summary>
/// The requested resource does not exist. Maps to 404 NOT_FOUND.
/// </summary>
public sealed class NotFoundException : AppException
{
    public NotFoundException(string message)
        : base(ErrorCodes.NotFound, message)
    {
    }

    public NotFoundException(string message, Exception innerException)
        : base(ErrorCodes.NotFound, message, innerException)
    {
    }

    public override int StatusCode => ErrorCodes.StatusFor(ErrorCode);
}
