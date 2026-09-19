using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using DubbingPlatform.Domain.Exceptions;

namespace DubbingPlatform.Application.Services;

/// <summary>
/// Deterministic target-speech duration estimator plus SSML prosody helpers.
/// Model: <c>chars * perCharMs[lang] / rate</c>, clamped to
/// <c>300..30000ms</c>. Per-character table (case-insensitive, trimmed):
/// <c>en=70ms</c>, <c>es=75ms</c>, <c>de=80ms</c>, <c>fr=75ms</c>, default
/// (any other, empty, or unknown language) <c>75ms</c>. Rationale: German
/// averages longer realizations than English, Spanish/French track near
/// parity; unknown languages assume the 75ms parity default so timing never
/// rewards truncation. No randomness: same input always yields the same
/// estimate on any machine. The estimate pre-adjusts prosody before the first
/// paid TTS call (<c>rate = clamp(targetMs/estimate, 0.85, 1.15)</c>) so the
/// first synthesis already tracks the segment budget, reducing
/// timing-optimization retries. Never receives secrets (tenant text plus
/// language only); never logs text.
/// </summary>
public static class DurationEstimator
{
    /// <summary>Minimum estimated duration in milliseconds.</summary>
    public const int MinMs = 300;

    /// <summary>Maximum estimated duration in milliseconds.</summary>
    public const int MaxMs = 30000;

    /// <summary>Minimum prosody rate (slow down at most 15%).</summary>
    public const double MinRate = 0.85;

    /// <summary>Maximum prosody rate (speed up at most 15%).</summary>
    public const double MaxRate = 1.15;

    /// <summary>Deterministic SSML template identity (no secrets in the id).</summary>
    public const string SsmlTemplateId = "tts-ssml-v1";

    /// <summary>
    /// Per-character milliseconds for a language. Pure. Case-insensitive,
    /// trimmed; empty or unknown yields the 75ms default.
    /// </summary>
    public static int PerCharMsFor(string? language)
    {
        var normalized = (language ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "en" => 70,
            "es" => 75,
            "de" => 80,
            "fr" => 75,
            _ => 75,
        };
    }

    /// <summary>
    /// Estimates speech duration: <c>chars * perCharMs[lang] / rate</c>,
    /// clamped to <c>300..30000ms</c>. Pure. <paramref name="text"/> may be
    /// empty (yields the 300ms floor); null throws. <paramref name="rate"/>
    /// must be finite and &gt; 0.
    /// </summary>
    public static int EstimateMs(string text, string? targetLang, double rate = 1.0)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (double.IsNaN(rate) || double.IsInfinity(rate) || rate <= 0.0)
        {
            throw new DomainException("Rate must be finite and > 0.");
        }

        var perChar = PerCharMsFor(targetLang);
        var raw = (double)text.Length * perChar / rate;
        if (double.IsNaN(raw) || double.IsInfinity(raw))
        {
            return MinMs;
        }

        var rounded = (int)Math.Round(raw, MidpointRounding.AwayFromZero);
        return Math.Clamp(rounded, MinMs, MaxMs);
    }

    /// <summary>
    /// Prosody rate aligning an estimate to a segment budget:
    /// <c>clamp(targetMs/estimateMs, 0.85, 1.15)</c>. Pure. Non-positive
    /// estimates yield 1.0 (no adjustment); non-positive targets clamp to the
    /// 0.85 floor via the ratio.
    /// </summary>
    public static double ComputeRate(int estimateMs, int targetMs)
    {
        if (estimateMs <= 0)
        {
            return 1.0;
        }

        var ratio = (double)targetMs / estimateMs;
        if (double.IsNaN(ratio) || double.IsInfinity(ratio))
        {
            return 1.0;
        }

        return Math.Clamp(ratio, MinRate, MaxRate);
    }

    /// <summary>
    /// Builds deterministic SSML wrapping <paramref name="text"/> in a
    /// <c>&lt;prosody rate="N%"&gt;</c> tag. Pure. Text is XML-escaped
    /// (&amp;, &lt;, &gt;, quotes); rate is rounded to whole percent
    /// (e.g. 1.0 → <c>100%</c>, 0.85 → <c>85%</c>). No secrets are added.
    /// </summary>
    public static string BuildSsml(string text, double rate)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (double.IsNaN(rate) || double.IsInfinity(rate) || rate <= 0.0)
        {
            throw new DomainException("Rate must be finite and > 0.");
        }

        var clamped = Math.Clamp(rate, MinRate, MaxRate);
        var percent = (int)Math.Round(clamped * 100.0, MidpointRounding.AwayFromZero);
        var escaped = EscapeXml(text);
        return string.Concat(
            "<speak><prosody rate=\"",
            percent.ToString(CultureInfo.InvariantCulture),
            "%\">",
            escaped,
            "</prosody></speak>");
    }

    /// <summary>
    /// SHA-256 hex (lowercase) of the UTF-8 bytes. Pure; same input always
    /// yields the same hash. Never receives secrets.
    /// </summary>
    public static string ComputeHash(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string EscapeXml(string text)
    {
        return text
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal)
            .Replace("'", "&apos;", StringComparison.Ordinal);
    }
}
