using DubbingPlatform.Domain.Enums;
using DubbingPlatform.Domain.Exceptions;

namespace DubbingPlatform.Domain.Entities;

public sealed class Notification
{
    public const int MaxTitleLength = 200;

    public const int MaxBodyLength = 1000;

    public const int MaxResourceTypeLength = 128;

    public const int MaxResourceIdLength = 256;

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid RecipientUserId { get; private set; }

    public Guid? ProjectId { get; private set; }

    public NotificationType Type { get; private set; }

    public NotificationSeverity Severity { get; private set; }

    public string Title { get; private set; }

    public string Body { get; private set; }

    public string ResourceType { get; private set; }

    public string ResourceId { get; private set; }

    public Guid? SourceEventId { get; private set; }

    public DateTimeOffset? ReadAt { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? ExpiresAt { get; private set; }

    private Notification()
    {
        Title = string.Empty;
        Body = string.Empty;
        ResourceType = string.Empty;
        ResourceId = string.Empty;
    }

    public Notification(
        Guid id,
        Guid tenantId,
        Guid recipientUserId,
        Guid? projectId,
        NotificationType type,
        NotificationSeverity severity,
        string title,
        string body,
        string resourceType,
        string resourceId,
        Guid? sourceEventId,
        DateTimeOffset? readAt,
        DateTimeOffset createdAt,
        DateTimeOffset? expiresAt)
    {
        Id = id;
        TenantId = tenantId;
        RecipientUserId = recipientUserId;
        ProjectId = projectId;
        Type = type;
        Severity = severity;
        Title = title;
        Body = body;
        ResourceType = resourceType;
        ResourceId = resourceId;
        SourceEventId = sourceEventId;
        ReadAt = readAt;
        CreatedAt = createdAt;
        ExpiresAt = expiresAt;

        Validate();
    }

    public void Validate()
    {
        if (Id == Guid.Empty)
        {
            throw new DomainException("Notification Id must not be empty.");
        }

        if (TenantId == Guid.Empty)
        {
            throw new DomainException("Notification TenantId must not be empty.");
        }

        if (RecipientUserId == Guid.Empty)
        {
            throw new DomainException("Notification RecipientUserId must not be empty.");
        }

        if (ProjectId.HasValue && ProjectId.Value == Guid.Empty)
        {
            throw new DomainException("Notification ProjectId must not be empty when set.");
        }

        if (ProjectId is null && Type is not NotificationType.QuotaWarning and not NotificationType.ProviderPolicyWarning)
        {
            throw new DomainException($"Notification ProjectId is required for type '{Type}'.");
        }

        if (!Enum.IsDefined(Type))
        {
            throw new DomainException("Notification Type is not defined.");
        }

        if (!Enum.IsDefined(Severity))
        {
            throw new DomainException("Notification Severity is not defined.");
        }

        if (string.IsNullOrWhiteSpace(Title))
        {
            throw new DomainException("Notification Title must not be empty.");
        }

        if (Title.Length > MaxTitleLength)
        {
            throw new DomainException($"Notification Title must be at most {MaxTitleLength} chars.");
        }

        if (string.IsNullOrWhiteSpace(Body))
        {
            throw new DomainException("Notification Body must not be empty.");
        }

        if (Body.Length > MaxBodyLength)
        {
            throw new DomainException($"Notification Body must be at most {MaxBodyLength} chars.");
        }

        if (string.IsNullOrWhiteSpace(ResourceType))
        {
            throw new DomainException("Notification ResourceType must not be empty.");
        }

        if (ResourceType.Length > MaxResourceTypeLength)
        {
            throw new DomainException($"Notification ResourceType must be at most {MaxResourceTypeLength} chars.");
        }

        if (string.IsNullOrWhiteSpace(ResourceId))
        {
            throw new DomainException("Notification ResourceId must not be empty.");
        }

        if (ResourceId.Length > MaxResourceIdLength)
        {
            throw new DomainException($"Notification ResourceId must be at most {MaxResourceIdLength} chars.");
        }

        if (SourceEventId.HasValue && SourceEventId.Value == Guid.Empty)
        {
            throw new DomainException("Notification SourceEventId must not be empty when set.");
        }

        if (ReadAt.HasValue && ReadAt.Value < CreatedAt)
        {
            throw new DomainException("Notification ReadAt must not be before CreatedAt.");
        }

        if (ExpiresAt.HasValue && ExpiresAt.Value <= CreatedAt)
        {
            throw new DomainException("Notification ExpiresAt must be after CreatedAt.");
        }

        ThrowIfUnsafeContent(Title, nameof(Title));
        ThrowIfUnsafeContent(Body, nameof(Body));
        ThrowIfUnsafeContent(ResourceId, nameof(ResourceId));
    }

    public void MarkAsRead(DateTimeOffset readAt)
    {
        if (readAt < CreatedAt)
        {
            throw new DomainException("Notification ReadAt must not be before CreatedAt.");
        }

        ReadAt = readAt;
        Validate();
    }

    internal static void ThrowIfUnsafeContent(string value, string fieldName)
    {
        if (value.Contains("http://", StringComparison.OrdinalIgnoreCase)
            || value.Contains("https://", StringComparison.OrdinalIgnoreCase))
        {
            throw new DomainException($"Notification {fieldName} must not contain URLs.");
        }

        if (value.Contains("bearer ", StringComparison.OrdinalIgnoreCase))
        {
            throw new DomainException($"Notification {fieldName} must not contain tokens.");
        }
    }
}
