using System.Text.Json;
using DubbingPlatform.Application.Authorization;
using DubbingPlatform.Application.Configuration;
using DubbingPlatform.Application.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace DubbingPlatform.Api.Filters;

/// <summary>
/// Idempotency action filter for POST/DELETE (also PUT/PATCH) mutations.
/// Reads the <c>Idempotency-Key</c> header; when absent the action runs without
/// idempotency. When present it computes the request hash via
/// <see cref="ConfigurationHashCalculator"/> over the bound action arguments
/// (canonical JSON, secrets stripped), claims
/// <c>(tenant, METHOD path, key)</c> via <see cref="IdempotencyService"/>, and:
/// replay → short-circuits with the stored status/body (no re-execution);
/// mismatch → 409 CONFLICT; in-flight → 409 CONFLICT + <c>Retry-After</c>.
/// After successful execution the filter completes the row with the action's
/// status code and JSON body. Failures mark the row <c>Failed</c> so the next
/// attempt with the same key is treated as a new claim.
/// Expiry comes from <see cref="IdempotencyRetention"/> per endpoint class.
/// </summary>
public sealed class IdempotencyFilter : IAsyncActionFilter
{
    private static readonly JsonSerializerOptions BodyOptions = new(JsonSerializerDefaults.Web);

    private readonly IdempotencyService _idempotency;

    public IdempotencyFilter(IdempotencyService idempotency)
    {
        ArgumentNullException.ThrowIfNull(idempotency);
        _idempotency = idempotency;
    }

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var request = context.HttpContext.Request;
        if (!IsMutation(request.Method))
        {
            await next().ConfigureAwait(false);
            return;
        }

        if (!request.Headers.TryGetValue(IdempotencyService.HeaderName, out var values))
        {
            await next().ConfigureAwait(false);
            return;
        }

        var key = values.ToString().Trim();
        if (string.IsNullOrWhiteSpace(key))
        {
            await next().ConfigureAwait(false);
            return;
        }

        if (key.Length > 256)
        {
            context.Result = new BadRequestObjectResult(new { error = "Idempotency-Key must be <= 256 chars." });
            return;
        }

        Guid tenantId;
        try
        {
            tenantId = context.HttpContext.User.GetTenantId();
        }
        catch (Application.Exceptions.ForbiddenException)
        {
            await next().ConfigureAwait(false);
            return;
        }

        var endpoint = string.Concat(request.Method.ToUpperInvariant(), " ", request.Path.Value ?? "/");
        var requestHash = ConfigurationHashCalculator.Compute(new SortedDictionary<string, object?>(FlattenArguments(context.ActionArguments), StringComparer.Ordinal));
        var expiry = IdempotencyRetention.ExpiryFor(endpoint);

        (bool IsReplay, string? Response) claim;
        try
        {
            claim = await _idempotency.TryClaimAsync(tenantId, endpoint, key, requestHash, expiry, context.HttpContext.RequestAborted).ConfigureAwait(false);
        }
        catch (IdempotencyInProgressException progress)
        {
            context.HttpContext.Response.Headers.RetryAfter = progress.RetryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
            throw;
        }

        if (claim.IsReplay)
        {
            var stored = claim.Response ?? string.Empty;
            int status = StatusCodes.Status200OK;
            string body = stored;
            try
            {
                using var document = JsonDocument.Parse(stored);
                if (document.RootElement.ValueKind == JsonValueKind.Object
                    && document.RootElement.TryGetProperty("statusCode", out var statusProp)
                    && statusProp.ValueKind == JsonValueKind.Number
                    && statusProp.TryGetInt32(out var parsed)
                    && parsed >= 200 && parsed <= 599
                    && document.RootElement.TryGetProperty("body", out var bodyProp)
                    && bodyProp.ValueKind == JsonValueKind.String)
                {
                    status = parsed;
                    body = bodyProp.GetString() ?? stored;
                }
            }
#pragma warning disable CA1031 // Stored-response parsing is best-effort; fall back to 200 + raw body.
            catch (Exception)
            {
                status = StatusCodes.Status200OK;
                body = stored;
            }
#pragma warning restore CA1031

            context.Result = new ContentResult
            {
                Content = body,
                ContentType = "application/json",
                StatusCode = status,
            };
            return;
        }

        var executed = await next().ConfigureAwait(false);

        var result = executed.Result;
        if (result is null)
        {
            await _idempotency.FailAsync(tenantId, endpoint, key).ConfigureAwait(false);
            return;
        }

        if (executed.Exception is not null && !executed.ExceptionHandled)
        {
            await _idempotency.FailAsync(tenantId, endpoint, key).ConfigureAwait(false);
            return;
        }

        var (statusCode, bodyJson) = CaptureResult(result);
        if (statusCode >= 200 && statusCode < 300)
        {
            var stored = JsonSerializer.Serialize(new StoredResponse(statusCode, bodyJson), BodyOptions);
            await _idempotency.CompleteAsync(tenantId, endpoint, key, statusCode, stored).ConfigureAwait(false);
        }
        else
        {
            await _idempotency.FailAsync(tenantId, endpoint, key).ConfigureAwait(false);
        }
    }

    internal static bool IsMutation(string method)
    {
        return string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase)
            || string.Equals(method, "DELETE", StringComparison.OrdinalIgnoreCase)
            || string.Equals(method, "PUT", StringComparison.OrdinalIgnoreCase)
            || string.Equals(method, "PATCH", StringComparison.OrdinalIgnoreCase);
    }

    private static Dictionary<string, object?> FlattenArguments(IDictionary<string, object?> arguments)
    {
        var flattened = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var pair in arguments)
        {
            flattened[pair.Key] = pair.Value;
        }

        return flattened;
    }

    private static (int StatusCode, string BodyJson) CaptureResult(IActionResult result)
    {
        switch (result)
        {
            case ObjectResult objectResult:
                var status = objectResult.StatusCode ?? StatusCodes.Status200OK;
                var body = JsonSerializer.Serialize(objectResult.Value, BodyOptions);
                return (status, body);
            case ContentResult contentResult:
                return (contentResult.StatusCode ?? StatusCodes.Status200OK, contentResult.Content ?? string.Empty);
            case StatusCodeResult statusResult:
                return (statusResult.StatusCode, JsonSerializer.Serialize(new { status = statusResult.StatusCode }, BodyOptions));
            default:
                return (StatusCodes.Status200OK, JsonSerializer.Serialize(new { }, BodyOptions));
        }
    }

    private sealed record StoredResponse(int StatusCode, string Body);
}
