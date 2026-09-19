namespace DubbingPlatform.Domain.Enums;

/// <summary>
/// Internal failure category for stage execution and recovery decisions.
/// This is distinct from public API error codes surfaced to clients.
/// </summary>
public enum FailureCategory
{
    Validation,
    MediaUnsupported,
    MediaCorrupt,
    ProviderTransient,
    ProviderPermanent,
    ProviderRateLimited,
    ProviderTimeout,
    ProviderInvalidResponse,
    QuotaExceeded,
    RateLimited,
    LeaseLost,
    Cancelled,
    PolicyDenied,
    ConsentRequired,
    ConfigurationError,
    StorageUnavailable,
    ChecksumMismatch,
    InvariantViolation,
    Unknown
}
