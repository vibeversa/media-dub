using DubbingPlatform.Application.Services;
using DubbingPlatform.Domain.Enums;

namespace DubbingPlatform.UnitTests.Pipeline;

/// <summary>
/// Hermetic QC planner tests (no Docker): verdict classification precedence,
/// settings parsers, loudness-profile resolution, and duck-graph parsing use
/// the pure <see cref="QualityControlService"/> helpers directly. DB
/// persistence, artifact publication, and signal measurement live in the
/// service/FFmpeg layers (PG + ffmpeg, covered in CI).
/// </summary>
public sealed class QcLogicTests
{
    private static QcFinding Finding(QualityStatus status, string code = "QC_TEST")
    {
        return new QcFinding(
            code, ScopeType.Project, Guid.NewGuid().ToString("D"), null,
            status, QualityControlService.SeverityWarning, "test finding.", null, null);
    }

    [Fact]
    public void Classify_Empty_Passes()
    {
        Assert.Equal(QualityStatus.Pass, QualityControlService.Classify([]));
    }

    [Fact]
    public void Classify_Warnings_Pass_With_Warnings()
    {
        var verdict = QualityControlService.Classify(
            [Finding(QualityStatus.PassWithWarnings, "QC_GAP")]);

        Assert.Equal(QualityStatus.PassWithWarnings, verdict);
    }

    [Fact]
    public void Classify_Blocked_Wins()
    {
        var verdict = QualityControlService.Classify(
        [
            Finding(QualityStatus.PassWithWarnings, "QC_GAP"),
            Finding(QualityStatus.RetryRequired, QualityControlService.CodeStaleMetadata),
            Finding(QualityStatus.ManualReviewRequired, QualityControlService.CodeSyncFailure),
            Finding(QualityStatus.Blocked, QualityControlService.CodeCorrupt),
        ]);

        Assert.Equal(QualityStatus.Blocked, verdict);
    }

    [Fact]
    public void Classify_Review_Beats_Retry()
    {
        var verdict = QualityControlService.Classify(
        [
            Finding(QualityStatus.RetryRequired, QualityControlService.CodeStaleMetadata),
            Finding(QualityStatus.ManualReviewRequired, QualityControlService.CodeSyncFailure),
        ]);

        Assert.Equal(QualityStatus.ManualReviewRequired, verdict);
    }

    [Fact]
    public void ParseDuckDb_Reads_Volume()
    {
        Assert.Equal(-12.0, QualityControlService.ParseDuckDb("premix: [bgcomp]volume=-12dB[bgduck]"));
        Assert.Null(QualityControlService.ParseDuckDb("[dialog]anull[mix]"));
        Assert.Null(QualityControlService.ParseDuckDb(null));
        Assert.Null(QualityControlService.ParseDuckDb("volume=bad-dB"));
    }

    [Fact]
    public void ParseTerminologyStrict_Fail_Closed()
    {
        Assert.Equal(true, QualityControlService.ParseTerminologyStrict("{\"terminologyStrict\": true}"));
        Assert.Equal(false, QualityControlService.ParseTerminologyStrict("{\"terminologyStrict\": false}"));
        Assert.Null(QualityControlService.ParseTerminologyStrict("{}"));
        Assert.Null(QualityControlService.ParseTerminologyStrict(null));
        Assert.Null(QualityControlService.ParseTerminologyStrict("not-json"));
        Assert.Null(QualityControlService.ParseTerminologyStrict("{\"terminologyStrict\": \"yes\"}"));
    }

    [Fact]
    public void Loudness_Profile_Resolution()
    {
        Assert.Equal(-16.0, QualityControlService.ResolveTarget("web").IntegratedLufs);
        Assert.Equal(-23.0, QualityControlService.ResolveTarget("broadcast").IntegratedLufs);
        Assert.Equal(-16.0, QualityControlService.ResolveTarget(null).IntegratedLufs);
        Assert.Equal("web", QualityControlService.ParseLoudnessProfile("{}"));
        Assert.Equal("broadcast", QualityControlService.ParseLoudnessProfile("{\"loudnessProfile\": \"broadcast\"}"));
        Assert.False(QualityControlService.HasLoudnessProfile("{}"));
        Assert.True(QualityControlService.HasLoudnessProfile("{\"loudnessProfile\": \"web\"}"));
    }
}
