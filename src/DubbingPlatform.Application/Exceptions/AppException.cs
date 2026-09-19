namespace DubbingPlatform.Application.Exceptions;

/// <summary>
/// Base class for application errors that carry a public error code.
/// The code appears in API error envelopes; the HTTP status is derived from
/// <see cref="Errors.ErrorCodes.StatusFor(string)"/> unless overridden.
/// </summary>
public abstract class AppException : Exception
{
    protected AppException(string errorCode, string message)
        : base(message)
    {
        ErrorCode = errorCode;
    }

    protected AppException(string errorCode, string message, Exception innerException)
        : base(message, innerException)
    {
        ErrorCode = errorCode;
    }

    /// <summary>
    /// Gets the public error code from the catalog.
    /// </summary>
    public string ErrorCode { get; }

    /// <summary>
    /// Gets the HTTP status code for this error.
    /// </summary>
    public abstract int StatusCode { get; }
}
