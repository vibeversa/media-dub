using DubbingPlatform.Domain.Enums;
using DubbingPlatform.Domain.Exceptions;

namespace DubbingPlatform.Application.StateMachines;

/// <summary>
/// Export-job lifecycle transitions. Pending→Running→Completed|Failed|Cancelled.
/// Same-state transitions are allowed (idempotent).
/// </summary>
public static class ExportStateMachine
{
    public static bool CanTransition(ExportJobStatus from, ExportJobStatus to)
    {
        if (from == to)
        {
            return true;
        }

        return (from, to) switch
        {
            (ExportJobStatus.Pending, ExportJobStatus.Running) => true,
            (ExportJobStatus.Running, ExportJobStatus.Completed) => true,
            (ExportJobStatus.Running, ExportJobStatus.Failed) => true,
            (ExportJobStatus.Running, ExportJobStatus.Cancelled) => true,
            _ => false,
        };
    }

    public static void EnsureCanTransition(ExportJobStatus from, ExportJobStatus to)
    {
        if (!CanTransition(from, to))
        {
            throw new DomainException($"Illegal export status transition from '{from}' to '{to}'.");
        }
    }
}
