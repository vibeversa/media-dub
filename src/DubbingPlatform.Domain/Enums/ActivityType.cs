namespace DubbingPlatform.Domain.Enums;

public enum ActivityType
{
    UploadCompleted,
    UploadRejected,
    ProcessingStarted,
    TranslationCompleted,
    ReviewRequested,
    ReviewResolved,
    EditApplied,
    ExportCompleted,
    ExportFailed,
    ProcessingCompleted,
    ProcessingFailed,
    QuotaWarning
}
