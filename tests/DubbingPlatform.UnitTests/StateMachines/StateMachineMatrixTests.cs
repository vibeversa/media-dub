// Task 039C: state machine unit gap closure.
using DubbingPlatform.Application.StateMachines;
using DubbingPlatform.Domain.Enums;
using DubbingPlatform.Domain.Exceptions;

namespace DubbingPlatform.UnitTests.StateMachines;

/// <summary>
/// Exhaustive transition matrices for all eight lifecycle state machines.
/// <see cref="StateMachineTests"/> (pre-existing, not owned by 039C) samples
/// individual legs; this file pins the complete (from, to) product of every
/// enum value, the same-state idempotent leg for every value, the
/// <c>isManualRetry</c> gate on the three retryable machines, and the
/// <c>EnsureCanTransition</c> throw leg for every illegal edge.
/// </summary>
public sealed class StateMachineMatrixTests
{
    [Fact]
    public void Artifact_Full_Matrix()
    {
        var allowed = Allowed<ArtifactStatus>(
            (ArtifactStatus.Pending, ArtifactStatus.Committed),
            (ArtifactStatus.Committed, ArtifactStatus.Deleted));

        AssertFullMatrix((from, to) => ArtifactStateMachine.CanTransition(from, to), allowed);
        AssertForbiddenThrow((from, to) => ArtifactStateMachine.EnsureCanTransition(from, to), allowed);
    }

    [Fact]
    public void ContentObject_Full_Matrix()
    {
        var allowed = Allowed<ContentObjectStatus>(
            (ContentObjectStatus.Pending, ContentObjectStatus.Committed),
            (ContentObjectStatus.Committed, ContentObjectStatus.Orphaned),
            (ContentObjectStatus.Orphaned, ContentObjectStatus.Deleted));

        AssertFullMatrix((from, to) => ContentObjectStateMachine.CanTransition(from, to), allowed);
        AssertForbiddenThrow((from, to) => ContentObjectStateMachine.EnsureCanTransition(from, to), allowed);
    }

    [Fact]
    public void Export_Full_Matrix()
    {
        var allowed = Allowed<ExportJobStatus>(
            (ExportJobStatus.Pending, ExportJobStatus.Running),
            (ExportJobStatus.Running, ExportJobStatus.Completed),
            (ExportJobStatus.Running, ExportJobStatus.Failed),
            (ExportJobStatus.Running, ExportJobStatus.Cancelled));

        AssertFullMatrix((from, to) => ExportStateMachine.CanTransition(from, to), allowed);
        AssertForbiddenThrow((from, to) => ExportStateMachine.EnsureCanTransition(from, to), allowed);
    }

    [Fact]
    public void Project_Full_Matrix_Without_Manual_Retry()
    {
        var allowed = Allowed<ProjectStatus>(
            (ProjectStatus.Created, ProjectStatus.Uploading),
            (ProjectStatus.Uploading, ProjectStatus.MediaReady),
            (ProjectStatus.Uploading, ProjectStatus.MediaRejected),
            (ProjectStatus.MediaReady, ProjectStatus.Processing),
            (ProjectStatus.Processing, ProjectStatus.Cancelling),
            (ProjectStatus.Processing, ProjectStatus.Completed),
            (ProjectStatus.Processing, ProjectStatus.Failed),
            (ProjectStatus.Processing, ProjectStatus.ManualReviewRequired),
            (ProjectStatus.Cancelling, ProjectStatus.Cancelled),
            (ProjectStatus.ManualReviewRequired, ProjectStatus.Processing),
            (ProjectStatus.ManualReviewRequired, ProjectStatus.Cancelled));

        AssertFullMatrix((from, to) => ProjectStateMachine.CanTransition(from, to), allowed);
        AssertForbiddenThrow((from, to) => ProjectStateMachine.EnsureCanTransition(from, to), allowed);
    }

