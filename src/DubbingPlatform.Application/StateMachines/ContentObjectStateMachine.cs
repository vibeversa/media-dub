using DubbingPlatform.Domain.Enums;
using DubbingPlatform.Domain.Exceptions;

namespace DubbingPlatform.Application.StateMachines;

/// <summary>
/// Content-object lifecycle transitions:
/// Pending→Committed→Orphaned→Deleted, plus the direct
/// <c>Committed→Deleted</c> fast path.
/// Same-state transitions are allowed (idempotent).
/// </summary>
/// <remarks>
/// <c>Committed→Deleted</c> exists because the retention sweeper
/// (<c>RetentionService.SweepAsync</c>) selects dereferenced <c>Committed</c>
/// objects past the final retention window and marks them <c>Deleted</c>
/// without an intervening <c>Orphaned</c> step. <c>Orphaned</c> is reserved for
/// the zero-reference path driven by <c>OrphanObjectReconciler</c>, so the
/// table describes both legal routes. Deletion always requires zero live
/// references, an expired retention window, and no active hold — the state
/// machine never authorizes a delete on its own.
/// </remarks>
public static class ContentObjectStateMachine
{
    public static bool CanTransition(ContentObjectStatus from, ContentObjectStatus to)
    {
        if (from == to)
        {
            return true;
        }

        return (from, to) switch
        {
            (ContentObjectStatus.Pending, ContentObjectStatus.Committed) => true,
            (ContentObjectStatus.Committed, ContentObjectStatus.Orphaned) => true,
            (ContentObjectStatus.Orphaned, ContentObjectStatus.Deleted) => true,

            // Direct delete for dereferenced content past final retention.
            (ContentObjectStatus.Committed, ContentObjectStatus.Deleted) => true,
            _ => false,
        };
    }

    public static void EnsureCanTransition(ContentObjectStatus from, ContentObjectStatus to)
    {
        if (!CanTransition(from, to))
        {
            throw new DomainException($"Illegal content object status transition from '{from}' to '{to}'.");
        }
    }
}
