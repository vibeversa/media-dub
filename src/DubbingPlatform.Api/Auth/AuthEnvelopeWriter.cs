using System.Text.Json;
using DubbingPlatform.Api.Middleware;

namespace DubbingPlatform.Api.Auth;

/// <summary>
/// Writes 401/403 authentication envelopes with the shared error shape
/// (<c>{ error: { code, message, correlationId, details } }</c>) so auth
/// failures match business errors and always carry the correlation id.
/// </summary>
public static class AuthEnvelopeWriter
{
    private static readonly JsonSerializerOptions EnvelopeOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>
    /// Writes a 401 UNAUTHORIZED envelope.
    /// </summary>
    public static async Task WriteUnauthorizedAsync(HttpContext context, string message)
    {
        await WriteAsync(context, StatusCodes.Status401Unauthorized, "UNAUTHORIZED", message).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes a 403 FORBIDDEN envelope.
    /// </summary>
    public static async Task WriteForbiddenAsync(HttpContext context, string message)
    {
        await WriteAsync(context, StatusCodes.Status403Forbidden, "FORBIDDEN", message).ConfigureAwait(false);
    }

    private static async Task WriteAsync(HttpContext context, int statusCode, string code, string message)
    {
        if (context.Response.HasStarted)
        {
            return;
        }

        var correlationId = CorrelationIdMiddleware.GetCorrelationId(context);
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json";
        if (!context.Response.Headers.ContainsKey(CorrelationIdMiddleware.HeaderName))
        {
            context.Response.Headers[CorrelationIdMiddleware.HeaderName] = correlationId;
        }

        var envelope = new ErrorResponse(new ErrorBody(
            code,
            message,
            correlationId,
            new Dictionary<string, object?>(StringComparer.Ordinal)));
        await JsonSerializer.SerializeAsync(context.Response.Body, envelope, EnvelopeOptions).ConfigureAwait(false);
    }
}
