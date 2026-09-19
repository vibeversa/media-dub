using DubbingPlatform.Domain.Enums;

namespace DubbingPlatform.Domain.Entities;

/// <summary>
/// Documents the run-to-project status projection. Run status is authoritative;
/// project status is projected. Enforcement lives in Task 6 state machines.
/// Projection: run Running to project Processing; run Completed to project
/// Completed unless open required reviews exist, then ManualReviewRequired;
/// run Failed to project Failed; run Cancelling/Cancelled to project
/// Cancelling/Cancelled; run ManualReviewRequired to project ManualReviewRequired;
/// run Pending to project Processing.
/// </summary>
public static class ProjectStatusProjection
{
    public static ProjectStatus Project(ProcessingRunStatus runStatus, bool hasOpenRequiredReviews)
    {
        if (hasOpenRequiredReviews && runStatus == ProcessingRunStatus.Completed)
        {
            return ProjectStatus.ManualReviewRequired;
        }

        return runStatus switch
        {
            ProcessingRunStatus.Pending => ProjectStatus.Processing,
            ProcessingRunStatus.Running => ProjectStatus.Processing,
            ProcessingRunStatus.Completed => ProjectStatus.Completed,
            ProcessingRunStatus.Failed => ProjectStatus.Failed,
            ProcessingRunStatus.Cancelling => ProjectStatus.Cancelling,
            ProcessingRunStatus.Cancelled => ProjectStatus.Cancelled,
            ProcessingRunStatus.ManualReviewRequired => ProjectStatus.ManualReviewRequired,
            _ => throw new Exceptions.DomainException($"Unknown run status '{runStatus}'."),
        };
    }
}
