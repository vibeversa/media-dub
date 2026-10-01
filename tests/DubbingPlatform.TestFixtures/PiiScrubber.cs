using System.Globalization;
using System.Text.RegularExpressions;

namespace DubbingPlatform.TestFixtures;

/// <summary>
/// A finding produced by <see cref="PiiScrubber"/>.
/// </summary>
/// <param name="Rule">The rule that fired, e.g. <c>email-non-reserved</c>.</param>
/// <param name="Where">Where it fired, e.g. <c>SyntheticUsers.SyntheticEmail</c>.</param>
/// <param name="Evidence">
/// The offending fragment, truncated and with long digit/hex runs masked. It is
/// never the whole value: a scrubber report that quotes the secret it found is
/// a second copy of the secret, and the report is the artefact most likely to be
/// pasted into an issue.
/// </param>
public sealed record PiiFinding(string Rule, string Where, string Evidence);

/// <summary>
/// Asserts that fixtures, screenshots and logs carry no real PII and no
/// secrets (Task 046 instruction 3, and R4 "PII scrubber passes on all
/// fixtures").
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is a scrubber and not a review step.</b> "Do not commit real
/// customer data" is unenforceable as a convention. It is enforceable as a set
/// of patterns that fail a build, and that is what this is: the fixtures in this
/// library are run through it in <c>TestFixturesTests</c>, so a fixture that
/// grows a real-looking email or a JWT fails the build rather than shipping.
/// </para>
/// <para>
/// <b>Why the rules are shaped the way they are.</b> Each rule below exists
/// because a plausible-looking fixture would otherwise pass. The two that matter
/// most:
/// </para>
/// <list type="number">
/// <item>
/// <description>
/// <c>email-non-reserved</c> requires the address to sit under an RFC 2606
/// reserved domain, not merely to look synthetic. An <c>@example.org</c> address
/// belongs to a real party even if the mailbox is unmonitored, and a fixture
/// that mails one reaches a stranger.
/// </description>
/// </item>
/// <item>
/// <description>
/// <c>secret-literal</c> looks for the credential <em>shapes</em> a leaked
/// secret has - a JWT, an AWS key id, a GitHub token, a private-key header -
/// rather than for the literal strings a given secret happens to be. A rule that
/// listed today's leaked tokens would pass the day someone rotated them, which
/// is the wrong direction for a gate.
/// </description>
/// </item>
/// </list>
/// <para>
/// <b>What this cannot do.</b> It reads text. A fixture that embeds a real name
/// in a non-obvious encoding, or a screenshot with a face in it, is out of reach
/// of any regex. That is why the rules also cover the <em>fixtures themselves</em>
/// (whose shape is asserted directly in <c>TestFixturesTests</c>) and not only
/// free-form text.
/// </para>
/// </remarks>
public static partial class PiiScrubber
{
    /// <summary>
    /// RFC 2606 reserved top-level domains. Every synthetic address must sit
    /// under one of these.
    /// </summary>
    public static readonly IReadOnlyList<string> ReservedDomains =
    [
        "invalid",   // reserved, guaranteed never to resolve
        "test",      // reserved
        "localhost", // reserved
        "example",   // reserved, but owned by a real party: the weakest option
    ];

    /// <summary>
    /// Secret shapes. Every pattern is anchored on structure, never on a
    /// specific value.
    /// </summary>
    private static readonly (string Rule, Regex Pattern)[] SecretRules =
    [
        ("secret-jwt",
            new Regex(@"\beyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}", RegexOptions.CultureInvariant)),

        ("secret-aws-access-key",
            new Regex(@"\b(?:AKIA|ASIA|AIDA|AROA)[0-9A-Z]{16}\b", RegexOptions.CultureInvariant)),

        ("secret-github-token",
            new Regex(@"\bgh[pousr]_[A-Za-z0-9]{16,}\b", RegexOptions.CultureInvariant)),

        ("secret-private-key",
            new Regex(@"-----BEGIN [A-Z ]*PRIVATE KEY-----", RegexOptions.CultureInvariant)),

        ("secret-bearer-header",
            new Regex(@"\bBearer\s+[A-Za-z0-9._~+/-]{20,}=*", RegexOptions.CultureInvariant)),

        // A connection-string password that is NOT a placeholder. The negative
        // lookahead is what lets `Password=CHANGE_ME` through, which every compose
        // file and every runbook in this repository uses; without it the scrubber
        // would fire on the repository's own safe convention and be disabled.
        ("secret-connection-string-password",
            new Regex(
                "(?i)\\bpassword\\s*=\\s*(?!CHANGE_ME\\b|<|\\$|\\{)[^\\s;\"']{4,}",
                RegexOptions.CultureInvariant)),

        // EventSource cannot set headers, so a token in a query string is
        // structurally forced somewhere in this codebase - which is exactly why it
        // must never reach a log or a fixture.
        ("secret-bearer-query",
            new Regex(@"[?&]access_token=[^&\s]{8,}", RegexOptions.CultureInvariant)),
    ];

