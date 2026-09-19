namespace DubbingPlatform.Domain.Enums;

public enum ProcessingRunStatus
{
    Pending,
    Running,
    Completed,
    Failed,
    Cancelling,
    Cancelled,
    ManualReviewRequired
}