    [Fact]
    public void Run_Full_Matrix_Without_Manual_Retry()
    {
        var allowed = Allowed<ProcessingRunStatus>(
            (ProcessingRunStatus.Pending, ProcessingRunStatus.Running),
            (ProcessingRunStatus.Running, ProcessingRunStatus.Completed),
            (ProcessingRunStatus.Running, ProcessingRunStatus.Failed),
            (ProcessingRunStatus.Running, ProcessingRunStatus.Cancelling),
            (ProcessingRunStatus.Running, ProcessingRunStatus.ManualReviewRequired),
            (ProcessingRunStatus.Cancelling, ProcessingRunStatus.Cancelled),
            (ProcessingRunStatus.ManualReviewRequired, ProcessingRunStatus.Running));

        AssertFullMatrix((from, to) => RunStateMachine.CanTransition(from, to), allowed);
        AssertForbiddenThrow((from, to) => RunStateMachine.EnsureCanTransition(from, to), allowed);
    }

    [Fact]
    public void Stage_Full_Matrix_Without_Manual_Retry()
    {
        var allowed = Allowed<StageStatus>(
            (StageStatus.Pending, StageStatus.Scheduled),
            (StageStatus.Scheduled, StageStatus.Running),
            (StageStatus.Running, StageStatus.Completed),
            (StageStatus.Running, StageStatus.Failed),
            (StageStatus.Running, StageStatus.RetryPending),
            (StageStatus.Running, StageStatus.Cancelled),
            (StageStatus.Running, StageStatus.ManualReviewRequired),
            (StageStatus.Running, StageStatus.Skipped),
            (StageStatus.RetryPending, StageStatus.Scheduled));

        AssertFullMatrix((from, to) => StageStateMachine.CanTransition(from, to), allowed);
        AssertForbiddenThrow((from, to) => StageStateMachine.EnsureCanTransition(from, to), allowed);
    }

    [Fact]
    public void Upload_Full_Matrix()
    {
        var allowed = Allowed<UploadStatus>(
            (UploadStatus.Created, UploadStatus.InProgress),
            (UploadStatus.InProgress, UploadStatus.Completed),
            (UploadStatus.InProgress, UploadStatus.Aborted),
            (UploadStatus.InProgress, UploadStatus.Expired),
            (UploadStatus.Completed, UploadStatus.Duplicate));

        AssertFullMatrix((from, to) => UploadStateMachine.CanTransition(from, to), allowed);
        AssertForbiddenThrow((from, to) => UploadStateMachine.EnsureCanTransition(from, to), allowed);
    }

    [Fact]
    public void Review_Full_Matrix()
    {
        var allowed = Allowed<ReviewStatus>(
            (ReviewStatus.Open, ReviewStatus.Approved),
            (ReviewStatus.Open, ReviewStatus.Rejected),
            (ReviewStatus.Open, ReviewStatus.Requeued),
            (ReviewStatus.Open, ReviewStatus.ResolvedWithEdit));

        AssertFullMatrix((from, to) => ReviewStateMachine.CanTransition(from, to), allowed);
        AssertForbiddenThrow((from, to) => ReviewStateMachine.EnsureCanTransition(from, to), allowed);
    }

    [Fact]
    public void Stage_Running_To_Skipped_Is_Unconditionally_Allowed_And_Skipped_Is_Terminal()
    {
        // Complementary to StateMachineTests: the conditional-skip edge is the
        // only leg whose source is Running and whose target is unique to it.
        Assert.True(StageStateMachine.CanTransition(StageStatus.Running, StageStatus.Skipped));
        StageStateMachine.EnsureCanTransition(StageStatus.Running, StageStatus.Skipped);

        foreach (var to in Enum.GetValues<StageStatus>())
        {
            if (to == StageStatus.Skipped)
            {
                continue;
            }

            Assert.False(StageStateMachine.CanTransition(StageStatus.Skipped, to));
            Assert.False(StageStateMachine.CanTransition(StageStatus.Skipped, to, true));
        }
    }

