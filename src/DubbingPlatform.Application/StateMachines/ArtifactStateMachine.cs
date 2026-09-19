using DubbingPlatform.Domain.Enums;
using DubbingPlatform.Domain.Exceptions;

namespace DubbingPlatform.Application.StateMachines;

/// <summary>
/// Artifact lifecycle transitions. Pending→Committed→Deleted.
/// Same-state transitions are allowed (idempotent).
/// </summary>
public static class ArtifactStateMachine
{
    public static bool CanTransition(ArtifactStatus from, ArtifactStatus to)
    {
        if (from == to)
        {
            return true;
        }

        return (from, to) switch
        {
            (ArtifactStatus.Pending, ArtifactStatus.Committed) => true,
            (ArtifactStatus.Committed, ArtifactStatus.Deleted) => true,
            _ => false,
        };
    }

    public static void EnsureCanTransition(ArtifactStatus from, ArtifactStatus to)
    {
        if (!CanTransition(from, to))
        {
            throw new DomainException($"Illegal artifact status transition from '{from}' to '{to}'.");
        }
    }
}
