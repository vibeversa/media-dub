using DubbingPlatform.Contracts.Messages;
using DubbingPlatform.Domain.Enums;
using DubbingPlatform.Domain.Exceptions;

namespace DubbingPlatform.Application.Notifications;

/// <summary>
/// Pure mapping from backend events and outbox messages to
/// <see cref="NotificationInput"/>. Summaries carry short ids, names, and error
/// codes only — never transcript bodies, storage keys, signed URLs, hashes, or
/// secrets (error messages are excluded because they may embed paths).
/// </summary>
public static class NotificationEventMapper
{
    private static readonly TimeSpan DefaultRetention = TimeSpan.FromDays(90);

    private static readonly TimeSpan WarningRetention = TimeSpan.FromDays(30);

    public static NotificationInput FromRunCompleted(RunCompleted message)
    {
        ArgumentNullException.ThrowIfNull(message);
        RequireIdentity(message.TenantId, message.ProjectId);
        return new NotificationInput(
            message.TenantId, message.ProjectId,
            NotificationType.ProcessingCompleted, NotificationSeverity.Info,
            "Processing completed",
            string.Concat("Run ", Short(message.ProcessingRunId), " completed."),
            "ProcessingRun", message.ProcessingRunId.ToString("N"),
            message.MessageId, DateTimeOffset.UtcNow + DefaultRetention);
    }

    public static NotificationInput FromRunFailed(RunFailed message)
    {
        ArgumentNullException.ThrowIfNull(message);
        RequireIdentity(message.TenantId, message.ProjectId);
        var code = string.IsNullOrWhiteSpace(message.ErrorCode) ? "UNKNOWN" : message.ErrorCode.Trim();
        return new NotificationInput(
            message.TenantId, message.ProjectId,
            NotificationType.ProcessingFailed, NotificationSeverity.Error,
            "Processing failed",
            string.Concat("Run ", Short(message.ProcessingRunId), " failed (", code, ")."),
            "ProcessingRun", message.ProcessingRunId.ToString("N"),
            message.MessageId, DateTimeOffset.UtcNow + DefaultRetention);
    }

    public static NotificationInput FromReviewRequired(StageReviewRequired message)
    {
        ArgumentNullException.ThrowIfNull(message);
        RequireIdentity(message.TenantId, message.ProjectId);
        var stage = string.IsNullOrWhiteSpace(message.BlockedStageType) ? "a stage" : message.BlockedStageType.Trim();
        return new NotificationInput(
            message.TenantId, message.ProjectId,
            NotificationType.ManualReviewRequired, NotificationSeverity.Warning,
            "Manual review required",
            string.Concat("Stage ", stage, " needs review."),
            "ReviewItem", message.ReviewItemId.Trim(),
            message.MessageId, DateTimeOffset.UtcNow + DefaultRetention);
    }

    public static NotificationInput FromReviewResolved(ReviewResolved message)
    {
        ArgumentNullException.ThrowIfNull(message);
        RequireIdentity(message.TenantId, message.ProjectId);
        var decision = string.IsNullOrWhiteSpace(message.Decision) ? "resolved" : message.Decision.Trim();
        return new NotificationInput(
            message.TenantId, message.ProjectId,
            NotificationType.ReviewResolved, NotificationSeverity.Info,
            "Review resolved",
            string.Concat("Review resolved with ", decision, "."),
            "ReviewItem", message.ReviewItemId.Trim(),
            message.MessageId, DateTimeOffset.UtcNow + DefaultRetention);
    }

    public static NotificationInput FromUploadRejected(MediaValidated message)
    {
        ArgumentNullException.ThrowIfNull(message);
        RequireIdentity(message.TenantId, message.ProjectId);
        if (message.IsValid)
        {
            throw new DomainException("FromUploadRejected requires an invalid MediaValidated message.");
        }

        return new NotificationInput(
            message.TenantId, message.ProjectId,
            NotificationType.UploadRejected, NotificationSeverity.Warning,
            "Upload rejected",
            "Uploaded media was rejected during validation.",
            "MediaAsset", message.MediaAssetId.Trim(),
            message.MessageId, DateTimeOffset.UtcNow + DefaultRetention);
    }

