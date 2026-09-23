using System.Text.Json;

namespace DubbingPlatform.Api.Sse;

/// <summary>
/// SSE payload allowlist (Task 013). Allowed: IDs, status enums, approximate
/// percents, counts, machine-readable codes, timestamps. NEVER: secrets,
/// tokens, signed URLs, internal paths, raw provider payloads, lease
/// tokens/heartbeats, transcript/translation bodies. The automated scan asserts
/// serialized frames contain none of the forbidden keys.
/// </summary>
public static class SsePayloadPolicy
{
    /// <summary>
    /// Forbidden payload keys (case-insensitive substring match on serialized
    /// JSON keys). Any frame containing one fails validation.
    /// </summary>
    public static readonly string[] ForbiddenKeys =
    [
        "signedUrl",
        "token",
        "secret",
        "apiKey",
        "connectionString",
        "internalPath",
        "rawPayload",
        "leaseToken",
    ];

    private static readonly JsonSerializerOptions ScanOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Validates a payload dictionary against the allowlist. Throws
    /// <see cref="InvalidOperationException"/> when a forbidden key is present.
    /// </summary>
    public static void Validate(IReadOnlyDictionary<string, object?>? payload)
    {
        if (payload is null)
        {
            return;
        }

        foreach (var key in payload.Keys)
        {
            if (IsForbiddenKey(key))
            {
                throw new InvalidOperationException($"SSE payload key '{key}' is forbidden.");
            }
        }

        var json = JsonSerializer.Serialize(payload, ScanOptions);
        ScanJson(json);
    }

    /// <summary>
    /// Scans a serialized SSE frame for forbidden keys. Returns the matched
    /// keys (empty when clean). Case-insensitive.
    /// </summary>
    public static IReadOnlyList<string> ScanFrame(string? frame)
    {
        if (string.IsNullOrEmpty(frame))
        {
            return [];
        }

        var matched = new List<string>();
        foreach (var forbidden in ForbiddenKeys)
        {
            var quoted = string.Concat("\"", forbidden, "\"");
            if (frame.Contains(quoted, StringComparison.OrdinalIgnoreCase))
            {
                matched.Add(forbidden);
            }
        }

        return matched;
    }

    /// <summary>
    /// Determines whether a payload key is forbidden. Pure.
    /// </summary>
    public static bool IsForbiddenKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        foreach (var forbidden in ForbiddenKeys)
        {
            if (key.Contains(forbidden, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static void ScanJson(string json)
    {
        foreach (var forbidden in ForbiddenKeys)
        {
            var quoted = string.Concat("\"", forbidden, "\"");
            if (json.Contains(quoted, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"SSE payload contains forbidden key '{forbidden}'.");
            }
        }
    }
}
