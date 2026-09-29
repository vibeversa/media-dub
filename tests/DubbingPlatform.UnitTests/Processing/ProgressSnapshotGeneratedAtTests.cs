using DubbingPlatform.Application.Services;
using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Domain.Enums;

namespace DubbingPlatform.UnitTests.Processing;

/// <summary>
/// Regression guard for <see cref="ProgressService.BuildSnapshot"/>'s
/// <c>GeneratedAt</c> (Task 041B).
///
/// <para>
/// The snapshot stamped <c>GeneratedAt</c> with <c>DateTimeOffset.UtcNow</c>. The
/// workspace renders that field as "Updated &lt;time&gt;", so every read of the
/// workspace reported a fresh update even when nothing had changed, and the value
/// moved on every poll. That is both a misreport to the user and an
/// unreproducible response for any consumer that diffs or caches it - the
/// cross-layer visual matrix found it as a per-second pixel diff in the workspace
/// baselines.
/// </para>
///
/// <para>
/// The correct value is the run's own <c>UpdatedAt</c>: the last time the run
/// actually moved. It is durable, it is already in scope, and two reads of
/// unchanged data now agree.
/// </para>
/// </summary>
public sealed class ProgressSnapshotGeneratedAtTests
{
    private static ProcessingRun Run(DateTimeOffset updatedAt)
    {
        var started = updatedAt.AddMinutes(-5);
        return new ProcessingRun(
            Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"),
            Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002"),
            Guid.Parse("cccccccc-0000-0000-0000-000000000003"),
            1,
            ProcessingRunStatus.Running,
            "1.0.0",
            new string('a', 64),
            new string('b', 64),
            new string('c', 64),
            updatedAt,
            updatedAt,
            started,
            null);
    }

    [Fact]
    public void GeneratedAt_Is_The_Runs_UpdatedAt_Not_The_Current_Clock()
    {
        var runUpdatedAt = new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);
        var run = Run(runUpdatedAt);

        var snapshot = ProgressService.BuildSnapshot(run.ProjectId, run, [], 0, []);

        Assert.Equal(runUpdatedAt, snapshot.GeneratedAt);
    }

    [Fact]
    public void Two_Reads_Of_Unchanged_Data_Agree_On_GeneratedAt()
    {
        // The property the visual matrix depends on: a stable value for stable
        // input. With UtcNow these two differed by however long the test took.
        var run = Run(new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero));

        var first = ProgressService.BuildSnapshot(run.ProjectId, run, [], 0, []);
        Thread.Sleep(15);
        var second = ProgressService.BuildSnapshot(run.ProjectId, run, [], 0, []);

        Assert.Equal(first.GeneratedAt, second.GeneratedAt);
    }

    [Fact]
    public void GeneratedAt_Tracks_Run_Updates_Rather_Than_Reading_Time()
    {
        // When the run does move, the stamp follows it - so the field stays a
        // meaningful "last updated" and does not become a frozen constant.
        var first = Run(new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero));
        var moved = Run(new DateTimeOffset(2026, 3, 2, 9, 30, 0, TimeSpan.Zero));

        var before = ProgressService.BuildSnapshot(first.ProjectId, first, [], 0, []);
        var after = ProgressService.BuildSnapshot(moved.ProjectId, moved, [], 0, []);

        Assert.True(after.GeneratedAt > before.GeneratedAt);
    }
}
