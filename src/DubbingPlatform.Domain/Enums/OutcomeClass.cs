namespace DubbingPlatform.Domain.Enums;

public enum OutcomeClass
{
    Success,
    ProviderUnavailable,
    ProviderRateLimited,
    ProviderTransientFailure,
    ProviderPermanentFailure,
    ProviderInvalidResponse,
    ProviderTimeout,
    QualityBelowThreshold,
    PolicyRejected,
    UnsupportedCapability,
    Cancelled
}
