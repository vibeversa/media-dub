using DubbingPlatform.Domain.Enums;
using DubbingPlatform.Domain.Exceptions;

namespace DubbingPlatform.Application.StateMachines;

/// <summary>
/// Project lifecycle transitions. Created→Uploading→MediaReady|MediaRejected;
/// MediaReady→Processing→Cancelling|Completed|Failed|ManualReviewRequired;
/// Cancelling→Cancelled; ManualReviewRequired→Processing|Cancelled;
/// Failed→Processing only via manual retry. Same-state transitions are allowed (idempotent).
/// </summary>
public static class ProjectStateMachine
{
    public static bool CanTransition(ProjectStatus from, ProjectStatus to, bool isManualRetry = false)
    {
        if (from == to)
        {
            return true;
        }

        return (from, to) switch
        {
            (ProjectStatus.Created, ProjectStatus.Uploading) => true,
            (ProjectStatus.Uploading, ProjectStatus.MediaReady) => true,
            (ProjectStatus.Uploading, ProjectStatus.MediaRejected) => true,
            (ProjectStatus.MediaReady, ProjectStatus.Processing) => true,
            (ProjectStatus.Processing, ProjectStatus.Cancelling) => true,
            (ProjectStatus.Processing, ProjectStatus.Completed) => true,
            (ProjectStatus.Processing, ProjectStatus.Failed) => true,
            (ProjectStatus.Processing, ProjectStatus.ManualReviewRequired) => true,
            (ProjectStatus.Cancelling, ProjectStatus.Cancelled) => true,
            (ProjectStatus.ManualReviewRequired, ProjectStatus.Processing) => true,
            (ProjectStatus.ManualReviewRequired, ProjectStatus.Cancelled) => true,
            (ProjectStatus.Failed, ProjectStatus.Processing) => isManualRetry,
            _ => false,
        };
    }

    public static void EnsureCanTransition(ProjectStatus from, ProjectStatus to, bool isManualRetry = false)
    {
        if (!CanTransition(from, to, isManualRetry))
        {
            throw new DomainException($"Illegal project status transition from '{from}' to '{to}'.");
        }
    }
}