    [Fact]
    public void Ensure_Throw_Messages_Name_The_Illegal_Leg()
    {
        var artifact = Assert.Throws<DomainException>(
            () => ArtifactStateMachine.EnsureCanTransition(ArtifactStatus.Deleted, ArtifactStatus.Pending));
        Assert.Equal("Illegal artifact status transition from 'Deleted' to 'Pending'.", artifact.Message);

        var contentObject = Assert.Throws<DomainException>(
            () => ContentObjectStateMachine.EnsureCanTransition(ContentObjectStatus.Deleted, ContentObjectStatus.Pending));
        Assert.Equal("Illegal content object status transition from 'Deleted' to 'Pending'.", contentObject.Message);

        var export = Assert.Throws<DomainException>(
            () => ExportStateMachine.EnsureCanTransition(ExportJobStatus.Cancelled, ExportJobStatus.Pending));
        Assert.Equal("Illegal export status transition from 'Cancelled' to 'Pending'.", export.Message);

        var project = Assert.Throws<DomainException>(
            () => ProjectStateMachine.EnsureCanTransition(ProjectStatus.Completed, ProjectStatus.Created));
        Assert.Equal("Illegal project status transition from 'Completed' to 'Created'.", project.Message);

        var run = Assert.Throws<DomainException>(
            () => RunStateMachine.EnsureCanTransition(ProcessingRunStatus.Cancelled, ProcessingRunStatus.Pending));
        Assert.Equal("Illegal run status transition from 'Cancelled' to 'Pending'.", run.Message);

        var stage = Assert.Throws<DomainException>(
            () => StageStateMachine.EnsureCanTransition(StageStatus.Skipped, StageStatus.Running));
        Assert.Equal("Illegal stage status transition from 'Skipped' to 'Running'.", stage.Message);

        var upload = Assert.Throws<DomainException>(
            () => UploadStateMachine.EnsureCanTransition(UploadStatus.Expired, UploadStatus.Created));
        Assert.Equal("Illegal upload status transition from 'Expired' to 'Created'.", upload.Message);

        var review = Assert.Throws<DomainException>(
            () => ReviewStateMachine.EnsureCanTransition(ReviewStatus.ResolvedWithEdit, ReviewStatus.Open));
        Assert.Equal("Illegal review status transition from 'ResolvedWithEdit' to 'Open'.", review.Message);
    }

    [Fact]
    public void Same_State_Transitions_Are_Idempotent_For_Every_Value()
    {
        foreach (var status in Enum.GetValues<ArtifactStatus>())
        {
            Assert.True(ArtifactStateMachine.CanTransition(status, status));
            ArtifactStateMachine.EnsureCanTransition(status, status);
        }

        foreach (var status in Enum.GetValues<ContentObjectStatus>())
        {
            Assert.True(ContentObjectStateMachine.CanTransition(status, status));
            ContentObjectStateMachine.EnsureCanTransition(status, status);
        }

        foreach (var status in Enum.GetValues<ExportJobStatus>())
        {
            Assert.True(ExportStateMachine.CanTransition(status, status));
            ExportStateMachine.EnsureCanTransition(status, status);
        }

        foreach (var status in Enum.GetValues<ProjectStatus>())
        {
            Assert.True(ProjectStateMachine.CanTransition(status, status));
            Assert.True(ProjectStateMachine.CanTransition(status, status, true));
            ProjectStateMachine.EnsureCanTransition(status, status);
            ProjectStateMachine.EnsureCanTransition(status, status, true);
        }

        foreach (var status in Enum.GetValues<ProcessingRunStatus>())
        {
            Assert.True(RunStateMachine.CanTransition(status, status));
            Assert.True(RunStateMachine.CanTransition(status, status, true));
            RunStateMachine.EnsureCanTransition(status, status);
            RunStateMachine.EnsureCanTransition(status, status, true);
        }

        foreach (var status in Enum.GetValues<StageStatus>())
        {
            Assert.True(StageStateMachine.CanTransition(status, status));
            Assert.True(StageStateMachine.CanTransition(status, status, true));
            StageStateMachine.EnsureCanTransition(status, status);
            StageStateMachine.EnsureCanTransition(status, status, true);
        }

        foreach (var status in Enum.GetValues<UploadStatus>())
        {
            Assert.True(UploadStateMachine.CanTransition(status, status));
            UploadStateMachine.EnsureCanTransition(status, status);
        }

        foreach (var status in Enum.GetValues<ReviewStatus>())
        {
            Assert.True(ReviewStateMachine.CanTransition(status, status));
            ReviewStateMachine.EnsureCanTransition(status, status);
        }
    }