    /// <summary>
    /// Patterns that indicate a real person's identifying data rather than a
    /// fixture's.
    /// </summary>
    private static readonly (string Rule, Regex Pattern)[] PiiRules =
    [
        ("email-non-reserved", EmailRegex()),

        ("phone-e164",
            new Regex(@"(?<![\w-])\+\d{10,15}(?![\w-])", RegexOptions.CultureInvariant)),

        ("iban",
            new Regex(@"\b[A-Z]{2}\d{2}[A-Z0-9]{10,30}\b", RegexOptions.CultureInvariant)),

        ("ssn-us",
            new Regex(@"(?<!\d)\d{3}-\d{2}-\d{4}(?!\d)", RegexOptions.CultureInvariant)),
    ];

    /// <summary>
    /// Runs every rule over <paramref name="text"/>.
    /// </summary>
    /// <param name="text">The text to scan: a log line, a fixture field, a trace.</param>
    /// <param name="where">
    /// A label used in the finding, so a failure names the field rather than an
    /// offset into a blob.
    /// </param>
    /// <returns>Every finding, in rule order. Empty means the text is clean.</returns>
    public static IReadOnlyList<PiiFinding> Scan(string? text, string where)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(where);

        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        var findings = new List<PiiFinding>();

        foreach (var (rule, pattern) in PiiRules)
        {
            foreach (Match match in pattern.Matches(text))
            {
                if (rule == "email-non-reserved" && IsReservedAddress(match.Value))
                {
                    continue;
                }

                findings.Add(new PiiFinding(rule, where, Mask(match.Value)));
            }
        }

        foreach (var (rule, pattern) in SecretRules)
        {
            foreach (Match match in pattern.Matches(text))
            {
                findings.Add(new PiiFinding(rule, where, Mask(match.Value)));
            }
        }

        return findings;
    }

    /// <summary>
    /// Throws when <paramref name="text"/> contains PII or a secret.
    /// </summary>
    /// <remarks>
    /// Used by <c>TestFixturesTests</c> over every fixture field, and by any test
    /// that wants to assert a rendered screen or a captured log line is clean.
    /// The message lists rule and location and never the evidence, for the reason
    /// given on <see cref="PiiFinding.Evidence"/>.
    /// </remarks>
    /// <param name="text">The text to scan.</param>
    /// <param name="where">Where the text came from, for the failure message.</param>
    /// <exception cref="PiiScrubberException">The text carries PII or a secret.</exception>
    public static void AssertClean(string? text, string where)
    {
        var findings = Scan(text, where);
        if (findings.Count == 0)
        {
            return;
        }

        throw new PiiScrubberException(where, findings);
    }

    /// <summary>
    /// Whether an email address sits under an RFC 2606 reserved domain.
    /// </summary>
    /// <param name="address">The address to classify.</param>
    public static bool IsReservedAddress(string address)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);

        var at = address.LastIndexOf('@');
        if (at < 0 || at == address.Length - 1)
        {
            return false;
        }

        var domain = address[(at + 1)..].TrimEnd('.').ToLowerInvariant();
        return ReservedDomains.Any(reserved => domain == reserved || domain.EndsWith('.' + reserved, StringComparison.Ordinal));
    }

    /// <summary>
    /// Truncates and masks a fragment so a failure report cannot itself become a
    /// copy of the secret it found.
    /// </summary>
    /// <param name="value">The offending fragment.</param>
    /// <returns>
    /// At most 12 characters, with every run of four or more digits or hex
    /// characters collapsed to a single <c>*</c>.
    /// </returns>
    public static string Mask(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var clipped = value.Length <= 12 ? value : string.Concat(value.AsSpan(0, 12), "...");
        return RunOfDigitsOrHex().Replace(clipped, "*");
    }

    /// <summary>Renders findings as one actionable line each.</summary>
    /// <param name="findings">The findings to render.</param>
    public static string Describe(IEnumerable<PiiFinding> findings) =>
        string.Join(
            Environment.NewLine,
            findings.Select(finding =>
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"  PII[{finding.Rule}] at {finding.Where}: {finding.Evidence}")));

    [GeneratedRegex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex EmailRegex();

    [GeneratedRegex(@"[0-9a-fA-F]{4,}")]
    private static partial Regex RunOfDigitsOrHex();

    /// <summary>Thrown by <see cref="AssertClean"/>.</summary>
    public sealed class PiiScrubberException : Exception
    {
        /// <summary>The findings, for a caller that wants to assert on them.</summary>
        public IReadOnlyList<PiiFinding> Findings { get; }

        internal PiiScrubberException(string where, IReadOnlyList<PiiFinding> findings)
            : base(
                $"PII/SECRET_DETECTED at {where}: {findings.Count} finding(s).{Environment.NewLine}" +
                Describe(findings) + Environment.NewLine +
                "Fixtures carry synthetic data only. Use an RFC 2606 reserved domain " +
                "(see PiiScrubber.ReservedDomains) and read credentials from configuration " +
                "placeholders, never from a literal.")
        {
            Findings = findings;
        }
    }
}