using DubbingPlatform.Domain.Enums;
using DubbingPlatform.Domain.Exceptions;

namespace DubbingPlatform.Application.StateMachines;

/// <summary>
/// Upload-session lifecycle transitions. Created→InProgress→Completed|Aborted|Expired;
/// Completed→Duplicate. Same-state transitions are allowed (idempotent).
/// </summary>
public static class UploadStateMachine
{
    public static bool CanTransition(UploadStatus from, UploadStatus to)
    {
        if (from == to)
        {
            return true;
        }

        return (from, to) switch
        {
            (UploadStatus.Created, UploadStatus.InProgress) => true,
            (UploadStatus.InProgress, UploadStatus.Completed) => true,
            (UploadStatus.InProgress, UploadStatus.Aborted) => true,
            (UploadStatus.InProgress, UploadStatus.Expired) => true,
            (UploadStatus.Completed, UploadStatus.Duplicate) => true,
            _ => false,
        };
    }

    public static void EnsureCanTransition(UploadStatus from, UploadStatus to)
    {
        if (!CanTransition(from, to))
        {
            throw new DomainException($"Illegal upload status transition from '{from}' to '{to}'.");
        }
    }
}
