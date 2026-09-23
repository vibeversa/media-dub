using DubbingPlatform.Domain.Exceptions;

namespace DubbingPlatform.Application.Exports;

/// <summary>
/// Export profile allowlist. Profiles are optional kebab-case variants
/// (e.g. <c>default</c>, <c>broadcast</c>, <c>social</c>); path traversal
/// (<c>..</c>, <c>/</c>, <c>\</c>), blank, and over-long values are rejected
/// with 400 <c>VALIDATION_FAILED</c> via <see cref="DomainException"/>.
/// </summary>
public static class ExportProfileValidator
{
    public const int MaxLength = 64;

    /// <summary>
    /// Normalizes a profile value (null/blank → null) or throws.
    /// </summary>
    public static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var trimmed = raw.Trim();
        if (trimmed.Length > MaxLength)
        {
            throw new DomainException($"Export profile must be at most {MaxLength} chars.");
        }

        if (trimmed.Contains("..", StringComparison.Ordinal)
            || trimmed.Contains('/', StringComparison.Ordinal)
            || trimmed.Contains('\\', StringComparison.Ordinal)
            || trimmed.Contains('\0'))
        {
            throw new DomainException("Export profile must not contain path traversal characters.");
        }

        foreach (var ch in trimmed)
        {
            var ok = char.IsLetterOrDigit(ch) || ch == '-' || ch == '_';
            if (!ok)
            {
                throw new DomainException("Export profile must be kebab-case (letters, digits, '-' or '_').");
            }
        }

        return trimmed.ToLowerInvariant();
    }
}
