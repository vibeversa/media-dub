using System.Text.RegularExpressions;

namespace DubbingPlatform.Application.Security;

/// <summary>
/// Redacts secret material from strings destined for logs, traces, hashes, or
/// error responses. Values assigned to keys containing secret/password/token/key/
/// credential (case-insensitive substring match, consistent with
/// <c>ConfigurationHashCalculator</c>) are replaced with <c>[REDACTED]</c>,
/// as are HTTP bearer tokens.
/// </summary>
public static partial class SecretRedactor
{
    public const string RedactedValue = "[REDACTED]";

    private static readonly string[] SecretFragments = ["secret", "password", "token", "key", "credential"];

    [GeneratedRegex("\\b(?<key>[a-z0-9_.\\-\\[\\]]*(?:secret|password|passwd|pwd|token|key|credential)[a-z0-9_.\\-\\[\\]]*)\\s*[:=]\\s*(?<quote>[\"']?)(?<value>[^\\s,;}\\]\"']+)\\k<quote>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex AssignmentPattern();

    [GeneratedRegex("\\bbearer\\s+[A-Za-z0-9\\-._~+/=]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex BearerPattern();

    /// <summary>
    /// Redacts secret assignments and bearer tokens in free text.
    /// Returns null or empty input unchanged.
    /// </summary>
    public static string? Redact(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        var redacted = AssignmentPattern().Replace(
            value,
            match => string.Concat(match.Groups["key"].Value, $"={RedactedValue}"));
        return BearerPattern().Replace(redacted, $"Bearer {RedactedValue}");
    }

    /// <summary>
    /// Determines whether a key name denotes secret material.
    /// </summary>
    public static bool IsSensitiveKey(string name)
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

    /// <summary>
    /// Copies detail entries, replacing values of sensitive keys with
    /// <c>[REDACTED]</c>.
    /// </summary>
    public static Dictionary<string, object?> RedactDetails(IEnumerable<KeyValuePair<string, object?>>? details)
    {
        var redacted = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (details is null)
        {
            return redacted;
        }

        foreach (var pair in details)
        {
            redacted[pair.Key] = IsSensitiveKey(pair.Key) ? RedactedValue : pair.Value;
        }

        return redacted;
    }
}
