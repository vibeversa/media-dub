using DubbingPlatform.Domain.Exceptions;

namespace DubbingPlatform.Application.Security;

/// <summary>
/// Secret-handling policy. Secrets arrive only via environment variables or a
/// secret manager (never hardcoded, never defaulted credentials); they must
/// never appear in logs, traces, hashes, or error responses. Property names
/// carrying secret material are those containing <c>secret</c>,
/// <c>password</c>, <c>token</c>, <c>key</c>, or <c>credential</c>
/// (case-insensitive substring, consistent with <see cref="SecretRedactor"/>
/// and <c>ConfigurationHashCalculator</c>). Options properties holding secrets
/// carry <c>[Secret]</c> (see <c>SecretAttribute</c>) and are redacted by the
/// Serilog destructuring policy plus <see cref="SecretRedactor"/> at the audit
/// boundary. Hash inputs must exclude secret values (hashes are
/// content-addressed over non-secret configuration only).
/// </summary>
public static class SecretPolicy
{
    /// <summary>
    /// Whether a property name denotes secret material. Pure. Delegates to
    /// <see cref="SecretRedactor.IsSensitiveKey"/> so log, hash, and error
    /// boundaries share one definition.
    /// </summary>
    public static bool IsSecretProperty(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return SecretRedactor.IsSensitiveKey(name);
    }

    /// <summary>
    /// Returns the subset of <paramref name="names"/> that denote secret
    /// material. Pure. Callers building hash inputs or error details must drop
    /// these entries before hashing or returning them.
    /// </summary>
    public static IReadOnlyList<string> FindSecretNames(IEnumerable<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        var found = new List<string>();
        foreach (var name in names)
        {
            if (!string.IsNullOrWhiteSpace(name) && SecretRedactor.IsSensitiveKey(name))
            {
                found.Add(name);
            }
        }

        return found;
    }

    /// <summary>
    /// Throws <see cref="DomainException"/> when any of <paramref name="names"/>
    /// denotes secret material. Pure. Use at hash-input construction sites to
    /// fail closed instead of silently hashing a credential.
    /// </summary>
    public static void ThrowIfSecretNames(IEnumerable<string> names)
    {
        var secrets = FindSecretNames(names);
        if (secrets.Count > 0)
        {
            throw new DomainException(
                string.Concat("Hash inputs must not contain secret material: ", string.Join(",", secrets), "."));
        }
    }

    /// <summary>
    /// Redacts secret assignments and bearer tokens in free text destined for
    /// logs, traces, or error responses. Returns null or empty input unchanged.
    /// </summary>
    public static string? Redact(string? value)
    {
        return SecretRedactor.Redact(value);
    }

    /// <summary>
    /// Copies detail entries, replacing values of sensitive keys with
    /// <c>[REDACTED]</c>. Never returns secret values.
    /// </summary>
    public static Dictionary<string, object?> RedactDetails(IEnumerable<KeyValuePair<string, object?>>? details)
    {
        return SecretRedactor.RedactDetails(details);
    }
}
