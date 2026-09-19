namespace DubbingPlatform.Domain.Enums;

public enum StageStatus
{
    Pending,
    Scheduled,
    Running,
    Completed,
    Failed,
    RetryPending,
    Cancelled,
    ManualReviewRequired,
    Skipped
}
