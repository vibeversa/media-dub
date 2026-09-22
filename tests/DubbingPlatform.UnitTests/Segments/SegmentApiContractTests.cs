using DubbingPlatform.Application.Errors;
using DubbingPlatform.Application.Segments;

namespace DubbingPlatform.UnitTests.Segments;

/// <summary>
/// Hermetic Task 009 coverage: new error codes, conflict refresh payload,
/// retry-active details, and plain-text sanitization. Docker-backed
/// <c>SegmentEditingApiTests</c> replays the HTTP paths in CI.
/// </summary>
public sealed class SegmentApiContractTests
{
    [Fact]
    public void New_Segment_Codes_Map()
    {
        Assert.Equal(409, ErrorCodes.StatusFor(ErrorCodes.SelectionConflict));
        Assert.Equal(404, ErrorCodes.StatusFor(ErrorCodes.VersionNotFound));
        Assert.Equal(400, ErrorCodes.StatusFor(ErrorCodes.VersionSegmentMismatch));
        Assert.Equal(400, ErrorCodes.StatusFor(ErrorCodes.SegmentTextEmpty));
        Assert.Equal(409, ErrorCodes.StatusFor(ErrorCodes.SegmentRetryActive));
        Assert.Equal(52, ErrorCodes.All.Length);
    }

    [Fact]
    public void Selection_Conflict_Carries_Refresh_Payload()
    {
        var transcript = Guid.NewGuid();
        var ex = new SelectionConflictApiException(2, transcript, null);
        Assert.Equal(ErrorCodes.SelectionConflict, ex.ErrorCode);
        Assert.Equal(409, ex.StatusCode);
        var details = ex.GetErrorDetails();
        Assert.Equal(2, details["currentSelectionVersion"]);
        var ids = Assert.IsType<Dictionary<string, object?>>(details["currentVersionIds"]);
        Assert.Equal(transcript.ToString("D"), ids["transcriptVersionId"]);
        Assert.Null(ids["translationVersionId"]);
    }

    [Fact]
    public void Retry_Active_Carries_Execution()
    {
        var execution = Guid.NewGuid();
        var ex = new SegmentRetryActiveApiException(execution, "Transcription", 1);
        Assert.Equal(ErrorCodes.SegmentRetryActive, ex.ErrorCode);
        Assert.Equal(409, ex.StatusCode);
        var details = ex.GetErrorDetails();
        Assert.Equal(execution.ToString("D"), details["executionId"]);
        Assert.Equal("Transcription", details["stage"]);
    }

    [Fact]
    public void Manual_Text_Strips_Html_And_Rejects_Empty()
    {
        Assert.Equal("hello", SegmentSelectionService.RequireManualText("<b>hello</b>"));
        Assert.Throws<global::DubbingPlatform.Domain.Exceptions.DomainException>(
            () => SegmentSelectionService.RequireManualText("<br/>"));
        Assert.Throws<global::DubbingPlatform.Domain.Exceptions.DomainException>(
            () => SegmentSelectionService.RequireManualText("   "));
    }
}
