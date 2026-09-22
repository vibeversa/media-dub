using System.Text.Json;
using DubbingPlatform.Application.Configuration;
using DubbingPlatform.Application.Errors;
using DubbingPlatform.Application.Exceptions;
using DubbingPlatform.Application.Services;

namespace DubbingPlatform.Application.Processing;

/// <summary>
/// Processing-scoped idempotency over <c>(TenantId, Idempotency-Key)</c>.
/// Unlike the generic <see cref="IdempotencyFilter"/> (namespaced by
/// <c>METHOD path</c>), start and run-level retry share one <c>processing</c>
/// namespace so a retry that reuses a completed-start key with a different
/// payload is rejected with 422 <c>IDEMPOTENCY_KEY_REUSED</c> (retry requires
/// a fresh key). Keys expire after 24h; replay returns the stored run with
/// <c>Idempotent-Replayed: true</c> semantics (caller sets the header and uses
/// 200 vs 202). Request hashes are canonical JSON via
/// <see cref="ConfigurationHashCalculator"/> (secrets stripped) so the same
/// key with a different body never replays.
/// </summary>
public sealed class ProcessingIdempotency
{
    /// <summary>
    /// Fixed endpoint namespace for all processing start/retry claims.
    /// </summary>
    public const string Endpoint = "processing";

    /// <summary>
    /// Key lifetime: 24h per Task 008.
    /// </summary>
    public static readonly TimeSpan Expiry = TimeSpan.FromHours(24);

    /// <summary>
    /// Header value set on replayed responses.
    /// </summary>
    public const string ReplayedHeaderName = "Idempotent-Replayed";

    private static readonly JsonSerializerOptions BodyOptions = new(JsonSerializerDefaults.Web);

    private readonly IdempotencyService _idempotency;

    public ProcessingIdempotency(IdempotencyService idempotency)
    {
        ArgumentNullException.ThrowIfNull(idempotency);
        _idempotency = idempotency;
    }

    /// <summary>
    /// Requires a non-empty key, else 400 <c>IDEMPOTENCY_KEY_REQUIRED</c>.
    /// </summary>
    public static string RequireKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Trim().Length > 256)
        {
            throw new ErrorCodeException(
                ErrorCodes.IdempotencyKeyRequired,
                "The Idempotency-Key header is required for this operation.");
        }

        return key.Trim();
    }

    /// <summary>
    /// Computes the canonical request hash for a processing payload.
    /// Pure for hermetic tests.
    /// </summary>
    public static string HashFor(Guid projectId, string? bodyJson, bool force)
    {
        return ConfigurationHashCalculator.Compute(new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["projectId"] = projectId.ToString("N"),
            ["body"] = bodyJson ?? string.Empty,
            ["force"] = force,
        });
    }

    /// <summary>
    /// Computes the canonical request hash for a run-level retry payload.
    /// Pure for hermetic tests.
    /// </summary>
    public static string HashForRetry(Guid projectId, Guid runId, string? bodyJson)
    {
        return ConfigurationHashCalculator.Compute(new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["projectId"] = projectId.ToString("N"),
            ["runId"] = runId.ToString("N"),
            ["body"] = bodyJson ?? string.Empty,
        });
    }

    /// <summary>
    /// Claims <c>(tenant, key)</c> for <paramref name="requestHash"/>.
    /// Returns <c>(IsReplay: false)</c> when the caller must execute, else
    /// <c>(IsReplay: true, RunId)</c> parsed from the stored response.
    /// Same key + different hash → 422 <c>IDEMPOTENCY_KEY_REUSED</c>.
    /// </summary>
    public async Task<(bool IsReplay, Guid? RunId)> TryClaimAsync(
        Guid tenantId,
        string key,
        string requestHash,
        CancellationToken cancellationToken = default)
    {
        var normalizedKey = RequireKey(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestHash);

        (bool IsReplay, string? Response) claim;
        try
        {
            claim = await _idempotency.TryClaimAsync(
                tenantId, Endpoint, normalizedKey, requestHash, Expiry, cancellationToken).ConfigureAwait(false);
        }
        catch (ConflictException ex)
        {
            throw new ErrorCodeException(ErrorCodes.IdempotencyKeyReused, ex.Message);
        }
        catch (IdempotencyInProgressException ex)
        {
            // In-flight with same hash: surface as conflict with Retry-After
            // preserved by the filter contract; different-hash in-flight also
            // surfaces here as reuse (fail closed, client retries with new key).
            throw new ErrorCodeException(ErrorCodes.IdempotencyKeyReused, ex.Message);
        }

        if (!claim.IsReplay)
        {
            return (false, null);
        }

        return (true, TryExtractRunId(claim.Response));
    }

    /// <summary>
    /// Completes a claim with the run response envelope.
    /// </summary>
    public Task CompleteAsync(
        Guid tenantId,
        string key,
        int statusCode,
        Guid runId,
        string projectId,
        string status,
        CancellationToken cancellationToken = default)
    {
        var normalizedKey = RequireKey(key);
        var body = JsonSerializer.Serialize(
            new StoredRun(runId.ToString("N"), projectId, status),
            BodyOptions);
        var stored = JsonSerializer.Serialize(
            new StoredResponse(statusCode, body),
            BodyOptions);
        return _idempotency.CompleteAsync(tenantId, Endpoint, normalizedKey, statusCode, stored, cancellationToken);
    }

    /// <summary>
    /// Marks a claim failed so the next attempt treats it as new.
    /// </summary>
    public Task FailAsync(Guid tenantId, string key, CancellationToken cancellationToken = default)
    {
        return _idempotency.FailAsync(tenantId, Endpoint, RequireKey(key), cancellationToken);
    }

    internal static Guid? TryExtractRunId(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored))
        {
            return null;
        }

        try
        {
            using var outer = JsonDocument.Parse(stored);
            if (outer.RootElement.ValueKind != JsonValueKind.Object
                || !outer.RootElement.TryGetProperty("body", out var bodyProp)
                || bodyProp.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var innerJson = bodyProp.GetString();
            if (string.IsNullOrWhiteSpace(innerJson))
            {
                return null;
            }

            using var inner = JsonDocument.Parse(innerJson);
            if (inner.RootElement.ValueKind != JsonValueKind.Object
                || !inner.RootElement.TryGetProperty("runId", out var runProp)
                || runProp.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var raw = runProp.GetString();
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            var hex = raw.Trim().StartsWith("run_", StringComparison.Ordinal)
                ? raw.Trim()["run_".Length..]
                : raw.Trim().Replace("-", string.Empty, StringComparison.Ordinal);
            if (Guid.TryParseExact(hex, "N", out var parsed) && parsed != Guid.Empty)
            {
                return parsed;
            }

            if (Guid.TryParse(raw.Trim(), out var guid) && guid != Guid.Empty)
            {
                return guid;
            }

            return null;
        }
#pragma warning disable CA1031 // Stored-response parsing is best effort; null means execute.
        catch (Exception)
        {
            return null;
        }
#pragma warning restore CA1031
    }

    private sealed record StoredResponse(int StatusCode, string Body);

    private sealed record StoredRun(string RunId, string ProjectId, string Status);
}
