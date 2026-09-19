using DubbingPlatform.Application.Errors;
using DubbingPlatform.Application.Exceptions;
using DubbingPlatform.Application.Services;

namespace DubbingPlatform.UnitTests.Pipeline;

/// <summary>
/// Hermetic timeline-assembly tests (no Docker): source-start placement,
/// intentional-overlap preservation, invalid-overlap rejection, and overflow
/// detection use the pure <see cref="TimelineAssemblyService"/> planners
/// directly with mock segment/audio durations. DB persistence and artifact
/// publication live in the service/worker (PG, covered in CI).
/// </summary>
public sealed class TimelineTests
{
    private static TimelineSegmentInput Segment(
        int sequence,
        int startMs,
        int endMs,
        int audioMs,
        string status = "Pending")
    {
        return new TimelineSegmentInput(
            Guid.NewGuid(), sequence, startMs, endMs, status, Guid.NewGuid(), audioMs);
    }

    [Fact]
    public void Placement_Matches()
    {
        var runId = Guid.NewGuid();
        var first = Segment(0, 0, 2000, 1900);
        var second = Segment(1, 3000, 5000, 2000);

        // Out-of-order input still yields StartMs-then-Sequence entries.
        var built = TimelineAssemblyService.BuildTimeline(
            [second, first], [], sourceDurationMs: 10000, backgroundArtifactId: null);

        Assert.Equal(2, built.Entries.Count);
        Assert.Empty(built.SkippedSegmentIds);
        Assert.Equal(first.SegmentId.ToString("N"), built.Entries[0].SegmentId);
        Assert.Equal(0, built.Entries[0].StartMs);
        Assert.Equal(1900, built.Entries[0].DurationMs);
        Assert.Equal(first.AudioArtifactId.ToString("N"), built.Entries[0].AudioArtifactId);
        Assert.Null(built.Entries[0].OverlapGroupId);
        Assert.Equal(second.SegmentId.ToString("N"), built.Entries[1].SegmentId);
        Assert.Equal(3000, built.Entries[1].StartMs);
        Assert.Equal(2000, built.Entries[1].DurationMs);

        // Silence gap between entries is preserved (no filling or shifting).
        Assert.True(built.Entries[0].StartMs + built.Entries[0].DurationMs <= built.Entries[1].StartMs);

        var json = TimelineAssemblyService.BuildJson(runId, built);
        Assert.Contains("\"schemaVersion\":\"1\"", json, StringComparison.Ordinal);
        Assert.Contains(runId.ToString("N"), json, StringComparison.Ordinal);
        Assert.Contains(first.SegmentId.ToString("N"), json, StringComparison.Ordinal);
        Assert.Contains(second.SegmentId.ToString("N"), json, StringComparison.Ordinal);

        // Canonical: entries stay sorted in JSON document order.
        var firstIndex = json.IndexOf(first.SegmentId.ToString("N"), StringComparison.Ordinal);
        var secondIndex = json.IndexOf(second.SegmentId.ToString("N"), StringComparison.Ordinal);
        Assert.True(firstIndex >= 0 && secondIndex > firstIndex);

        // Deterministic: same inputs yield byte-identical JSON.
        var repeat = TimelineAssemblyService.BuildJson(runId, built);
        Assert.Equal(json, repeat);
    }

    [Fact]
    public void Intentional_Preserved()
    {
        var groupId = Guid.NewGuid();
        var first = Segment(0, 0, 2000, 2000);
        var second = Segment(1, 1500, 3500, 2000);

        var built = TimelineAssemblyService.BuildTimeline(
            [first, second],
            [
                new TimelineOverlapInput(first.SegmentId, groupId),
                new TimelineOverlapInput(second.SegmentId, groupId),
            ],
            sourceDurationMs: 10000,
            backgroundArtifactId: Guid.NewGuid());

        Assert.Equal(2, built.Entries.Count);
        Assert.Equal(groupId.ToString("N"), built.Entries[0].OverlapGroupId);
        Assert.Equal(groupId.ToString("N"), built.Entries[1].OverlapGroupId);
        Assert.NotNull(built.BackgroundArtifactId);

        // Overlapping placed intervals are both kept.
        var aEnd = built.Entries[0].StartMs + built.Entries[0].DurationMs;
        Assert.True(aEnd > built.Entries[1].StartMs);
    }

    [Fact]
    public void Invalid_Blocked()
    {
        var first = Segment(0, 0, 2000, 2000);
        var second = Segment(1, 1500, 3500, 2000);

        // Overlapping placed audio with no shared group is an invariant violation.
        var ungrouped = Assert.Throws<ErrorCodeException>(() =>
            TimelineAssemblyService.BuildTimeline(
                [first, second], [], sourceDurationMs: 10000, backgroundArtifactId: null));
        Assert.Equal(ErrorCodes.PipelineInvariantViolation, ungrouped.ErrorCode);

        // Overlapping audio in different groups is equally invalid.
        var disjoint = Assert.Throws<ErrorCodeException>(() =>
            TimelineAssemblyService.BuildTimeline(
                [first, second],
                [
                    new TimelineOverlapInput(first.SegmentId, Guid.NewGuid()),
                    new TimelineOverlapInput(second.SegmentId, Guid.NewGuid()),
                ],
                sourceDurationMs: 10000,
                backgroundArtifactId: null));
        Assert.Equal(ErrorCodes.PipelineInvariantViolation, disjoint.ErrorCode);

        // Missing selected audio on a pending segment is unavailable.
        var missing = Segment(2, 4000, 5000, 0);
        var unavailable = Assert.Throws<ErrorCodeException>(() =>
            TimelineAssemblyService.BuildTimeline(
                [missing], [], sourceDurationMs: 10000, backgroundArtifactId: null));
        Assert.Equal(ErrorCodes.ArtifactUnavailable, unavailable.ErrorCode);

        // Skipped segments with no audio are omitted and listed in metadata.
        var skipped = new TimelineSegmentInput(
            Guid.NewGuid(), 3, 5000, 6000, "Skipped", Guid.Empty, 0);
        var withSkipped = TimelineAssemblyService.BuildTimeline(
            [first, skipped], [], sourceDurationMs: 10000, backgroundArtifactId: null);
        Assert.DoesNotContain(withSkipped.Entries, e => string.Equals(e.SegmentId, skipped.SegmentId.ToString("N"), StringComparison.Ordinal));
        Assert.Contains(skipped.SegmentId.ToString("N"), withSkipped.SkippedSegmentIds, StringComparer.Ordinal);
    }

    [Fact]
    public void Overflow_Detected()
    {
        // Entry ends at 2000 + 8500 = 10500ms, beyond 10000ms + 100ms tolerance.
        var overflowing = Segment(0, 2000, 3000, 8500);

        var blocked = Assert.Throws<ErrorCodeException>(() =>
            TimelineAssemblyService.BuildTimeline(
                [overflowing], [], sourceDurationMs: 10000, backgroundArtifactId: null));
        Assert.Equal(ErrorCodes.QcBlocked, blocked.ErrorCode);

        // Ending exactly at source + tolerance fits.
        var fitting = Segment(0, 2000, 3000, 8100);
        var built = TimelineAssemblyService.BuildTimeline(
            [fitting], [], sourceDurationMs: 10000, backgroundArtifactId: null);
        Assert.Single(built.Entries);
    }
}
