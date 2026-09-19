using DubbingPlatform.Application.Errors;
using DubbingPlatform.Application.Exceptions;
using DubbingPlatform.Application.Options;

namespace DubbingPlatform.Infrastructure.Providers.Mock;

/// <summary>
/// Resolves per-capability mock behavior and injects configured failures.
/// Resolution order: <c>Behaviors[capability]</c> (case-insensitive) else the
/// global <c>Providers:Mock</c> root. Unknown scenario strings fail fast with
/// <c>PROVIDER_CONFIGURATION_ERROR</c> (defense in depth; startup validation
/// catches them first). <c>FailRate</c> sampling uses a stable per-request
/// seed so the same input always draws the same outcome.
/// </summary>
internal static class MockBehaviorEvaluator
{
    /// <summary>
    /// Resolves the behavior for a capability (case-insensitive lookup).
    /// </summary>
    public static MockBehaviorOptions Resolve(MockBehaviorOptions root, string capability)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(capability);

        if (root.Behaviors is not null)
        {
            foreach (var pair in root.Behaviors)
            {
                if (string.Equals(pair.Key, capability, StringComparison.OrdinalIgnoreCase) && pair.Value is not null)
                {
                    return pair.Value;
                }
            }
        }

        return root;
    }

    /// <summary>
    /// Computes the effective scenario after <c>FailRate</c> sampling.
    /// </summary>
    public static string EffectiveScenario(MockBehaviorOptions behavior, string seedKey)
    {
        ArgumentNullException.ThrowIfNull(behavior);
        ArgumentException.ThrowIfNullOrWhiteSpace(seedKey);

        var scenario = MockBehaviorOptions.NormalizeScenario(behavior.Scenario);
        if (!MockBehaviorOptions.IsKnownScenario(scenario))
        {
            throw new ErrorCodeException(
                ErrorCodes.ProviderConfigurationError,
                $"Unknown mock scenario '{behavior.Scenario}'. Known: {string.Join(", ", MockBehaviorOptions.KnownScenarios)}.");
        }

        if (double.IsNaN(behavior.FailRate) || behavior.FailRate < 0 || behavior.FailRate > 1)
        {
            throw new ErrorCodeException(
                ErrorCodes.ProviderConfigurationError,
                $"Mock FailRate must be in 0..1 (was {behavior.FailRate}).");
        }

        var failWith = string.IsNullOrWhiteSpace(behavior.FailWith)
            ? null
            : MockBehaviorOptions.NormalizeScenario(behavior.FailWith);
        if (failWith is not null && !MockBehaviorOptions.IsKnownScenario(failWith))
        {
            throw new ErrorCodeException(
                ErrorCodes.ProviderConfigurationError,
                $"Unknown mock FailWith scenario '{behavior.FailWith}'. Known: {string.Join(", ", MockBehaviorOptions.KnownScenarios)}.");
        }

        if (string.Equals(scenario, MockBehaviorOptions.AsyncJob, StringComparison.Ordinal))
        {
            return scenario;
        }

        if (behavior.FailRate <= 0)
        {
            return scenario;
        }

        var draw = MockDeterminism.SeededRandom(seedKey, "failrate").NextDouble();
        if (string.Equals(scenario, MockBehaviorOptions.Success, StringComparison.Ordinal))
        {
            return draw < behavior.FailRate
                ? (failWith ?? MockBehaviorOptions.RateLimited)
                : scenario;
        }

        return draw < behavior.FailRate ? scenario : MockBehaviorOptions.Success;
    }

    /// <summary>
    /// Throws the configured failure for throwing scenarios.
    /// Timeout respects cancellation: a cancelled token surfaces
    /// <see cref="OperationCanceledException"/> (mapped to PROVIDER_TIMEOUT);
    /// otherwise a <c>PROVIDER_TIMEOUT</c> error is thrown.
    /// Returns false when the scenario is non-throwing (caller returns data).
    /// </summary>
    public static async Task<bool> ThrowIfFailingAsync(string scenario, CancellationToken cancellationToken)
    {
        var normalized = MockBehaviorOptions.NormalizeScenario(scenario);
        switch (normalized)
        {
            case MockBehaviorOptions.Success:
            case MockBehaviorOptions.LowConfidence:
            case MockBehaviorOptions.AsyncJob:
            case MockBehaviorOptions.Duplicate:
            case MockBehaviorOptions.Partial:
                return false;
            case MockBehaviorOptions.RateLimited:
                throw new ErrorCodeException(ErrorCodes.ProviderRateLimited, "Mock rate limit exceeded (fixture).");
            case MockBehaviorOptions.Timeout:
                await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken).ConfigureAwait(false);
                throw new ErrorCodeException(ErrorCodes.ProviderTimeout, "Mock provider timeout (fixture).");
            case MockBehaviorOptions.Malformed:
                throw new ErrorCodeException(ErrorCodes.ProviderInvalidResponse, "Mock malformed provider response (fixture).");
            case MockBehaviorOptions.Expired:
                throw new ErrorCodeException(ErrorCodes.ProviderFailed, "Mock job expired (fixture).");
            case MockBehaviorOptions.QuotaExhausted:
                throw new ErrorCodeException(ErrorCodes.ProviderQuotaExhausted, "Mock quota exhausted (fixture).");
            default:
                throw new ErrorCodeException(
                    ErrorCodes.ProviderConfigurationError,
                    $"Unknown mock scenario '{scenario}'.");
        }
    }

    /// <summary>
    /// Whether the scenario returns low-confidence data (quality path, no transport retry).
    /// </summary>
    public static bool IsLowConfidence(string scenario)
    {
        return string.Equals(MockBehaviorOptions.NormalizeScenario(scenario), MockBehaviorOptions.LowConfidence, StringComparison.Ordinal);
    }

    /// <summary>
    /// Whether the scenario returns partial data.
    /// </summary>
    public static bool IsPartial(string scenario)
    {
        return string.Equals(MockBehaviorOptions.NormalizeScenario(scenario), MockBehaviorOptions.Partial, StringComparison.Ordinal);
    }

    /// <summary>
    /// Whether the scenario returns duplicate-tagged data.
    /// </summary>
    public static bool IsDuplicate(string scenario)
    {
        return string.Equals(MockBehaviorOptions.NormalizeScenario(scenario), MockBehaviorOptions.Duplicate, StringComparison.Ordinal);
    }
}
