using DubbingPlatform.Contracts.Messages;
using DubbingPlatform.Domain.Enums;
using DubbingPlatform.Domain.Exceptions;

namespace DubbingPlatform.Application.Activity;

/// <summary>
/// Pure mapping from backend events and outbox messages to
/// <see cref="ActivityInput"/>. Summaries carry short ids and names only;
/// metadata carries ids, counts, and version markers via
/// <see cref="ActivityProjector.BuildMetadata"/> — never media, text, storage
/// keys, signed URLs, or secrets.
/// </summary>
public static class ActivityEventMapper
{
    public static ActivityInput FromUploadCompleted(MediaUploaded message)
    {
        ArgumentNullException.ThrowIfNull(message);
        RequireIdentity(message.TenantId, message.ProjectId);
        return new ActivityInput(
            message.TenantId, message.ProjectId, null,
            ActivityType.UploadCompleted, ActivityActorType.System, null,
            "Upload completed.",
            ActivitySeverity.Info,
            CorrelationFor(message.CorrelationId, message.MessageId),
            message.CreatedAt,
            ActivityProjector.BuildMetadata(new Dictionary<string, object?>
            {
                ["uploadSessionId"] = message.UploadSessionId.Trim(),
                ["projectId"] = message.ProjectId.ToString("N"),
            }));
    }

    public static ActivityInput FromUploadRejected(MediaValidated message)
    {
        ArgumentNullException.ThrowIfNull(message);
        RequireIdentity(message.TenantId, message.ProjectId);
        if (message.IsValid)
        {
            throw new DomainException("FromUploadRejected requires an invalid MediaValidated message.");
        }

        return new ActivityInput(
            message.TenantId, message.ProjectId, null,
            ActivityType.UploadRejected, ActivityActorType.System, null,
            "Upload rejected during validation.",
            ActivitySeverity.Warning,
            CorrelationFor(message.CorrelationId, message.MessageId),
            message.CreatedAt,
            ActivityProjector.BuildMetadata(new Dictionary<string, object?>
            {
                ["mediaAssetId"] = message.MediaAssetId.Trim(),
                ["projectId"] = message.ProjectId.ToString("N"),
            }));
    }

    public static ActivityInput FromProcessingStarted(RunStarted message)
    {
        ArgumentNullException.ThrowIfNull(message);
        RequireIdentity(message.TenantId, message.ProjectId);
        if (message.ProcessingRunId == Guid.Empty)
        {
            throw new DomainException("ProcessingRunId must not be empty.");
        }

        var pipeline = string.IsNullOrWhiteSpace(message.PipelineVersion) ? "default" : message.PipelineVersion.Trim();
        return new ActivityInput(
            message.TenantId, message.ProjectId, message.ProcessingRunId,
            ActivityType.ProcessingStarted, ActivityActorType.System, null,
            string.Concat("Processing started (pipeline ", pipeline, ")."),
            ActivitySeverity.Info,
            CorrelationFor(message.CorrelationId, message.MessageId),
            message.CreatedAt,
            ActivityProjector.BuildMetadata(new Dictionary<string, object?>
            {
                ["runId"] = message.ProcessingRunId.ToString("N"),
                ["pipelineVersion"] = pipeline,
            }));
    }

    public static ActivityInput FromTranslationCompleted(StageCompleted message)
    {
        ArgumentNullException.ThrowIfNull(message);
        RequireIdentity(message.TenantId, message.ProjectId);
        return new ActivityInput(
            message.TenantId, message.ProjectId,
            message.ProcessingRunId == Guid.Empty ? null : message.ProcessingRunId,
            ActivityType.TranslationCompleted, ActivityActorType.System, null,
            string.Concat("Stage ", message.CompletedStageType.Trim(), " completed."),
            ActivitySeverity.Info,
            CorrelationFor(message.CorrelationId, message.MessageId),
            message.CreatedAt,
            ActivityProjector.BuildMetadata(new Dictionary<string, object?>
            {
                ["stageType"] = message.CompletedStageType.Trim(),
                ["runId"] = message.ProcessingRunId.ToString("N"),
            }));
    }

    public static ActivityInput FromReviewRequested(StageReviewRequired message)
    {
        ArgumentNullException.ThrowIfNull(message);
        RequireIdentity(message.TenantId, message.ProjectId);
        var stage = string.IsNullOrWhiteSpace(message.BlockedStageType) ? "a stage" : message.BlockedStageType.Trim();
        return new ActivityInput(
            message.TenantId, message.ProjectId,
            message.ProcessingRunId == Guid.Empty ? null : message.ProcessingRunId,
            ActivityType.ReviewRequested, ActivityActorType.System, null,
            string.Concat("Review requested for stage ", stage, "."),
            ActivitySeverity.Warning,
            CorrelationFor(message.CorrelationId, message.MessageId),
            message.CreatedAt,
            ActivityProjector.BuildMetadata(new Dictionary<string, object?>
            {
                ["reviewItemId"] = message.ReviewItemId.Trim(),
                ["stageType"] = stage,
            }));
    }

