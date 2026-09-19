namespace DubbingPlatform.Domain.Enums;

/// <summary>
/// Approved, Rejected, Requeued and ResolvedWithEdit are terminal states.
/// Only Open may transition to a terminal state.
/// </summary>
public enum ReviewStatus
{
    Open,
    Approved,
    Rejected,
    Requeued,
    ResolvedWithEdit
}
