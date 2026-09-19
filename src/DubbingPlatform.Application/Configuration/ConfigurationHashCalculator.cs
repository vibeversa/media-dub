using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DubbingPlatform.Application.Configuration;

/// <summary>
/// Computes deterministic configuration hashes. Objects are canonicalized to JSON with
/// sorted keys (ordinal), invariant formatting, and UTF-8 encoding, then hashed with
/// SHA-256 (lowercase hex). Keys containing secret/password/token/key/credential
/// (case-insensitive substring match) are stripped before hashing so secrets never
/// influence hashes. Null settings are treated as an empty object and do not throw.
/// </summary>
public static class ConfigurationHashCalculator
{
    private static readonly string[] SecretFragments = ["secret", "password", "token", "key", "credential"];

    private static readonly JsonSerializerOptions CanonicalOptions = new()
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.Default,
    };

    /// <summary>
    /// Computes the canonical SHA-256 hash of the given settings object.
    /// </summary>
    public static string Compute(object? settings)
    {
        var node = ToNode(settings);
        var canonical = Canonicalize(node);
        var json = canonical.ToJsonString(CanonicalOptions);
        var bytes = Encoding.UTF8.GetBytes(json);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    internal static JsonNode ToNode(object? settings)
    {
        if (settings is null)
        {
            return new JsonObject();
        }

        if (settings is JsonNode node)
        {
            return node;
        }

        if (settings is string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return new JsonObject();
            }

            try
            {
                return JsonNode.Parse(text) ?? new JsonObject();
            }
            catch (JsonException)
            {
                return JsonValue.Create(text)!;
            }
        }

        return JsonSerializer.SerializeToNode(settings, settings.GetType()) ?? new JsonObject();
    }

    internal static JsonNode Canonicalize(JsonNode? node)
    {
        switch (node)
        {
            case null:
                return JsonNode.Parse("null")!;
            case JsonObject obj:
                var canonical = new JsonObject();
                foreach (var pair in obj.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    if (IsSecretKey(pair.Key))
                    {
                        continue;
                    }

                    canonical.Add(pair.Key, Canonicalize(pair.Value));
                }

                return canonical;
            case JsonArray array:
                var canonicalArray = new JsonArray();
                foreach (var item in array)
                {
                    canonicalArray.Add(Canonicalize(item));
                }

                return canonicalArray;
            case JsonValue value:
                return JsonNode.Parse(value.ToJsonString())!;
            default:
                return JsonNode.Parse(node.ToJsonString())!;
        }
    }

    internal static bool IsSecretKey(string name)
    {
        foreach (var fragment in SecretFragments)
        {
            if (name.Contains(fragment, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