    public static NotificationInput FromExport(
        Guid tenantId,
        Guid projectId,
        Guid exportJobId,
        string format,
        bool success,
        Guid sourceMessageId)
    {
        RequireIdentity(tenantId, projectId);
        if (exportJobId == Guid.Empty)
        {
            throw new DomainException("ExportJobId must not be empty.");
        }

        var wireFormat = string.IsNullOrWhiteSpace(format) ? "export" : format.Trim();
        return success
            ? new NotificationInput(
                tenantId, projectId,
                NotificationType.ExportCompleted, NotificationSeverity.Info,
                "Export completed",
                string.Concat("Export ", Short(exportJobId), " (", wireFormat, ") completed."),
                "ExportJob", exportJobId.ToString("N"),
                sourceMessageId, DateTimeOffset.UtcNow + DefaultRetention)
            : new NotificationInput(
                tenantId, projectId,
                NotificationType.ExportFailed, NotificationSeverity.Error,
                "Export failed",
                string.Concat("Export ", Short(exportJobId), " (", wireFormat, ") failed."),
                "ExportJob", exportJobId.ToString("N"),
                sourceMessageId, DateTimeOffset.UtcNow + DefaultRetention);
    }

    public static NotificationInput FromQuotaWarning(
        Guid tenantId,
        Guid? projectId,
        string dimension,
        Guid sourceMessageId)
    {
        if (tenantId == Guid.Empty)
        {
            throw new DomainException("TenantId must not be empty.");
        }

        if (projectId.HasValue && projectId.Value == Guid.Empty)
        {
            throw new DomainException("ProjectId must not be empty when set.");
        }

        if (string.IsNullOrWhiteSpace(dimension))
        {
            throw new DomainException("Dimension must not be empty.");
        }

        var trimmed = dimension.Trim();
        return new NotificationInput(
            tenantId, projectId,
            NotificationType.QuotaWarning, NotificationSeverity.Warning,
            "Quota warning",
            string.Concat("Quota '", trimmed, "' is near its limit."),
            projectId.HasValue ? "DubbingProject" : "Tenant",
            (projectId ?? tenantId).ToString("N"),
            sourceMessageId, DateTimeOffset.UtcNow + WarningRetention);
    }

    public static NotificationInput FromProviderPolicyWarning(
        Guid tenantId,
        Guid? projectId,
        string policy,
        Guid sourceMessageId)
    {
        if (tenantId == Guid.Empty)
        {
            throw new DomainException("TenantId must not be empty.");
        }

        if (projectId.HasValue && projectId.Value == Guid.Empty)
        {
            throw new DomainException("ProjectId must not be empty when set.");
        }

        if (string.IsNullOrWhiteSpace(policy))
        {
            throw new DomainException("Policy must not be empty.");
        }

        var trimmed = policy.Trim();
        return new NotificationInput(
            tenantId, projectId,
            NotificationType.ProviderPolicyWarning, NotificationSeverity.Warning,
            "Provider policy warning",
            string.Concat("Provider policy '", trimmed, "' needs attention."),
            projectId.HasValue ? "DubbingProject" : "Tenant",
            (projectId ?? tenantId).ToString("N"),
            sourceMessageId, DateTimeOffset.UtcNow + WarningRetention);
    }

    internal static string Short(Guid id)
    {
        return id.ToString("N").Substring(0, 8);
    }

    private static void RequireIdentity(Guid tenantId, Guid projectId)
    {
        if (tenantId == Guid.Empty)
        {
            throw new DomainException("TenantId must not be empty.");
        }

        if (projectId == Guid.Empty)
        {
            throw new DomainException("ProjectId must not be empty.");
        }
    }
}