    [Fact]
    public void Project_Manual_Retry_Flag_Only_Opens_Failed_To_Processing()
    {
        foreach (var to in Enum.GetValues<ProjectStatus>())
        {
            var withoutFlag = ProjectStateMachine.CanTransition(ProjectStatus.Failed, to);
            var withFlag = ProjectStateMachine.CanTransition(ProjectStatus.Failed, to, true);
            Assert.Equal(withoutFlag || to == ProjectStatus.Processing, withFlag);
        }

        AssertFlagChangesNothingElse(
            Enum.GetValues<ProjectStatus>(),
            (from, to) => ProjectStateMachine.CanTransition(from, to),
            (from, to) => ProjectStateMachine.CanTransition(from, to, true),
            ProjectStatus.Failed);
    }

    [Fact]
    public void Project_Manual_Retry_Ensure_Throw_And_Pass_Legs()
    {
        Assert.Throws<DomainException>(
            () => ProjectStateMachine.EnsureCanTransition(ProjectStatus.Failed, ProjectStatus.Processing));
        Assert.Throws<DomainException>(
            () => ProjectStateMachine.EnsureCanTransition(ProjectStatus.Failed, ProjectStatus.Processing, false));
        ProjectStateMachine.EnsureCanTransition(ProjectStatus.Failed, ProjectStatus.Processing, true);
    }

    [Fact]
    public void Run_Manual_Retry_Flag_Only_Opens_Failed_To_Running()
    {
        foreach (var to in Enum.GetValues<ProcessingRunStatus>())
        {
            var withoutFlag = RunStateMachine.CanTransition(ProcessingRunStatus.Failed, to);
            var withFlag = RunStateMachine.CanTransition(ProcessingRunStatus.Failed, to, true);
            Assert.Equal(withoutFlag || to == ProcessingRunStatus.Running, withFlag);
        }

        AssertFlagChangesNothingElse(
            Enum.GetValues<ProcessingRunStatus>(),
            (from, to) => RunStateMachine.CanTransition(from, to),
            (from, to) => RunStateMachine.CanTransition(from, to, true),
            ProcessingRunStatus.Failed);
    }

    [Fact]
    public void Run_Manual_Retry_Ensure_Throw_And_Pass_Legs()
    {
        Assert.Throws<DomainException>(
            () => RunStateMachine.EnsureCanTransition(ProcessingRunStatus.Failed, ProcessingRunStatus.Running));
        Assert.Throws<DomainException>(
            () => RunStateMachine.EnsureCanTransition(ProcessingRunStatus.Failed, ProcessingRunStatus.Running, false));
        RunStateMachine.EnsureCanTransition(ProcessingRunStatus.Failed, ProcessingRunStatus.Running, true);
    }

    [Fact]
    public void Stage_Manual_Retry_Flag_Only_Opens_Failed_To_Scheduled()
    {
        foreach (var to in Enum.GetValues<StageStatus>())
        {
            var withoutFlag = StageStateMachine.CanTransition(StageStatus.Failed, to);
            var withFlag = StageStateMachine.CanTransition(StageStatus.Failed, to, true);
            Assert.Equal(withoutFlag || to == StageStatus.Scheduled, withFlag);
        }

        AssertFlagChangesNothingElse(
            Enum.GetValues<StageStatus>(),
            (from, to) => StageStateMachine.CanTransition(from, to),
            (from, to) => StageStateMachine.CanTransition(from, to, true),
            StageStatus.Failed);
    }

    [Fact]
    public void Stage_Manual_Retry_Ensure_Throw_And_Pass_Legs()
    {
        Assert.Throws<DomainException>(
            () => StageStateMachine.EnsureCanTransition(StageStatus.Failed, StageStatus.Scheduled));
        Assert.Throws<DomainException>(
            () => StageStateMachine.EnsureCanTransition(StageStatus.Failed, StageStatus.Scheduled, false));
        StageStateMachine.EnsureCanTransition(StageStatus.Failed, StageStatus.Scheduled, true);
    }

