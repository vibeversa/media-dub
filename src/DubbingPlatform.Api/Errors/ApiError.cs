using DubbingPlatform.Api.Middleware;
using DubbingPlatform.Application.Errors;
using DubbingPlatform.Application.Exceptions;
using DubbingPlatform.Application.Security;
using DubbingPlatform.Domain.Exceptions;
using FluentValidation;

namespace DubbingPlatform.Api.Errors;

/// <summary>
/// Frozen backend error envelope + code→HTTP mapping table (Task 013).
/// Wire shape is <c>{ error: { code, message, correlationId, details } }</c>
/// (see <see cref="ErrorResponse"/>); <c>details</c> carries field errors only.
/// Mapping table: validation→400, auth→401
/// (<c>TOKEN_EXPIRED, TOKEN_REUSED, INVALID_CREDENTIALS</c>), forbidden→403,
/// not-found→404 (cross-tenant included, no existence leak), conflict→409
/// (<c>SELECTION_CONFLICT, REVIEW_VERSION_CONFLICT, SETTINGS_LOCKED_ACTIVE_RUN,
/// RUN_ALREADY_ACTIVE, PREVIEW_STATE_CONFLICT</c> markers), quota→429,
/// downstream/provider→502/504 (<c>PROVIDER_FAILED, PROVIDER_INVALID_RESPONSE</c>
/// 502; <c>PROVIDER_TIMEOUT / PREVIEW_PROVIDER_TIMEOUT</c> 504 per catalog),
/// unknown→500 <c>INTERNAL_ERROR</c> (message generic, detail in logs only).
/// 500s never leak stack traces or internals; <c>correlationId</c> links to
/// the server log. Sub-code markers (<c>ADMIN_ROUTE_UNKNOWN,
/// DIAGNOSTICS_FORBIDDEN, PREVIEW_*</c>) ride in the message on the public
/// code so the catalog stays stable.
/// </summary>
public static class ApiError
{
    /// <summary>Marker for unknown admin sub-paths; public code stays NOT_FOUND (404).</summary>
    public const string AdminRouteUnknownMarker = "ADMIN_ROUTE_UNKNOWN";

    /// <summary>Generic 500 message; internals stay in logs only.</summary>
    public const string GenericInternalMessage = "An unexpected error occurred.";

    /// <summary>
    /// Maps an exception to (status, code, message, details) per the frozen table.
    /// </summary>
    public static (int StatusCode, string Code, string Message, Dictionary<string, object?> Details) Map(Exception exception)
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
}
