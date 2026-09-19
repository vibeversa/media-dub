using DubbingPlatform.Domain.Enums;
using DubbingPlatform.Domain.Exceptions;

namespace DubbingPlatform.Application.StateMachines;

/// <summary>
/// Content-object lifecycle transitions. Pending→Committed→Orphaned→Deleted.
/// Same-state transitions are allowed (idempotent).
/// </summary>
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
