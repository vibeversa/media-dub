using DubbingPlatform.Application.StateMachines;
using DubbingPlatform.Domain.Enums;
using DubbingPlatform.Domain.Exceptions;

namespace DubbingPlatform.UnitTests.StateMachines;

public sealed class StateMachineTests
{
    [Theory]
    [InlineData(ProjectStatus.Created, ProjectStatus.Uploading)]
    [InlineData(ProjectStatus.Uploading, ProjectStatus.MediaReady)]
    [InlineData(ProjectStatus.Uploading, ProjectStatus.MediaRejected)]
    [InlineData(ProjectStatus.MediaReady, ProjectStatus.Processing)]
    [InlineData(ProjectStatus.Processing, ProjectStatus.Cancelling)]
    [InlineData(ProjectStatus.Processing, ProjectStatus.Completed)]
    [InlineData(ProjectStatus.Processing, ProjectStatus.Failed)]
    [InlineData(ProjectStatus.Processing, ProjectStatus.ManualReviewRequired)]
    [InlineData(ProjectStatus.Cancelling, ProjectStatus.Cancelled)]
    [InlineData(ProjectStatus.ManualReviewRequired, ProjectStatus.Processing)]
    [InlineData(ProjectStatus.ManualReviewRequired, ProjectStatus.Cancelled)]
    public void Project_Allowed_Transitions(ProjectStatus from, ProjectStatus to)
    {
        Assert.True(ProjectStateMachine.CanTransition(from, to));
        ProjectStateMachine.EnsureCanTransition(from, to);
    }

    [Fact]
    public void Project_Failed_To_Processing_Requires_Manual_Retry()
    {
        Assert.False(ProjectStateMachine.CanTransition(ProjectStatus.Failed, ProjectStatus.Processing));
        Assert.False(ProjectStateMachine.CanTransition(ProjectStatus.Failed, ProjectStatus.Processing, false));
        Assert.True(ProjectStateMachine.CanTransition(ProjectStatus.Failed, ProjectStatus.Processing, true));
        ProjectStateMachine.EnsureCanTransition(ProjectStatus.Failed, ProjectStatus.Processing, true);
    }

    [Theory]
    [InlineData(ProjectStatus.Created, ProjectStatus.Completed)]
    [InlineData(ProjectStatus.Created, ProjectStatus.Processing)]
    [InlineData(ProjectStatus.Uploading, ProjectStatus.Processing)]
    [InlineData(ProjectStatus.Processing, ProjectStatus.Created)]
    [InlineData(ProjectStatus.Completed, ProjectStatus.Processing)]
    [InlineData(ProjectStatus.Cancelled, ProjectStatus.Processing)]
    [InlineData(ProjectStatus.MediaRejected, ProjectStatus.Processing)]
    [InlineData(ProjectStatus.Failed, ProjectStatus.Cancelled)]
    public void Project_Forbidden_Transitions(ProjectStatus from, ProjectStatus to)
    {
        Assert.False(ProjectStateMachine.CanTransition(from, to));
        Assert.False(ProjectStateMachine.CanTransition(from, to, true));
        Assert.Throws<DomainException>(() => ProjectStateMachine.EnsureCanTransition(from, to));
    }

    [Theory]
    [InlineData(ProcessingRunStatus.Pending, ProcessingRunStatus.Running)]
    [InlineData(ProcessingRunStatus.Running, ProcessingRunStatus.Completed)]
    [InlineData(ProcessingRunStatus.Running, ProcessingRunStatus.Failed)]
    [InlineData(ProcessingRunStatus.Running, ProcessingRunStatus.Cancelling)]
    [InlineData(ProcessingRunStatus.Running, ProcessingRunStatus.ManualReviewRequired)]
    [InlineData(ProcessingRunStatus.Cancelling, ProcessingRunStatus.Cancelled)]
    [InlineData(ProcessingRunStatus.ManualReviewRequired, ProcessingRunStatus.Running)]
    public void Run_Allowed_Transitions(ProcessingRunStatus from, ProcessingRunStatus to)
    {
        Assert.True(RunStateMachine.CanTransition(from, to));
        RunStateMachine.EnsureCanTransition(from, to);
    }

    [Fact]
    public void Run_Failed_To_Running_Requires_Manual_Retry()
    {
        Assert.False(RunStateMachine.CanTransition(ProcessingRunStatus.Failed, ProcessingRunStatus.Running));
        Assert.True(RunStateMachine.CanTransition(ProcessingRunStatus.Failed, ProcessingRunStatus.Running, true));
        RunStateMachine.EnsureCanTransition(ProcessingRunStatus.Failed, ProcessingRunStatus.Running, true);
    }

