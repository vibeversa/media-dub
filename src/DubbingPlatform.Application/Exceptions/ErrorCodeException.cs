using DubbingPlatform.Application.Errors;

namespace DubbingPlatform.Application.Exceptions;

/// <summary>
/// Carries any catalogued public error code. Prefer the specific exception
/// types when one exists; use this for the remaining catalog codes.
/// </summary>
public sealed class ErrorCodeException : AppException
{
    public ErrorCodeException(string errorCode, string message)
        : base(RequireKnown(errorCode), message)
    {
    }

    public ErrorCodeException(string errorCode, string message, Exception innerException)
        : base(RequireKnown(errorCode), message, innerException)
    {
    }

    public override int StatusCode => ErrorCodes.StatusFor(ErrorCode);

    private static string RequireKnown(string errorCode)
    {
        if (!ErrorCodes.IsKnown(errorCode))
        {
            throw new ArgumentException($"Unknown error code '{errorCode}'.", nameof(errorCode));
        }

        return errorCode;
    }
}
