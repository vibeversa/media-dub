using DubbingPlatform.Application.Errors;

namespace DubbingPlatform.Application.Exceptions;

/// <summary>
/// The caller is authenticated but not allowed. Maps to 403 FORBIDDEN.
/// </summary>
public sealed class ForbiddenException : AppException
{
    public ForbiddenException(string message)
        : base(ErrorCodes.Forbidden, message)
    {
    }

    public ForbiddenException(string message, Exception innerException)
        : base(ErrorCodes.Forbidden, message, innerException)
    {
    }

    public override int StatusCode => ErrorCodes.StatusFor(ErrorCode);
}