    [Theory]
    [InlineData(ProcessingRunStatus.Pending, ProcessingRunStatus.Completed)]
    [InlineData(ProcessingRunStatus.Running, ProcessingRunStatus.Pending)]
    [InlineData(ProcessingRunStatus.Completed, ProcessingRunStatus.Running)]
    [InlineData(ProcessingRunStatus.Cancelled, ProcessingRunStatus.Running)]
    [InlineData(ProcessingRunStatus.Failed, ProcessingRunStatus.Cancelled)]
    public void Run_Forbidden_Transitions(ProcessingRunStatus from, ProcessingRunStatus to)
    {
        Assert.False(RunStateMachine.CanTransition(from, to));
        Assert.False(RunStateMachine.CanTransition(from, to, true));
        Assert.Throws<DomainException>(() => RunStateMachine.EnsureCanTransition(from, to));
    }

    [Theory]
    [InlineData(StageStatus.Pending, StageStatus.Scheduled)]
    [InlineData(StageStatus.Scheduled, StageStatus.Running)]
    [InlineData(StageStatus.Running, StageStatus.Completed)]
    [InlineData(StageStatus.Running, StageStatus.Failed)]
    [InlineData(StageStatus.Running, StageStatus.RetryPending)]
    [InlineData(StageStatus.Running, StageStatus.Cancelled)]
    [InlineData(StageStatus.Running, StageStatus.ManualReviewRequired)]
    [InlineData(StageStatus.RetryPending, StageStatus.Scheduled)]
    public void Stage_Allowed_Transitions(StageStatus from, StageStatus to)
    {
        Assert.True(StageStateMachine.CanTransition(from, to));
        StageStateMachine.EnsureCanTransition(from, to);
    }

    [Fact]
    public void Stage_Failed_To_Scheduled_Requires_Manual_Retry()
    {
        Assert.False(StageStateMachine.CanTransition(StageStatus.Failed, StageStatus.Scheduled));
        Assert.True(StageStateMachine.CanTransition(StageStatus.Failed, StageStatus.Scheduled, true));
        StageStateMachine.EnsureCanTransition(StageStatus.Failed, StageStatus.Scheduled, true);
    }

    [Theory]
    [InlineData(StageStatus.Pending, StageStatus.Running)]
    [InlineData(StageStatus.Scheduled, StageStatus.Completed)]
    [InlineData(StageStatus.Running, StageStatus.Pending)]
    [InlineData(StageStatus.Completed, StageStatus.Scheduled)]
    [InlineData(StageStatus.Cancelled, StageStatus.Scheduled)]
    [InlineData(StageStatus.RetryPending, StageStatus.Running)]
    public void Stage_Forbidden_Transitions(StageStatus from, StageStatus to)
    {
        Assert.False(StageStateMachine.CanTransition(from, to));
        Assert.False(StageStateMachine.CanTransition(from, to, true));
        Assert.Throws<DomainException>(() => StageStateMachine.EnsureCanTransition(from, to));
    }

    [Theory]
    [InlineData(UploadStatus.Created, UploadStatus.InProgress)]
    [InlineData(UploadStatus.InProgress, UploadStatus.Completed)]
    [InlineData(UploadStatus.InProgress, UploadStatus.Aborted)]
    [InlineData(UploadStatus.InProgress, UploadStatus.Expired)]
    [InlineData(UploadStatus.Completed, UploadStatus.Duplicate)]
    public void Upload_Allowed_Transitions(UploadStatus from, UploadStatus to)
    {
        Assert.True(UploadStateMachine.CanTransition(from, to));
        UploadStateMachine.EnsureCanTransition(from, to);
    }

    [Theory]
    [InlineData(UploadStatus.Created, UploadStatus.Completed)]
    [InlineData(UploadStatus.Created, UploadStatus.Duplicate)]
    [InlineData(UploadStatus.InProgress, UploadStatus.Duplicate)]
    [InlineData(UploadStatus.Completed, UploadStatus.InProgress)]
    [InlineData(UploadStatus.Aborted, UploadStatus.InProgress)]
    [InlineData(UploadStatus.Duplicate, UploadStatus.Completed)]
    public void Upload_Forbidden_Transitions(UploadStatus from, UploadStatus to)
    {
        Assert.False(UploadStateMachine.CanTransition(from, to));
        Assert.Throws<DomainException>(() => UploadStateMachine.EnsureCanTransition(from, to));
    }

