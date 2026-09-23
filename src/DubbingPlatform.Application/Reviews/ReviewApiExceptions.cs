using DubbingPlatform.Application.Errors;

namespace DubbingPlatform.Application.Reviews;

/// <summary>
/// Task 011 review API errors. Each carries a catalogued code plus structured
/// refresh details so clients can recover without a second read. The review
/// <c>version</c> is the count of <c>ReviewDecision</c> rows for the review
/// (0 when untouched); every mutation appends exactly one decision row, so
/// <c>expectedVersion</c> guards all writers uniformly including the legacy
/// Plan A approve/reject/requeue paths.
/// </summary>
public sealed class ReviewVersionConflictException : Exceptions.AppException, Exceptions.IErrorDetailsProvider
{
    public ReviewVersionConflictException(int currentVersion)
        : base(
            ErrorCodes.ReviewVersionConflict,
            string.Concat(
                "Review version conflict: expected version did not match current version ",
                currentVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ". Refresh and retry."))
    {
        CurrentVersion = currentVersion;
    }

    public int CurrentVersion { get; }

    public override int StatusCode => ErrorCodes.StatusFor(ErrorCode);

    public IReadOnlyDictionary<string, object?> GetErrorDetails()
    {
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["currentVersion"] = CurrentVersion,
        };
    }
}

/// <summary>
/// Mutation reason missing or blank (400 REVIEW_REASON_REQUIRED).
/// </summary>
public sealed class ReviewReasonRequiredException : Exceptions.AppException
{
    public ReviewReasonRequiredException()
        : base(ErrorCodes.ReviewReasonRequired, "Review mutations require a non-empty reason (max 500 characters).")
    {
    }

    public override int StatusCode => ErrorCodes.StatusFor(ErrorCode);
}

/// <summary>
/// Resolve/dismiss/resolve-with-edit on a non-open review
/// (409 REVIEW_ALREADY_RESOLVED). Details carry the current status so clients
/// can refresh without a second read. Idempotent-key replays still return 200.
/// </summary>
public sealed class ReviewAlreadyResolvedException : Exceptions.AppException, Exceptions.IErrorDetailsProvider
{
    public ReviewAlreadyResolvedException(string currentStatus)
        : base(
            ErrorCodes.ReviewAlreadyResolved,
            string.Concat("Review is already '", currentStatus, "' and cannot be resolved again."))
    {
        CurrentStatus = currentStatus;
    }

    public string CurrentStatus { get; }

    public override int StatusCode => ErrorCodes.StatusFor(ErrorCode);

    public IReadOnlyDictionary<string, object?> GetErrorDetails()
    {
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["currentStatus"] = CurrentStatus,
        };
    }
}

/// <summary>
/// Reopen on an already-open review (409 REVIEW_NOT_RESOLVED).
/// </summary>
public sealed class ReviewNotResolvedException : Exceptions.AppException, Exceptions.IErrorDetailsProvider
{
    public ReviewNotResolvedException(string currentStatus)
        : base(
            ErrorCodes.ReviewNotResolved,
            string.Concat("Review is '", currentStatus, "' and cannot be reopened."))
    {
        CurrentStatus = currentStatus;
    }

    public string CurrentStatus { get; }

    public override int StatusCode => ErrorCodes.StatusFor(ErrorCode);

    public IReadOnlyDictionary<string, object?> GetErrorDetails()
    {
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["currentStatus"] = CurrentStatus,
        };
    }
}

/// <summary>
/// Resolve-with-edit with empty edit text (400 REVIEW_EDIT_EMPTY).
/// </summary>
public sealed class ReviewEditEmptyException : Exceptions.AppException
{
    public ReviewEditEmptyException()
        : base(ErrorCodes.ReviewEditEmpty, "Resolve-with-edit requires non-empty edit text.")
    {
    }

    public override int StatusCode => ErrorCodes.StatusFor(ErrorCode);
}
