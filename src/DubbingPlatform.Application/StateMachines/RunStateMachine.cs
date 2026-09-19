using DubbingPlatform.Domain.Enums;
using DubbingPlatform.Domain.Exceptions;

namespace DubbingPlatform.Application.StateMachines;

/// <summary>
/// Processing-run lifecycle transitions. Pending→Running→Completed|Failed|Cancelling|ManualReviewRequired;
/// Cancelling→Cancelled; ManualReviewRequired→Running; Failed→Running only via manual retry.
/// Same-state transitions are allowed (idempotent).
/// </summary>
public static class RunStateMachine
{
    public static bool CanTransition(ProcessingRunStatus from, ProcessingRunStatus to, bool isManualRetry = false)
    {
        if (from == to)
        {
            return true;
        }

        return (from, to) switch
        {
            (ProcessingRunStatus.Pending, ProcessingRunStatus.Running) => true,
            (ProcessingRunStatus.Running, ProcessingRunStatus.Completed) => true,
            (ProcessingRunStatus.Running, ProcessingRunStatus.Failed) => true,
            (ProcessingRunStatus.Running, ProcessingRunStatus.Cancelling) => true,
            (ProcessingRunStatus.Running, ProcessingRunStatus.ManualReviewRequired) => true,
            (ProcessingRunStatus.Cancelling, ProcessingRunStatus.Cancelled) => true,
            (ProcessingRunStatus.ManualReviewRequired, ProcessingRunStatus.Running) => true,
            (ProcessingRunStatus.Failed, ProcessingRunStatus.Running) => isManualRetry,
            _ => false,
        };
    }

    public static void EnsureCanTransition(ProcessingRunStatus from, ProcessingRunStatus to, bool isManualRetry = false)
    {
        if (!CanTransition(from, to, isManualRetry))
        {
            throw new DomainException($"Illegal run status transition from '{from}' to '{to}'.");
        }
    }
}
