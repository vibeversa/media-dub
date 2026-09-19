using DubbingPlatform.Domain.Enums;

namespace DubbingPlatform.Application.Providers;

/// <summary>
/// What the pipeline may do with an outcome. Transport retries (broker/HTTP
/// redelivery) are governed by <see cref="AllowRetry"/>; quality gates never
/// set it (see <see cref="OutcomePolicy"/>).
/// </summary>
public sealed record OutcomeDecision(
    bool AllowRetry,
    bool AllowFallback,
    bool AllowReview,
    bool FailFast);

/// <summary>
/// Maps provider outcomes to retry/fallback/review/fail-fast behavior.
/// Quality outcomes never trigger transport retries: <c>QualityBelowThreshold</c>
/// allows review only. Policy/unsupported outcomes fail fast. Cancelled allows
/// nothing (terminal by operator intent).
/// </summary>
public static class OutcomePolicy
{
    public static OutcomeDecision Decide(OutcomeClass outcome)
    {
        return outcome switch
        {
            OutcomeClass.Success => new OutcomeDecision(false, false, false, false),
            OutcomeClass.ProviderUnavailable => new OutcomeDecision(true, true, false, false),
            OutcomeClass.ProviderRateLimited => new OutcomeDecision(true, true, false, false),
            OutcomeClass.ProviderTransientFailure => new OutcomeDecision(true, true, false, false),
            OutcomeClass.ProviderTimeout => new OutcomeDecision(true, true, false, false),
            OutcomeClass.ProviderPermanentFailure => new OutcomeDecision(false, true, false, false),
            OutcomeClass.ProviderInvalidResponse => new OutcomeDecision(false, true, true, false),
            OutcomeClass.QualityBelowThreshold => new OutcomeDecision(false, false, true, false),
            OutcomeClass.PolicyRejected => new OutcomeDecision(false, false, false, true),
            OutcomeClass.UnsupportedCapability => new OutcomeDecision(false, false, false, true),
            OutcomeClass.Cancelled => new OutcomeDecision(false, false, false, false),
            _ => new OutcomeDecision(false, false, false, true),
        };
    }
}
