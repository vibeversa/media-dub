namespace DubbingPlatform.Application.Exceptions;

/// <summary>
/// Carries structured <c>details</c> for the error envelope.
/// The middleware merges these into <c>error.details</c> (camelCase via
/// envelope serialization); secrets must never appear here.
/// </summary>
public interface IErrorDetailsProvider
{
    /// <summary>
    /// Gets the structured details for the envelope.
    /// </summary>
    IReadOnlyDictionary<string, object?> GetErrorDetails();
}
