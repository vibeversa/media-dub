using DubbingPlatform.Domain.Enums;
using DubbingPlatform.Domain.Exceptions;

namespace DubbingPlatform.Application.StateMachines;

/// <summary>
/// Review-item lifecycle transitions. Open→Approved|Rejected|Requeued|ResolvedWithEdit.
/// Terminal states have no outgoing transitions. Same-state transitions are allowed (idempotent).
/// </summary>
public static class ReviewStateMachine
{
    public static bool CanTransition(ReviewStatus from, ReviewStatus to)
    {
        if (from == to)
        {
            return true;
        }

        return (from, to) switch
        {
            (ReviewStatus.Open, ReviewStatus.Approved) => true,
            (ReviewStatus.Open, ReviewStatus.Rejected) => true,
            (ReviewStatus.Open, ReviewStatus.Requeued) => true,
            (ReviewStatus.Open, ReviewStatus.ResolvedWithEdit) => true,
            _ => false,
        };
    }

    public static void EnsureCanTransition(ReviewStatus from, ReviewStatus to)
    {
        if (!CanTransition(from, to))
        {
            throw new DomainException($"Illegal review status transition from '{from}' to '{to}'.");
        }
    }
}
