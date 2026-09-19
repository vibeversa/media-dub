using DubbingPlatform.Domain.Enums;
using DubbingPlatform.Domain.Exceptions;

namespace DubbingPlatform.Application.StateMachines;

/// <summary>
/// Stage-execution lifecycle transitions. Pending→Scheduled→Running→Completed|Failed|RetryPending|Cancelled|ManualReviewRequired;
/// RetryPending→Scheduled; Failed→Scheduled only via manual retry. Running→Skipped is additionally
/// allowed for conditionally skipped units. Same-state transitions are allowed (idempotent).
/// </summary>
public static class StageStateMachine
{
    public static bool CanTransition(StageStatus from, StageStatus to, bool isManualRetry = false)
    {
        if (from == to)
        {
            return true;
        }

        return (from, to) switch
        {
            (StageStatus.Pending, StageStatus.Scheduled) => true,
            (StageStatus.Scheduled, StageStatus.Running) => true,
            (StageStatus.Running, StageStatus.Completed) => true,
            (StageStatus.Running, StageStatus.Failed) => true,
            (StageStatus.Running, StageStatus.RetryPending) => true,
            (StageStatus.Running, StageStatus.Cancelled) => true,
            (StageStatus.Running, StageStatus.ManualReviewRequired) => true,
            (StageStatus.Running, StageStatus.Skipped) => true,
            (StageStatus.RetryPending, StageStatus.Scheduled) => true,
            (StageStatus.Failed, StageStatus.Scheduled) => isManualRetry,
            _ => false,
        };
    }

    public static void EnsureCanTransition(StageStatus from, StageStatus to, bool isManualRetry = false)
    {
        if (!CanTransition(from, to, isManualRetry))
        {
            throw new DomainException($"Illegal stage status transition from '{from}' to '{to}'.");
        }
    }
}
