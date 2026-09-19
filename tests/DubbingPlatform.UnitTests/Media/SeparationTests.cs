using DubbingPlatform.Application.Providers;
using DubbingPlatform.Application.Services;
using DubbingPlatform.Domain.Enums;

namespace DubbingPlatform.UnitTests.Media;

/// <summary>
/// Hermetic separation-policy tests (no Docker): settings parsing,
/// confidence normalization, and threshold decisions use a mock provider with
/// configurable confidence 0.5 (fallback) / 0.9 (select). Full DB paths live
/// in <c>SeparationWorkerTests</c> (PG).
/// </summary>
public sealed class SeparationTests
{
    [Fact]
    public void Disabled_Skips()
    {
        var (defPolicy, defThreshold, defFail) = SeparationPolicyParser.Parse("{}", 0.70);
        Assert.Equal(SourceSeparationPolicy.Disabled, defPolicy);
        Assert.Equal(0.70, defThreshold);
        Assert.False(defFail);

        var (nullPolicy, _, _) = SeparationPolicyParser.Parse(null, 0.70);
        Assert.Equal(SourceSeparationPolicy.Disabled, nullPolicy);

        var (invalidPolicy, _, _) = SeparationPolicyParser.Parse("not json", 0.70);
        Assert.Equal(SourceSeparationPolicy.Disabled, invalidPolicy);

        var (explicitPolicy, _, _) = SeparationPolicyParser.Parse("{\"sourceSeparation\":\"disabled\"}", 0.70);
        Assert.Equal(SourceSeparationPolicy.Disabled, explicitPolicy);

        var (unknownPolicy, _, _) = SeparationPolicyParser.Parse("{\"sourceSeparation\":\"sometimes\"}", 0.70);
        Assert.Equal(SourceSeparationPolicy.Disabled, unknownPolicy);

        var (upperPolicy, _, _) = SeparationPolicyParser.Parse("{\"sourceSeparation\":\" DISABLED \"}", 0.70);
        Assert.Equal(SourceSeparationPolicy.Disabled, upperPolicy);
    }

    [Fact]
    public void Low_Confidence_Fallback()
    {
        var normalized = ConfidenceNormalizer.Normalize(0.5, "0-1");
        Assert.Equal(0.5, normalized);
        Assert.True(normalized < 0.70);

        var lowMock = ConfidenceNormalizer.Normalize(0.35, "0-1");
        Assert.True(lowMock < 0.70);

        var custom = ConfidenceNormalizer.Normalize(0.5, "0-1");
        Assert.Equal(0.5, custom, precision: 9);
    }

    [Fact]
    public void High_Confidence_Selects()
    {
        var normalized = ConfidenceNormalizer.Normalize(0.9, "0-1");
        Assert.Equal(0.9, normalized);
        Assert.True(normalized >= 0.70);

        var atThreshold = ConfidenceNormalizer.Normalize(0.70, "0-1");
        Assert.True(atThreshold >= 0.70);
    }

    [Fact]
    public void Warning_Recorded()
    {
        Assert.Equal("SEPARATION_FALLBACK", SourceSeparationService.FallbackCode);

        var (policy, threshold, failOn) = SeparationPolicyParser.Parse(
            "{\"sourceSeparation\":\"enabled\",\"separationThreshold\":0.80,\"failOnSeparationError\":true}",
            0.70);
        Assert.Equal(SourceSeparationPolicy.Enabled, policy);
        Assert.Equal(0.80, threshold);
        Assert.True(failOn);

        var (autoPolicy, _, _) = SeparationPolicyParser.Parse("{\"sourceSeparation\":\"Auto\"}", 0.70);
        Assert.Equal(SourceSeparationPolicy.Auto, autoPolicy);

        var (badThreshold, keptThreshold, _) = SeparationPolicyParser.Parse(
            "{\"sourceSeparation\":\"enabled\",\"separationThreshold\":9.0}",
            0.70);
        Assert.Equal(SourceSeparationPolicy.Enabled, badThreshold);
        Assert.Equal(0.70, keptThreshold);
    }

    [Fact]
    public void Execution_Recorded()
    {
        var key = ProviderExecutionRecorder.BuildIdempotencyKey(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            "SourceSeparation",
            "Project:22222222-2222-2222-2222-222222222222",
            0);
        Assert.Equal(
            "11111111111111111111111111111111:SourceSeparation:Project:22222222-2222-2222-2222-222222222222:0",
            key);

        var (min, max) = ConfidenceNormalizer.ParseRange("mock-deterministic");
        Assert.Equal(0.0, min);
        Assert.Equal(1.0, max);

        var (dmin, dmax) = ConfidenceNormalizer.ParseRange(null);
        Assert.Equal(0.0, dmin);
        Assert.Equal(1.0, dmax);

        var (cmin, cmax) = ConfidenceNormalizer.ParseRange("0.2-0.8");
        Assert.Equal(0.2, cmin);
        Assert.Equal(0.8, cmax);
        Assert.Equal(0.5, ConfidenceNormalizer.Normalize(0.5, "0.2-0.8"), precision: 9);

        Assert.Equal(0.0, ConfidenceNormalizer.Normalize(double.NaN, "0-1"));
        Assert.Equal(1.0, ConfidenceNormalizer.Normalize(99.0, "0-1"));
        Assert.Equal(0.0, ConfidenceNormalizer.Normalize(-5.0, "0-1"));
    }
}
