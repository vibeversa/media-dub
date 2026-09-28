// Task 039C: domain entity unit gap closure.
using System;
using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Domain.Enums;
using DubbingPlatform.Domain.Exceptions;

namespace DubbingPlatform.UnitTests.Domain.Entities;

/// <summary>
/// Run-to-project status projection. Run status is authoritative; project
/// status is projected. The <c>hasOpenRequiredReviews</c> override only applies
/// to a Completed run, and an undefined <see cref="ProcessingRunStatus"/> must
/// surface as an explicit <see cref="DomainException"/> (never a generic 500).
/// </summary>
public sealed class ProjectStatusProjectionTests
{
    [Theory]
    [InlineData(ProcessingRunStatus.Pending, ProjectStatus.Processing)]
    [InlineData(ProcessingRunStatus.Running, ProjectStatus.Processing)]
    [InlineData(ProcessingRunStatus.Completed, ProjectStatus.Completed)]
    [InlineData(ProcessingRunStatus.Failed, ProjectStatus.Failed)]
    [InlineData(ProcessingRunStatus.Cancelling, ProjectStatus.Cancelling)]
    [InlineData(ProcessingRunStatus.Cancelled, ProjectStatus.Cancelled)]
    [InlineData(ProcessingRunStatus.ManualReviewRequired, ProjectStatus.ManualReviewRequired)]
    public void Project_Maps_Every_Defined_RunStatus(ProcessingRunStatus runStatus, ProjectStatus expected)
    {
        Assert.Equal(expected, ProjectStatusProjection.Project(runStatus, hasOpenRequiredReviews: false));
    }

    [Fact]
    public void Project_Covers_Every_ProcessingRunStatus_Member()
    {
        var members = (ProcessingRunStatus[])Enum.GetValues(typeof(ProcessingRunStatus));
        Assert.Equal(7, members.Length);

        foreach (var member in members)
        {
            var projected = ProjectStatusProjection.Project(member, hasOpenRequiredReviews: false);
            Assert.True(Enum.IsDefined(typeof(ProjectStatus), projected), $"'{projected}' must be a defined ProjectStatus.");
        }
    }

    [Fact]
    public void Project_Completed_With_OpenRequiredReviews_Projects_ManualReviewRequired()
    {
        Assert.Equal(
            ProjectStatus.ManualReviewRequired,
            ProjectStatusProjection.Project(ProcessingRunStatus.Completed, hasOpenRequiredReviews: true));
    }

    [Fact]
    public void Project_OpenRequiredReviews_Override_Applies_Only_To_Completed()
    {
        foreach (var runStatus in Enum.GetValues<ProcessingRunStatus>())
        {
            if (runStatus == ProcessingRunStatus.Completed)
            {
                continue;
            }

            Assert.Equal(
                ProjectStatusProjection.Project(runStatus, hasOpenRequiredReviews: false),
                ProjectStatusProjection.Project(runStatus, hasOpenRequiredReviews: true));
        }
    }

    [Fact]
    public void Project_ManualReviewRequired_Run_Stays_ManualReviewRequired_With_OpenReviews()
    {
        Assert.Equal(
            ProjectStatus.ManualReviewRequired,
            ProjectStatusProjection.Project(ProcessingRunStatus.ManualReviewRequired, hasOpenRequiredReviews: true));
    }

    [Theory]
    [InlineData(99)]
    [InlineData(100)]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    [InlineData(int.MinValue)]
    public void Project_Unknown_RunStatus_Throws_DomainException_Not_Generic(int rawValue)
    {
        var unknown = (ProcessingRunStatus)rawValue;
        Assert.False(Enum.IsDefined(typeof(ProcessingRunStatus), unknown));

        var ex = Assert.Throws<DomainException>(() => ProjectStatusProjection.Project(unknown, hasOpenRequiredReviews: false));
        Assert.Equal($"Unknown run status '{unknown}'.", ex.Message);

        // The review override is scoped to Completed only, so an unknown status
        // with open reviews must still fail explicitly rather than silently
        // returning ManualReviewRequired.
        Assert.Throws<DomainException>(() => ProjectStatusProjection.Project(unknown, hasOpenRequiredReviews: true));
    }

    [Fact]
    public void Project_Projection_Is_Pure_And_Repeatable()
    {
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(ProjectStatus.Processing, ProjectStatusProjection.Project(ProcessingRunStatus.Running, false));
        }
    }
}
