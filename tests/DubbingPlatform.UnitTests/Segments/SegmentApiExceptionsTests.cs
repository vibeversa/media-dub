// Task 039C: selection-version segment unit gap closure.

using DubbingPlatform.Application.Errors;
using DubbingPlatform.Application.Exceptions;
using DubbingPlatform.Application.Segments;

namespace DubbingPlatform.UnitTests.Segments;

/// <summary>
/// Per-exception contract for <c>SegmentApiExceptions.cs</c>: every exception
/// type's catalogued public code, the HTTP status derived from it, and the exact
/// public message string. The structured refresh payloads (version ids, stage,
/// attempt) are covered by <see cref="SegmentApiContractTests"/>; this file pins
/// the messages and the boundary values clients surface verbatim. No HTTP, no
/// database: the types are pure value carriers.
/// </summary>
public sealed class SegmentApiExceptionsTests
{
    [Fact]
    public void Selection_Conflict_Code_Status_And_Message()
    {
        var ex = new SelectionConflictApiException(7, null, null);

        Assert.Equal(ErrorCodes.SelectionConflict, ex.ErrorCode);
        Assert.Equal(409, ex.StatusCode);
        Assert.Equal(
            "Selection conflict: expected version did not match current version 7. Refresh and retry.",
            ex.Message);
        Assert.Null(ex.CurrentTranscriptVersionId);
        Assert.Null(ex.CurrentTranslationVersionId);
    }

    [Theory]
    [InlineData(0, "0")]
    [InlineData(1, "1")]
    [InlineData(int.MaxValue, "2147483647")]
    public void Selection_Conflict_Renders_Every_Version_Invariantly(int version, string rendered)
    {
        var ex = new SelectionConflictApiException(version, null, null);

        Assert.Equal(
            string.Concat(
                "Selection conflict: expected version did not match current version ",
                rendered,
                ". Refresh and retry."),
            ex.Message);
        Assert.Equal(version, ex.CurrentSelectionVersion);
    }

    [Fact]
    public void Selection_Conflict_Exposes_Refresh_Details_And_Version_Ids()
    {
        var transcript = Guid.NewGuid();
        var translation = Guid.NewGuid();
        var ex = new SelectionConflictApiException(3, transcript, translation);

        var details = ex.GetErrorDetails();
        Assert.Equal(3, details["currentSelectionVersion"]);

        var ids = Assert.IsType<Dictionary<string, object?>>(details["currentVersionIds"]);
        Assert.Equal(transcript.ToString("D"), ids["transcriptVersionId"]);
        Assert.Equal(translation.ToString("D"), ids["translationVersionId"]);

        // Dashed form, never the compact one, so clients can round-trip it.
        Assert.DoesNotContain(transcript.ToString("N"), ids["transcriptVersionId"]!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Retry_Active_Code_Status_And_Message()
    {
        var ex = new SegmentRetryActiveApiException(Guid.NewGuid(), "QualityControl", 2);

        Assert.Equal(ErrorCodes.SegmentRetryActive, ex.ErrorCode);
        Assert.Equal(409, ex.StatusCode);
        Assert.Equal(
            "Segment already has an active retry for stage 'QualityControl'. Poll the existing execution.",
            ex.Message);
        Assert.Equal("QualityControl", ex.Stage);
        Assert.Equal(2, ex.Attempt);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(int.MaxValue)]
    public void Retry_Active_Attempt_Boundaries_Are_Carried_Verbatim(int attempt)
    {
        var ex = new SegmentRetryActiveApiException(Guid.NewGuid(), "Transcription", attempt);

        Assert.Equal(attempt, Assert.IsType<int>(ex.GetErrorDetails()["attempt"]));
        Assert.Equal(attempt, ex.Attempt);
    }

    [Fact]
    public void Version_Not_Found_Code_Status_And_Message()
    {
        var ex = new VersionNotFoundApiException("Transcript version 'tv_01' was not found.");

        Assert.Equal(ErrorCodes.VersionNotFound, ex.ErrorCode);
        Assert.Equal(404, ex.StatusCode);
        Assert.Equal("Transcript version 'tv_01' was not found.", ex.Message);
    }

    [Fact]
    public void Version_Segment_Mismatch_Code_Status_And_Message()
    {
        var ex = new VersionSegmentMismatchApiException("Version belongs to another segment.");

        Assert.Equal(ErrorCodes.VersionSegmentMismatch, ex.ErrorCode);
        Assert.Equal(400, ex.StatusCode);
        Assert.Equal("Version belongs to another segment.", ex.Message);
    }

    [Fact]
    public void Segment_Text_Empty_Code_Status_And_Message()
    {
        var ex = new SegmentTextEmptyApiException("Manual edit text must not be empty.");

        Assert.Equal(ErrorCodes.SegmentTextEmpty, ex.ErrorCode);
        Assert.Equal(400, ex.StatusCode);
        Assert.Equal("Manual edit text must not be empty.", ex.Message);
    }

    [Fact]
    public void Only_The_Conflict_Variants_Carry_Envelope_Details()
    {
        Assert.IsAssignableFrom<IErrorDetailsProvider>(new SelectionConflictApiException(1, null, null));
        Assert.IsAssignableFrom<IErrorDetailsProvider>(new SegmentRetryActiveApiException(Guid.NewGuid(), "MediaAnalysis", 1));

        Assert.False(typeof(IErrorDetailsProvider).IsAssignableFrom(typeof(VersionNotFoundApiException)));
        Assert.False(typeof(IErrorDetailsProvider).IsAssignableFrom(typeof(VersionSegmentMismatchApiException)));
        Assert.False(typeof(IErrorDetailsProvider).IsAssignableFrom(typeof(SegmentTextEmptyApiException)));
    }

    [Fact]
    public void Every_Exception_Code_Is_Catalogued_And_Status_Matches_The_Catalog()
    {
        AppException[] exceptions =
        [
            new SelectionConflictApiException(1, null, null),
            new VersionNotFoundApiException("x"),
            new VersionSegmentMismatchApiException("x"),
            new SegmentTextEmptyApiException("x"),
            new SegmentRetryActiveApiException(Guid.NewGuid(), "MediaAnalysis", 0),
        ];

        foreach (var exception in exceptions)
        {
            Assert.True(ErrorCodes.IsKnown(exception.ErrorCode), exception.ErrorCode);
            Assert.Equal(ErrorCodes.StatusFor(exception.ErrorCode), exception.StatusCode);
            Assert.False(string.IsNullOrWhiteSpace(exception.Message));
        }

        // Distinct codes per type: no aliasing between the 400/404/409 variants.
        Assert.Equal(5, exceptions.Select(e => e.ErrorCode).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Version_Mismatch_Variants_Stay_Distinguishable_By_Status()
    {
        // Same "version" family, different failure modes; clients branch on status.
        Assert.Equal(404, new VersionNotFoundApiException("x").StatusCode);
        Assert.Equal(400, new VersionSegmentMismatchApiException("x").StatusCode);
        Assert.Equal(409, new SelectionConflictApiException(2, Guid.NewGuid(), Guid.NewGuid()).StatusCode);
    }
}