    [Fact]
    public void Terminal_States_Have_No_Outgoing_Edges()
    {
        foreach (var to in Enum.GetValues<ExportJobStatus>())
        {
            if (to == ExportJobStatus.Completed)
            {
                continue;
            }

            Assert.False(ExportStateMachine.CanTransition(ExportJobStatus.Completed, to));
        }

        foreach (var to in Enum.GetValues<ProjectStatus>())
        {
            if (to is ProjectStatus.Completed or ProjectStatus.Cancelled)
            {
                continue;
            }

            Assert.False(ProjectStateMachine.CanTransition(ProjectStatus.Completed, to));
            Assert.False(ProjectStateMachine.CanTransition(ProjectStatus.Cancelled, to));
        }

        foreach (var to in Enum.GetValues<ProcessingRunStatus>())
        {
            if (to is ProcessingRunStatus.Completed or ProcessingRunStatus.Cancelled)
            {
                continue;
            }

            Assert.False(RunStateMachine.CanTransition(ProcessingRunStatus.Completed, to));
            Assert.False(RunStateMachine.CanTransition(ProcessingRunStatus.Cancelled, to));
        }

        foreach (var to in Enum.GetValues<ReviewStatus>())
        {
            if (to is ReviewStatus.Approved or ReviewStatus.Rejected
                or ReviewStatus.Requeued or ReviewStatus.ResolvedWithEdit)
            {
                continue;
            }

            Assert.False(ReviewStateMachine.CanTransition(ReviewStatus.Approved, to));
            Assert.False(ReviewStateMachine.CanTransition(ReviewStatus.Rejected, to));
            Assert.False(ReviewStateMachine.CanTransition(ReviewStatus.Requeued, to));
            Assert.False(ReviewStateMachine.CanTransition(ReviewStatus.ResolvedWithEdit, to));
        }
    }

    private static void AssertFlagChangesNothingElse<TStatus>(
        TStatus[] values,
        Func<TStatus, TStatus, bool> withoutFlag,
        Func<TStatus, TStatus, bool> withFlag,
        TStatus gated)
        where TStatus : struct, Enum
    {
        foreach (var from in values)
        {
            if (from.Equals(gated))
            {
                continue;
            }

            foreach (var to in values)
            {
                Assert.Equal(withoutFlag(from, to), withFlag(from, to));
            }
        }
    }

    private static HashSet<(TStatus From, TStatus To)> Allowed<TStatus>(params (TStatus From, TStatus To)[] edges)
        where TStatus : struct, Enum
    {
        var set = new HashSet<(TStatus, TStatus)>();
        foreach (var value in Enum.GetValues<TStatus>())
        {
            set.Add((value, value));
        }

        foreach (var edge in edges)
        {
            set.Add(edge);
        }

        return set;
    }

    private static void AssertFullMatrix<TStatus>(
        Func<TStatus, TStatus, bool> canTransition,
        HashSet<(TStatus From, TStatus To)> allowed)
        where TStatus : struct, Enum
    {
        var values = Enum.GetValues<TStatus>();
        Assert.NotEmpty(values);

        var allowedHits = 0;
        foreach (var from in values)
        {
            foreach (var to in values)
            {
                var expected = allowed.Contains((from, to));
                Assert.Equal(expected, canTransition(from, to));
                if (expected)
                {
                    allowedHits++;
                }
            }
        }

        // Guards against a stale expectation table silently narrowing coverage.
        Assert.Equal(allowed.Count, allowedHits);
    }

    private static void AssertForbiddenThrow<TStatus>(
        Action<TStatus, TStatus> ensureCanTransition,
        HashSet<(TStatus From, TStatus To)> allowed)
        where TStatus : struct, Enum
    {
        var values = Enum.GetValues<TStatus>();
        var forbidden = 0;
        foreach (var from in values)
        {
            foreach (var to in values)
            {
                if (allowed.Contains((from, to)))
                {
                    continue;
                }

                Assert.Throws<DomainException>(() => ensureCanTransition(from, to));
                forbidden++;
            }
        }

        Assert.Equal((values.Length * values.Length) - allowed.Count, forbidden);
    }
}
