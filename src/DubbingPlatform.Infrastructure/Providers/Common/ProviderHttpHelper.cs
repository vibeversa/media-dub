using System.Net;
using System.Text.Json;
using DubbingPlatform.Application.Errors;
using DubbingPlatform.Application.Exceptions;
using DubbingPlatform.Domain.Enums;

namespace DubbingPlatform.Infrastructure.Providers.Common;

/// <summary>
/// Shared HTTP mapping for real provider adapters. No secrets are logged;
/// error messages carry status + endpoint path only, never keys or bodies.
/// Classification: 429 → <c>PROVIDER_RATE_LIMITED</c> (or
/// <c>PROVIDER_QUOTA_EXHAUSTED</c> when the body/header signals quota),
/// 408/504 → <c>PROVIDER_TIMEOUT</c>, other 4xx → <c>PROVIDER_FAILED</c>
/// (permanent, fallback but no transport retry), 5xx → <c>PROVIDER_FAILED</c>
/// (transient, retryable within budget), 200 with invalid JSON →
/// <c>PROVIDER_INVALID_RESPONSE</c> (fallback, never transport retry).
/// Timeouts surface as <c>PROVIDER_TIMEOUT</c> unless the caller's token was
/// cancelled, in which case <see cref="OperationCanceledException"/> propagates.
/// </summary>
public static class ProviderHttpHelper
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static string BuildIdempotencyKey(Guid runId, string stage, string scope, int attempt)
    {
        return string.Concat(
            runId.ToString("N"),
            ":",
            stage.Trim(),
            ":",
            scope.Trim(),
            ":",
            attempt.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    public static void AddIdempotencyKey(HttpRequestMessage request, string key)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        request.Headers.Remove("Idempotency-Key");
        request.Headers.TryAddWithoutValidation("Idempotency-Key", key);
    }

    public static async Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        Func<HttpRequestMessage> requestFactory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(requestFactory);

        using var request = requestFactory();
        try
        {
            return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ErrorCodeException(ErrorCodes.ProviderTimeout, "Provider request timed out.", ex);
        }
        catch (TimeoutException ex)
        {
            throw new ErrorCodeException(ErrorCodes.ProviderTimeout, "Provider request timed out.", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new ErrorCodeException(ErrorCodes.ProviderFailed, "Provider request failed.", ex);
        }
    }

    public static async Task ThrowIfErrorAsync(
        HttpResponseMessage response,
        string provider,
        string operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var status = (int)response.StatusCode;
        var path = response.RequestMessage?.RequestUri?.AbsolutePath ?? operation;
        string bodyPreview = string.Empty;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(2));
            bodyPreview = (await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false) ?? string.Empty).Trim();
            if (bodyPreview.Length > 512)
            {
                bodyPreview = bodyPreview[..512];
            }
        }
        catch (OperationCanceledException)
        {
        }
#pragma warning disable CA1031 // Best-effort body preview for quota detection; never fails the classification.
        catch (Exception)
        {
        }
#pragma warning restore CA1031

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            if (IsQuotaSignal(response, bodyPreview))
            {
                throw new ErrorCodeException(
                    ErrorCodes.ProviderQuotaExhausted,
                    $"Provider '{provider}' quota exhausted for {path} (429).");
            }

            var retryAfter = GetRetryAfter(response);
            var suffix = retryAfter.HasValue
                ? $" Retry after {retryAfter.Value.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)}s."
                : string.Empty;
            throw new ErrorCodeException(
                ErrorCodes.ProviderRateLimited,
                $"Provider '{provider}' rate limited for {path} (429).{suffix}");
        }

        if (response.StatusCode == HttpStatusCode.RequestTimeout || response.StatusCode == HttpStatusCode.GatewayTimeout)
        {
            throw new ErrorCodeException(
                ErrorCodes.ProviderTimeout,
                $"Provider '{provider}' timed out for {path} ({status}).");
        }

        if (status >= 400 && status < 500)
        {
            if (IsQuotaSignal(response, bodyPreview))
            {
                throw new ErrorCodeException(
                    ErrorCodes.ProviderQuotaExhausted,
                    $"Provider '{provider}' quota exhausted for {path} ({status}).");
            }

            throw new ErrorCodeException(
                ErrorCodes.ProviderFailed,
                $"Provider '{provider}' permanent failure for {path} ({status}).");
        }

        throw new ErrorCodeException(
            ErrorCodes.ProviderFailed,
            $"Provider '{provider}' transient failure for {path} ({status}).");
    }

    public static async Task<JsonDocument> ReadJsonAsync(
        HttpResponseMessage response,
        string provider,
        string operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(response);
        string body;
        try
        {
            body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ErrorCodeException(ErrorCodes.ProviderTimeout, $"Provider '{provider}' timed out reading {operation}.", ex);
        }

        if (string.IsNullOrWhiteSpace(body))
        {
            throw new ErrorCodeException(
                ErrorCodes.ProviderInvalidResponse,
                $"Provider '{provider}' returned an empty response for {operation}.");
        }

        try
        {
            return JsonDocument.Parse(body, new JsonDocumentOptions { AllowTrailingCommas = false });
        }
        catch (JsonException ex)
        {
            throw new ErrorCodeException(
                ErrorCodes.ProviderInvalidResponse,
                $"Provider '{provider}' returned a malformed response for {operation}.",
                ex);
        }
    }

    public static OutcomeClass ToOutcome(string errorCode)
    {
        return errorCode switch
        {
            ErrorCodes.ProviderRateLimited => OutcomeClass.ProviderRateLimited,
            ErrorCodes.ProviderTimeout => OutcomeClass.ProviderTimeout,
            ErrorCodes.ProviderInvalidResponse => OutcomeClass.ProviderInvalidResponse,
            ErrorCodes.ProviderQuotaExhausted => OutcomeClass.ProviderRateLimited,
            ErrorCodes.ProviderConfigurationError => OutcomeClass.PolicyRejected,
            _ => OutcomeClass.ProviderPermanentFailure,
        };
    }

    public static TimeSpan? GetRetryAfter(HttpResponseMessage response)
    {
        try
        {
            var retryAfter = response.Headers.RetryAfter;
            if (retryAfter?.Delta.HasValue == true && retryAfter.Delta.Value >= TimeSpan.Zero)
            {
                return retryAfter.Delta;
            }

            if (retryAfter?.Date.HasValue == true)
            {
                var delay = retryAfter.Date.Value - DateTimeOffset.UtcNow;
                return delay < TimeSpan.Zero ? TimeSpan.Zero : delay;
            }
        }
#pragma warning disable CA1031 // Header parsing is best-effort; invalid values mean no delay.
        catch (Exception)
        {
        }
#pragma warning restore CA1031

        return null;
    }

    private static bool IsQuotaSignal(HttpResponseMessage response, string bodyPreview)
    {
        foreach (var header in response.Headers)
        {
            if (header.Key.Contains("quota", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            foreach (var value in header.Value)
            {
                if (value.Contains("quota", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return bodyPreview.Contains("quota", StringComparison.OrdinalIgnoreCase)
            || bodyPreview.Contains("insufficient_quota", StringComparison.OrdinalIgnoreCase)
            || bodyPreview.Contains("quota_exceeded", StringComparison.OrdinalIgnoreCase);
    }
}
