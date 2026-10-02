// GAP-017: overlapping dialogue must get a measured crossfade in the premix
// filter graph (acrossfade), not a bare sum. Non-overlapping timelines keep the
// existing apad/amix graph.
using DubbingPlatform.Infrastructure.Media;

namespace DubbingPlatform.UnitTests.Media;

public sealed class FFmpegMixerCrossfadeTests
{
    private static MixTimelineEntry Entry(string segmentId, int startMs, int durationMs) =>
        new(segmentId, 0, startMs, durationMs, $"art-{segmentId}");

    [Fact]
    public void Overlap_Windows_Are_Detected_And_Clamped()
    {
        var entries = new[]
        {
            Entry("a", 0, 1000),
            Entry("b", 800, 1000),
        };

        var plan = FFmpegMixer.PlanCrossfades(entries);

        Assert.Single(plan);
        Assert.Equal(200, plan[0].OverlapMs);
        Assert.Equal("a", plan[0].FromSegmentId);
        Assert.Equal("b", plan[0].ToSegmentId);

        // Contained entries clamp the window to the shorter side so acrossfade
        // never asks for more than the shorter input has.
        var contained = FFmpegMixer.PlanCrossfades(new[]
        {
            Entry("a", 0, 1000),
            Entry("b", 900, 100),
        });

        Assert.Equal(100, Assert.Single(contained).OverlapMs);
    }

    [Fact]
    public void No_Overlap_Means_No_Crossfade()
    {
        var plan = FFmpegMixer.PlanCrossfades(new[]
        {
            Entry("a", 0, 1000),
            Entry("b", 1000, 1000),
            Entry("c", 3000, 500),
        });

        Assert.Empty(plan);
    }

    [Fact]
    public void Overlapping_Dialogue_Emits_Acrossfade_In_The_Premix_Graph()
    {
        var entries = new[]
        {
            Entry("a", 0, 1000),
            Entry("b", 800, 1000),
        };

        var filter = FFmpegMixer.BuildPremixFilter(entries, 2000, hasBackground: false, duckDb: -12, fadeMs: 150);

        Assert.Contains("acrossfade=d=0.200", filter, StringComparison.Ordinal);
        Assert.Contains("c1=tri:c2=tri", filter, StringComparison.Ordinal);

        // The graph must remain label-consistent for FFmpeg.
        AssertBalancedLabels(filter);
    }

    [Fact]
    public void Non_Overlapping_Dialogue_Keeps_The_Existing_Amix_Graph()
    {
        var entries = new[]
        {
            Entry("a", 0, 1000),
            Entry("b", 1000, 1000),
        };

        var filter = FFmpegMixer.BuildPremixFilter(entries, 2000, hasBackground: false, duckDb: -12, fadeMs: 150);

        Assert.DoesNotContain("acrossfade", filter, StringComparison.Ordinal);
        Assert.Contains("amix=inputs=2", filter, StringComparison.Ordinal);
        Assert.Contains("apad=whole_dur=2.000", filter, StringComparison.Ordinal);
        Assert.Contains("[mix]", filter, StringComparison.Ordinal);
    }

    [Fact]
    public void Overlapping_Dialogue_With_Background_Keeps_Ducking_And_Loudness_Stages()
    {
        var entries = new[]
        {
            Entry("a", 0, 1000),
            Entry("b", 800, 1000),
        };

        var filter = FFmpegMixer.BuildPremixFilter(entries, 2000, hasBackground: true, duckDb: -12, fadeMs: 150);

        Assert.Contains("acrossfade", filter, StringComparison.Ordinal);
        Assert.Contains("sidechaincompress", filter, StringComparison.Ordinal);
        Assert.Contains("amix=inputs=2:duration=longest:dropout_transition=0:normalize=0[mix]", filter, StringComparison.Ordinal);
        AssertBalancedLabels(filter);
    }

    [Fact]
    public void Overlap_Windows_Are_Ordered_And_Bounded_By_The_Mix_Length()
    {
        var entries = new[]
        {
            Entry("b", 900, 800),
            Entry("a", 0, 1000),
        };

        var plan = FFmpegMixer.PlanCrossfades(entries);
        Assert.Equal("a", plan[0].FromSegmentId);
        Assert.Equal("b", plan[0].ToSegmentId);
        Assert.Equal(100, plan[0].OverlapMs);
        Assert.InRange(plan[0].OverlapMs, 1, 1000);
    }

    [Fact]
    public void Single_Entry_Never_Crossfades()
    {
        Assert.Empty(FFmpegMixer.PlanCrossfades([Entry("a", 0, 1000)]));
    }

    private static void AssertBalancedLabels(string filter)
    {
        var opens = filter.Count(c => c == '[');
        var closes = filter.Count(c => c == ']');
        Assert.Equal(opens, closes);
        Assert.DoesNotContain("[]", filter, StringComparison.Ordinal);

        // Labels referenced as filter inputs must be produced somewhere in the
        // graph (either as an output label or as an ffmpeg input index).
        var labels = System.Text.RegularExpressions.Regex.Matches(filter, @"\[([A-Za-z0-9]+)\]")
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var label in labels)
        {
            Assert.True(
                label.Contains(':', StringComparison.Ordinal) || filter.Contains($"[{label}]", StringComparison.Ordinal),
                $"Label '{label}' must be produced by the graph.");
        }

        Assert.Contains("mix", labels);
    }
}