using DubbingPlatform.Application.Errors;

namespace DubbingPlatform.Application.Segments;

/// <summary>
/// Task 009 segment API errors. Each carries a catalogued code plus structured
/// refresh details so clients can recover without a second read.
/// </summary>
public sealed class SelectionConflictApiException : Exceptions.AppException, Exceptions.IErrorDetailsProvider
{
    public SelectionConflictApiException(
        int currentSelectionVersion,
        Guid? currentTranscriptVersionId,
        Guid? currentTranslationVersionId)
        : base(
            ErrorCodes.SelectionConflict,
            string.Concat(
                "Selection conflict: expected version did not match current version ",
                currentSelectionVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ". Refresh and retry."))
    {
        CurrentSelectionVersion = currentSelectionVersion;
        CurrentTranscriptVersionId = currentTranscriptVersionId;
        CurrentTranslationVersionId = currentTranslationVersionId;
    }

    public int CurrentSelectionVersion { get; }

    public Guid? CurrentTranscriptVersionId { get; }

    public Guid? CurrentTranslationVersionId { get; }

    public override int StatusCode => ErrorCodes.StatusFor(ErrorCode);

    public IReadOnlyDictionary<string, object?> GetErrorDetails()
    {
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["currentSelectionVersion"] = CurrentSelectionVersion,
            ["currentVersionIds"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["transcriptVersionId"] = CurrentTranscriptVersionId?.ToString("D"),
                ["translationVersionId"] = CurrentTranslationVersionId?.ToString("D"),
            },
        };
    }
}

/// <summary>
/// Referenced content version does not exist (404 VERSION_NOT_FOUND).
/// </summary>
public sealed class VersionNotFoundApiException : Exceptions.AppException
{
    public VersionNotFoundApiException(string message)
        : base(ErrorCodes.VersionNotFound, message)
    {
    }

    public override int StatusCode => ErrorCodes.StatusFor(ErrorCode);
}

/// <summary>
/// Referenced version belongs to another segment (400 VERSION_SEGMENT_MISMATCH).
/// </summary>
public sealed class VersionSegmentMismatchApiException : Exceptions.AppException
{
    public VersionSegmentMismatchApiException(string message)
        : base(ErrorCodes.VersionSegmentMismatch, message)
    {
    }

    public override int StatusCode => ErrorCodes.StatusFor(ErrorCode);
}

/// <summary>
/// Manual edit text empty/whitespace (400 SEGMENT_TEXT_EMPTY).
/// </summary>
public sealed class SegmentTextEmptyApiException : Exceptions.AppException
{
    public SegmentTextEmptyApiException(string message)
        : base(ErrorCodes.SegmentTextEmpty, message)
    {
    }

    public override int StatusCode => ErrorCodes.StatusFor(ErrorCode);
}

/// <summary>
/// Segment already has an active retry execution (409 SEGMENT_RETRY_ACTIVE).
/// Details carry the existing execution so polling clients can reuse it.
/// </summary>
public sealed class SegmentRetryActiveApiException : Exceptions.AppException, Exceptions.IErrorDetailsProvider
{
    public SegmentRetryActiveApiException(Guid executionId, string stage, int attempt)
        : base(
            ErrorCodes.SegmentRetryActive,
            string.Concat("Segment already has an active retry for stage '", stage, "'. Poll the existing execution."))
    {
        ExecutionId = executionId;
        Stage = stage;
        Attempt = attempt;
    }

    public Guid ExecutionId { get; }

    public string Stage { get; }

    public int Attempt { get; }

    public override int StatusCode => ErrorCodes.StatusFor(ErrorCode);

    public IReadOnlyDictionary<string, object?> GetErrorDetails()
    {
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["executionId"] = ExecutionId.ToString("D"),
            ["stage"] = Stage,
            ["attempt"] = Attempt,
        };
    }
}
