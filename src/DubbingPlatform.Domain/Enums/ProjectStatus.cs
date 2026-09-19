namespace DubbingPlatform.Domain.Enums;

public enum ProjectStatus
{
    Created,
    Uploading,
    MediaReady,
    MediaRejected,
    Processing,
    Cancelling,
    Cancelled,
    Completed,
    Failed,
    ManualReviewRequired
}
