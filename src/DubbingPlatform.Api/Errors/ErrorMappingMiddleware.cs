using System.Text.Json;
using DubbingPlatform.Api.Middleware;

namespace DubbingPlatform.Api.Errors;

/// <summary>
/// Uniform error-envelope middleware (Task 013). Converts exceptions to
/// <c>{ error: { code, message, correlationId, details } }</c> via
/// <see cref="ApiError.Map"/> (frozen code→HTTP table). 500s carry a generic
/// message; correlationId links to the server log. This is the canonical
/// middleware; <see cref="ExceptionHandlingMiddleware"/> delegates to the same
/// table for backward compatibility.
/// </summary>
public sealed class ErrorMappingMiddleware
{
    private static readonly JsonSerializerOptions EnvelopeOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly RequestDelegate _next;

    public ErrorMappingMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
#pragma warning disable CA1031 // Middleware boundary: every exception must become an envelope, never escape.
        try
        {
            await _next(context).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await WriteErrorAsync(context, exception).ConfigureAwait(false);
        }
#pragma warning restore CA1031
    }

    internal static async Task WriteErrorAsync(HttpContext context, Exception exception)
    {
        if (context.Response.HasStarted)
        {
            throw exception;
        }

        var (statusCode, code, message, details) = ApiError.Map(exception);
        var correlationId = CorrelationIdMiddleware.GetCorrelationId(context);

        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json";
        context.Response.Headers[CorrelationIdMiddleware.HeaderName] = correlationId;

        var envelope = new ErrorResponse(new ErrorBody(code, message, correlationId, details));
        await JsonSerializer.SerializeAsync(context.Response.Body, envelope, EnvelopeOptions).ConfigureAwait(false);
    }
}
