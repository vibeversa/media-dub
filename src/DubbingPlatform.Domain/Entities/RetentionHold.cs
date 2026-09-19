using DubbingPlatform.Domain.Exceptions;

namespace DubbingPlatform.Domain.Entities;

public sealed class RetentionHold
{
    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid? ProjectId { get; private set; }

    public Guid? ArtifactId { get; private set; }

    public string Reason { get; private set; }

    public string PlacedBy { get; private set; }

    public DateTimeOffset PlacedAt { get; private set; }

    public DateTimeOffset? ReleasedAt { get; private set; }

    public bool IsActive { get; private set; }

    private RetentionHold()
    {
        Reason = string.Empty;
        PlacedBy = string.Empty;
    }

    public RetentionHold(
        Guid id,
        Guid tenantId,
        Guid? projectId,
        Guid? artifactId,
        string reason,
        string placedBy,
        DateTimeOffset placedAt,
        DateTimeOffset? releasedAt,
        bool isActive)
    {
        Id = id;
        TenantId = tenantId;
        ProjectId = projectId;
        ArtifactId = artifactId;
        Reason = reason;
        PlacedBy = placedBy;
        PlacedAt = placedAt;
        ReleasedAt = releasedAt;
        IsActive = isActive;

        Validate();
    }

    public void Validate()
    {
        if (Id == Guid.Empty)
        {
            throw new DomainException("RetentionHold Id must not be empty.");
        }

        if (TenantId == Guid.Empty)
        {
            throw new DomainException("RetentionHold TenantId must not be empty.");
        }

        if (ProjectId.HasValue && ProjectId.Value == Guid.Empty)
        {
            throw new DomainException("RetentionHold ProjectId must not be empty when set.");
        }

        if (ArtifactId.HasValue && ArtifactId.Value == Guid.Empty)
        {
            throw new DomainException("RetentionHold ArtifactId must not be empty when set.");
        }

        if (string.IsNullOrWhiteSpace(Reason))
        {
            throw new DomainException("RetentionHold Reason must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(PlacedBy))
        {
            throw new DomainException("RetentionHold PlacedBy must not be empty.");
        }

        if (ReleasedAt.HasValue && ReleasedAt.Value < PlacedAt)
        {
            throw new DomainException("RetentionHold ReleasedAt must not be before PlacedAt.");
        }
    }
}
