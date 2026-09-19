using Microsoft.Extensions.Options;

namespace DubbingPlatform.Application.Options;

/// <summary>
/// Deterministic mock behavior. Binds to the <c>Providers:Mock</c> section.
/// <see cref="Scenario"/> selects the fixture script; <see cref="FailRate"/>
/// (0..1) adds probabilistic injection of <see cref="FailWith"/> (or the
/// scenario itself) using a stable per-request seed; <see cref="Behaviors"/>
/// holds per-capability overrides keyed by capability name
/// (for example <c>Providers:Mock:Behaviors:Transcription:Scenario</c>).
/// </summary>
public sealed class MockBehaviorOptions
{
    public const string SectionName = "Providers:Mock";

    public const string Success = "success";

    public const string LowConfidence = "low-confidence";

    public const string RateLimited = "rate-limited";

    public const string Timeout = "timeout";

    public const string Malformed = "malformed";

    public const string AsyncJob = "async-job";

    public const string Duplicate = "duplicate";

    public const string Partial = "partial";

    public const string Expired = "expired";

    public const string QuotaExhausted = "quota-exhausted";

    /// <summary>
    /// All known scenario names (lowercase, hyphenated).
    /// </summary>
    public static readonly string[] KnownScenarios =
    [
        Success,
        LowConfidence,
        RateLimited,
        Timeout,
        Malformed,
        AsyncJob,
        Duplicate,
        Partial,
        Expired,
        QuotaExhausted,
    ];

    public string Scenario { get; set; } = Success;

    public double FailRate { get; set; }

    public string? FailWith { get; set; }

    /// <summary>
    /// Per-capability overrides. Keys are capability names
    /// (for example <c>Transcription</c>); lookup is case-insensitive.
    /// </summary>
    public Dictionary<string, MockBehaviorOptions> Behaviors { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Determines whether the scenario name is known (case-insensitive, trimmed).
    /// </summary>
    public static bool IsKnownScenario(string? scenario)
    {
        if (string.IsNullOrWhiteSpace(scenario))
        {
            return false;
        }

        var normalized = scenario.Trim();
        foreach (var known in KnownScenarios)
        {
            if (string.Equals(known, normalized, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Normalizes a scenario name to lowercase (trimmed). Unknown names are
    /// returned trimmed-lowercase so callers fail fast with a clear message.
    /// </summary>
    public static string NormalizeScenario(string? scenario)
    {
        return (scenario ?? string.Empty).Trim().ToLowerInvariant();
    }
}

/// <summary>
/// Fail-fast startup validation for <see cref="MockBehaviorOptions"/>.
/// Unknown scenario strings fail fast; <c>FailRate</c> must be in 0..1.
/// </summary>
public sealed class MockBehaviorOptionsValidator : IValidateOptions<MockBehaviorOptions>
{
    public ValidateOptionsResult Validate(string? name, MockBehaviorOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failure = ValidateSingle(options, "Mock");
        if (failure is not null)
        {
            return failure;
        }

        if (options.Behaviors is null)
        {
            return ValidateOptionsResult.Fail($"{nameof(MockBehaviorOptions)}.{nameof(MockBehaviorOptions.Behaviors)} must not be null.");
        }

        foreach (var pair in options.Behaviors)
        {
            if (string.IsNullOrWhiteSpace(pair.Key))
            {
                return ValidateOptionsResult.Fail($"{nameof(MockBehaviorOptions)}.{nameof(MockBehaviorOptions.Behaviors)} must not contain empty capability names.");
            }

            if (pair.Value is null)
            {
                return ValidateOptionsResult.Fail($"{nameof(MockBehaviorOptions)}.{nameof(MockBehaviorOptions.Behaviors)}['{pair.Key}'] must not be null.");
            }

            var nested = ValidateSingle(pair.Value, $"Behaviors['{pair.Key}']");
            if (nested is not null)
            {
                return nested;
            }
        }

        return ValidateOptionsResult.Success;
    }

    private static ValidateOptionsResult? ValidateSingle(MockBehaviorOptions options, string prefix)
    {
        if (!MockBehaviorOptions.IsKnownScenario(options.Scenario))
        {
            return ValidateOptionsResult.Fail($"{nameof(MockBehaviorOptions)}.{prefix}.{nameof(MockBehaviorOptions.Scenario)} has unknown scenario '{options.Scenario}'. Known: {string.Join(", ", MockBehaviorOptions.KnownScenarios)}.");
        }

        if (double.IsNaN(options.FailRate) || options.FailRate < 0 || options.FailRate > 1)
        {
            return ValidateOptionsResult.Fail($"{nameof(MockBehaviorOptions)}.{prefix}.{nameof(MockBehaviorOptions.FailRate)} must be in 0..1.");
        }

        if (!string.IsNullOrWhiteSpace(options.FailWith) && !MockBehaviorOptions.IsKnownScenario(options.FailWith))
        {
            return ValidateOptionsResult.Fail($"{nameof(MockBehaviorOptions)}.{prefix}.{nameof(MockBehaviorOptions.FailWith)} has unknown scenario '{options.FailWith}'. Known: {string.Join(", ", MockBehaviorOptions.KnownScenarios)}.");
        }

        return null;
    }
}
