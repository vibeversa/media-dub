using System.Text.Json;
using DubbingPlatform.Application.Errors;
using DubbingPlatform.Application.Exceptions;
using DubbingPlatform.Application.Security;
using DubbingPlatform.Domain.Exceptions;
using FluentValidation;

namespace DubbingPlatform.Api.Middleware;

/// <summary>
/// Converts exceptions to structured <see cref="ErrorResponse"/> envelopes.
/// Mapping: <see cref="ValidationException"/> and <see cref="DomainException"/>
/// to 400 VALIDATION_FAILED, <see cref="UnauthorizedAccessException"/> to 401,
/// <see cref="ForbiddenException"/> to 403, <see cref="NotFoundException"/> to 404,
/// <see cref="ConflictException"/> to 409, quota/rate errors to 429, any other
/// <see cref="AppException"/> to its catalogued code and status, and anything
/// else to 500 INTERNAL_ERROR with a generic message. Messages are passed
/// through <see cref="SecretRedactor"/>; stack traces are never returned.
/// </summary>
public sealed class ExceptionHandlingMiddleware
{
    private const string GenericInternalMessage = "An unexpected error occurred.";

    private static readonly JsonSerializerOptions EnvelopeOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly RequestDelegate _next;

    public ExceptionHandlingMiddleware(RequestDelegate next)
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

    internal static (int StatusCode, string Code, string Message, Dictionary<string, object?> Details) Map(Exception exception)
    {
        switch (exception)
        {
            case ValidationException validation:
                return (400, ErrorCodes.ValidationFailed, RedactedOrCode(validation, ErrorCodes.ValidationFailed), ValidationDetails(validation));
            case AppException app:
                return (app.StatusCode, app.ErrorCode, RedactedOrCode(app, app.ErrorCode), ErrorDetails(app));
            case DomainException domain:
                return (400, ErrorCodes.ValidationFailed, RedactedOrCode(domain, ErrorCodes.ValidationFailed), new Dictionary<string, object?>(StringComparer.Ordinal));
            case UnauthorizedAccessException unauthorized:
                return (401, ErrorCodes.Unauthorized, RedactedOrCode(unauthorized, ErrorCodes.Unauthorized), new Dictionary<string, object?>(StringComparer.Ordinal));
            default:
                return (500, ErrorCodes.InternalError, GenericInternalMessage, new Dictionary<string, object?>(StringComparer.Ordinal));
        }
    }

    private static string RedactedOrCode(Exception exception, string code)
    {
        var redacted = SecretRedactor.Redact(exception.Message);
        return string.IsNullOrWhiteSpace(redacted) ? code : redacted;
    }

    private static Dictionary<string, object?> ErrorDetails(AppException app)
    {
        if (app is IErrorDetailsProvider provider)
        {
            try
            {
                var details = provider.GetErrorDetails();
                var merged = new Dictionary<string, object?>(StringComparer.Ordinal);
                if (details is not null)
                {
                    foreach (var entry in details)
                    {
                        merged[entry.Key] = entry.Value;
                    }
                }

                return SecretRedactor.RedactDetails(merged);
            }
#pragma warning disable CA1031 // Details are best effort; envelope must never fail.
            catch (Exception)
            {
                return new Dictionary<string, object?>(StringComparer.Ordinal);
            }
#pragma warning restore CA1031
        }

        return new Dictionary<string, object?>(StringComparer.Ordinal);
    }

    private static Dictionary<string, object?> ValidationDetails(ValidationException validation)
    {
        var details = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var group in validation.Errors.GroupBy(f => f.PropertyName, StringComparer.Ordinal))
        {
            var messages = group
                .Select(f => SecretRedactor.Redact(f.ErrorMessage) ?? string.Empty)
                .Where(m => m.Length > 0)
                .ToArray();
            details[group.Key] = messages;
        }

        return SecretRedactor.RedactDetails(details);
    }

    private static async Task WriteErrorAsync(HttpContext context, Exception exception)
    {
        if (context.Response.HasStarted)
        {
            throw exception;
        }

        var (statusCode, code, message, details) = Map(exception);
        var correlationId = CorrelationIdMiddleware.GetCorrelationId(context);

        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json";
        context.Response.Headers[CorrelationIdMiddleware.HeaderName] = correlationId;

        var envelope = new ErrorResponse(new ErrorBody(code, message, correlationId, details));
        await JsonSerializer.SerializeAsync(context.Response.Body, envelope, EnvelopeOptions).ConfigureAwait(false);
    }
}
