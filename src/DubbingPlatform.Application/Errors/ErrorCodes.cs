namespace DubbingPlatform.Application.Errors;

/// <summary>
/// Public error-code catalog. Every API error response carries exactly one of
/// these codes in its envelope; richer internal failure detail stays out of
/// public responses.
/// </summary>
public static class ErrorCodes
{
    public const string ValidationFailed = "VALIDATION_FAILED";
    public const string Unauthorized = "UNAUTHORIZED";
    public const string Forbidden = "FORBIDDEN";
    public const string NotFound = "NOT_FOUND";
    public const string Conflict = "CONFLICT";
    public const string MediaUnsupported = "MEDIA_UNSUPPORTED";
    public const string MediaCorrupt = "MEDIA_CORRUPT";
    public const string UploadIncomplete = "UPLOAD_INCOMPLETE";
    public const string DuplicateMedia = "DUPLICATE_MEDIA";
    public const string ProviderConfigurationError = "PROVIDER_CONFIGURATION_ERROR";
    public const string ProviderRateLimited = "PROVIDER_RATE_LIMITED";
    public const string ProviderTimeout = "PROVIDER_TIMEOUT";
    public const string ProviderInvalidResponse = "PROVIDER_INVALID_RESPONSE";
    public const string ProviderQuotaExhausted = "PROVIDER_QUOTA_EXHAUSTED";
    public const string ProviderFailed = "PROVIDER_FAILED";
    public const string QcBlocked = "QC_BLOCKED";
    public const string QuotaExceeded = "QUOTA_EXCEEDED";
    public const string RateLimited = "RATE_LIMITED";
    public const string ResourceExhausted = "RESOURCE_EXHAUSTED";
    public const string ArtifactUnavailable = "ARTIFACT_UNAVAILABLE";
    public const string ArtifactChecksumMismatch = "ARTIFACT_CHECKSUM_MISMATCH";
    public const string LeaseLost = "LEASE_LOST";
    public const string PipelineInvariantViolation = "PIPELINE_INVARIANT_VIOLATION";
    public const string ManualReviewRequired = "MANUAL_REVIEW_REQUIRED";
    public const string ExportNotReady = "EXPORT_NOT_READY";
    public const string StorageUnavailable = "STORAGE_UNAVAILABLE";
    public const string ConsentRequired = "CONSENT_REQUIRED";
    public const string PolicyDenied = "POLICY_DENIED";
    public const string InternalError = "INTERNAL_ERROR";

    /// <summary>
    /// All 29 public error codes.
    /// </summary>
    public static readonly string[] All =
    [
        ValidationFailed,
        Unauthorized,
        Forbidden,
        NotFound,
        Conflict,
        MediaUnsupported,
        MediaCorrupt,
        UploadIncomplete,
        DuplicateMedia,
        ProviderConfigurationError,
        ProviderRateLimited,
        ProviderTimeout,
        ProviderInvalidResponse,
        ProviderQuotaExhausted,
        ProviderFailed,
        QcBlocked,
        QuotaExceeded,
        RateLimited,
        ResourceExhausted,
        ArtifactUnavailable,
        ArtifactChecksumMismatch,
        LeaseLost,
        PipelineInvariantViolation,
        ManualReviewRequired,
        ExportNotReady,
        StorageUnavailable,
        ConsentRequired,
        PolicyDenied,
        InternalError,
    ];

    /// <summary>
    /// Determines whether the given code is a known public error code.
    /// </summary>
    public static bool IsKnown(string? code)
    {
        return !string.IsNullOrEmpty(code) && All.Contains(code, StringComparer.Ordinal);
    }

    /// <summary>
    /// Maps a public error code to its default HTTP status code.
    /// </summary>
    public static int StatusFor(string code)
    {
        return code switch
        {
            ValidationFailed => 400,
            Unauthorized => 401,
            Forbidden => 403,
            NotFound => 404,
            Conflict => 409,
            MediaUnsupported => 415,
            MediaCorrupt => 422,
            UploadIncomplete => 400,
            DuplicateMedia => 409,
            ProviderConfigurationError => 500,
            ProviderRateLimited => 429,
            ProviderTimeout => 504,
            ProviderInvalidResponse => 502,
            ProviderQuotaExhausted => 429,
            ProviderFailed => 502,
            QcBlocked => 422,
            QuotaExceeded => 429,
            RateLimited => 429,
            ResourceExhausted => 503,
            ArtifactUnavailable => 404,
            ArtifactChecksumMismatch => 422,
            LeaseLost => 409,
            PipelineInvariantViolation => 500,
            ManualReviewRequired => 409,
            ExportNotReady => 409,
            StorageUnavailable => 503,
            ConsentRequired => 403,
            PolicyDenied => 403,
            InternalError => 500,
            _ => 500,
        };
    }
}