    public static ActivityInput FromReviewResolved(ReviewResolved message)
    {
        ArgumentNullException.ThrowIfNull(message);
        RequireIdentity(message.TenantId, message.ProjectId);
        var decision = string.IsNullOrWhiteSpace(message.Decision) ? "resolved" : message.Decision.Trim();
        return new ActivityInput(
            message.TenantId, message.ProjectId,
            message.ProcessingRunId == Guid.Empty ? null : message.ProcessingRunId,
            ActivityType.ReviewResolved, ActivityActorType.System, null,
            string.Concat("Review resolved with ", decision, "."),
            ActivitySeverity.Info,
            CorrelationFor(message.CorrelationId, message.MessageId),
            message.CreatedAt,
            ActivityProjector.BuildMetadata(new Dictionary<string, object?>
            {
                ["reviewItemId"] = message.ReviewItemId.Trim(),
                ["decision"] = decision,
            }));
    }

    public static ActivityInput FromEditApplied(
        Guid tenantId,
        Guid projectId,
        Guid? processingRunId,
        Guid reviewId,
        Guid? actorUserId,
        string correlationId,
        DateTimeOffset occurredAt)
    {
        RequireIdentity(tenantId, projectId);
        if (reviewId == Guid.Empty)
        {
            throw new DomainException("ReviewId must not be empty.");
        }

        return new ActivityInput(
            tenantId, projectId, processingRunId,
            ActivityType.EditApplied,
            actorUserId.HasValue ? ActivityActorType.User : ActivityActorType.System,
            actorUserId,
            "Reviewer edit applied.",
            ActivitySeverity.Info,
            RequireCorrelation(correlationId),
            occurredAt,
            ActivityProjector.BuildMetadata(new Dictionary<string, object?>
            {
                ["reviewId"] = reviewId.ToString("N"),
            }));
    }

    public static ActivityInput FromExportCompleted(
        Guid tenantId,
        Guid projectId,
        Guid? processingRunId,
        Guid exportJobId,
        string format,
        bool success,
        string correlationId,
        DateTimeOffset occurredAt)
    {
        RequireIdentity(tenantId, projectId);
        if (exportJobId == Guid.Empty)
        {
            throw new DomainException("ExportJobId must not be empty.");
        }

        var wireFormat = string.IsNullOrWhiteSpace(format) ? "export" : format.Trim();
        return new ActivityInput(
            tenantId, projectId, processingRunId,
            success ? ActivityType.ExportCompleted : ActivityType.ExportFailed,
            ActivityActorType.System, null,
            success
                ? string.Concat("Export (", wireFormat, ") completed.")
                : string.Concat("Export (", wireFormat, ") failed."),
            success ? ActivitySeverity.Info : ActivitySeverity.Error,
            RequireCorrelation(correlationId),
            occurredAt,
            ActivityProjector.BuildMetadata(new Dictionary<string, object?>
            {
                ["exportJobId"] = exportJobId.ToString("N"),
                ["format"] = wireFormat,
            }));
    }

    public static ActivityInput FromProcessingCompleted(RunCompleted message)
    {
        ArgumentNullException.ThrowIfNull(message);
        RequireIdentity(message.TenantId, message.ProjectId);
        return new ActivityInput(
            message.TenantId, message.ProjectId,
            message.ProcessingRunId == Guid.Empty ? null : message.ProcessingRunId,
            ActivityType.ProcessingCompleted, ActivityActorType.System, null,
            "Processing completed.",
            ActivitySeverity.Info,
            CorrelationFor(message.CorrelationId, message.MessageId),
            message.CreatedAt,
            ActivityProjector.BuildMetadata(new Dictionary<string, object?>
            {
                ["runId"] = message.ProcessingRunId.ToString("N"),
            }));
    }

    public static ActivityInput FromProcessingFailed(RunFailed message)
    {
        ArgumentNullException.ThrowIfNull(message);
        RequireIdentity(message.TenantId, message.ProjectId);
        var code = string.IsNullOrWhiteSpace(message.ErrorCode) ? "UNKNOWN" : message.ErrorCode.Trim();
        return new ActivityInput(
            message.TenantId, message.ProjectId,
            message.ProcessingRunId == Guid.Empty ? null : message.ProcessingRunId,
            ActivityType.ProcessingFailed, ActivityActorType.System, null,
            string.Concat("Processing failed (", code, ")."),
            ActivitySeverity.Error,
            CorrelationFor(message.CorrelationId, message.MessageId),
            message.CreatedAt,
            ActivityProjector.BuildMetadata(new Dictionary<string, object?>
            {
                ["runId"] = message.ProcessingRunId.ToString("N"),
                ["errorCode"] = code,
            }));
    }

    internal static string CorrelationFor(string? correlationId, Guid messageId)
    {
        return string.IsNullOrWhiteSpace(correlationId) ? messageId.ToString("N") : correlationId.Trim();
    }

    internal static string RequireCorrelation(string? correlationId)
    {
        if (string.IsNullOrWhiteSpace(correlationId))
        {
            throw new DomainException("CorrelationId must not be empty.");
        }

        return correlationId.Trim();
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
