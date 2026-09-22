namespace DubbingPlatform.Domain.Enums;

public enum NotificationType
{
    ProcessingCompleted,
    ProcessingFailed,
    ManualReviewRequired,
    ReviewResolved,
    ExportCompleted,
    ExportFailed,
    UploadRejected,
    QuotaWarning,
    ProviderPolicyWarning
}
