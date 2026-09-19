namespace DubbingPlatform.Api.Middleware;

/// <summary>
/// Structured error envelope returned by all APIs:
/// <c>{ "error": { "code": "...", "message": "...", "correlationId": "...", "details": {} } }</c>.
/// Responses never include stack traces or secret values.
/// </summary>
public sealed record ErrorResponse(ErrorBody Error);

/// <summary>
/// Error payload inside <see cref="ErrorResponse"/>.
/// </summary>
public sealed record ErrorBody(string Code, string Message, string CorrelationId, IReadOnlyDictionary<string, object?> Details);