    [Theory]
    [InlineData(ReviewStatus.Open, ReviewStatus.Approved)]
    [InlineData(ReviewStatus.Open, ReviewStatus.Rejected)]
    [InlineData(ReviewStatus.Open, ReviewStatus.Requeued)]
    [InlineData(ReviewStatus.Open, ReviewStatus.ResolvedWithEdit)]
    public void Review_Allowed_Transitions(ReviewStatus from, ReviewStatus to)
    {
        Assert.True(ReviewStateMachine.CanTransition(from, to));
        ReviewStateMachine.EnsureCanTransition(from, to);
    }

    [Theory]
    [InlineData(ReviewStatus.Approved, ReviewStatus.Rejected)]
    [InlineData(ReviewStatus.Rejected, ReviewStatus.Open)]
    [InlineData(ReviewStatus.Requeued, ReviewStatus.Approved)]
    [InlineData(ReviewStatus.ResolvedWithEdit, ReviewStatus.Open)]
    public void Review_Forbidden_Transitions(ReviewStatus from, ReviewStatus to)
    {
        Assert.False(ReviewStateMachine.CanTransition(from, to));
        Assert.Throws<DomainException>(() => ReviewStateMachine.EnsureCanTransition(from, to));
    }

    [Theory]
    [InlineData(ExportJobStatus.Pending, ExportJobStatus.Running)]
    [InlineData(ExportJobStatus.Running, ExportJobStatus.Completed)]
    [InlineData(ExportJobStatus.Running, ExportJobStatus.Failed)]
    [InlineData(ExportJobStatus.Running, ExportJobStatus.Cancelled)]
    public void Export_Allowed_Transitions(ExportJobStatus from, ExportJobStatus to)
    {
        Assert.True(ExportStateMachine.CanTransition(from, to));
        ExportStateMachine.EnsureCanTransition(from, to);
    }

    [Theory]
    [InlineData(ExportJobStatus.Pending, ExportJobStatus.Completed)]
    [InlineData(ExportJobStatus.Pending, ExportJobStatus.Failed)]
    [InlineData(ExportJobStatus.Running, ExportJobStatus.Pending)]
    [InlineData(ExportJobStatus.Completed, ExportJobStatus.Running)]
    [InlineData(ExportJobStatus.Failed, ExportJobStatus.Running)]
    public void Export_Forbidden_Transitions(ExportJobStatus from, ExportJobStatus to)
    {
        Assert.False(ExportStateMachine.CanTransition(from, to));
        Assert.Throws<DomainException>(() => ExportStateMachine.EnsureCanTransition(from, to));
    }

    [Fact]
    public void Same_State_Transitions_Are_Idempotent()
    {
        Assert.True(ProjectStateMachine.CanTransition(ProjectStatus.Processing, ProjectStatus.Processing));
        Assert.True(RunStateMachine.CanTransition(ProcessingRunStatus.Running, ProcessingRunStatus.Running));
        Assert.True(StageStateMachine.CanTransition(StageStatus.Running, StageStatus.Running));
        Assert.True(UploadStateMachine.CanTransition(UploadStatus.Created, UploadStatus.Created));
        Assert.True(ReviewStateMachine.CanTransition(ReviewStatus.Open, ReviewStatus.Open));
        Assert.True(ExportStateMachine.CanTransition(ExportJobStatus.Pending, ExportJobStatus.Pending));
    }

    [Theory]
    [InlineData(ProcessingRunStatus.Pending, false, ProjectStatus.Processing)]
    [InlineData(ProcessingRunStatus.Running, false, ProjectStatus.Processing)]
    [InlineData(ProcessingRunStatus.Completed, false, ProjectStatus.Completed)]
    [InlineData(ProcessingRunStatus.Completed, true, ProjectStatus.ManualReviewRequired)]
    [InlineData(ProcessingRunStatus.Failed, false, ProjectStatus.Failed)]
    [InlineData(ProcessingRunStatus.Cancelling, false, ProjectStatus.Cancelling)]
    [InlineData(ProcessingRunStatus.Cancelled, false, ProjectStatus.Cancelled)]
    [InlineData(ProcessingRunStatus.ManualReviewRequired, false, ProjectStatus.ManualReviewRequired)]
    [InlineData(ProcessingRunStatus.ManualReviewRequired, true, ProjectStatus.ManualReviewRequired)]
    public void Projector_Maps_Run_To_Project(ProcessingRunStatus runStatus, bool hasOpenReviews, ProjectStatus expected)
    {
        Assert.Equal(expected, ProjectStatusProjector.ProjectFrom(runStatus, hasOpenReviews));
    }
}
