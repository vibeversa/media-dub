using DubbingPlatform.Domain.Exceptions;

namespace DubbingPlatform.Domain.Entities;

public sealed class SegmentContextAssignment
{
    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid SegmentId { get; private set; }

    public Guid ContextWindowId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    private SegmentContextAssignment()
    {
    }

    public SegmentContextAssignment(
        Guid id,
        Guid tenantId,
        Guid segmentId,
        Guid contextWindowId,
        DateTimeOffset createdAt)
    {
        Id = id;
        TenantId = tenantId;
        SegmentId = segmentId;
        ContextWindowId = contextWindowId;
        CreatedAt = createdAt;

        Validate();
    }

    public void Validate()
    {
        if (Id == Guid.Empty)
        {
            throw new DomainException("SegmentContextAssignment Id must not be empty.");
        }

        if (TenantId == Guid.Empty)
        {
            throw new DomainException("SegmentContextAssignment TenantId must not be empty.");
        }

        if (SegmentId == Guid.Empty)
        {
            throw new DomainException("SegmentContextAssignment SegmentId must not be empty.");
        }

        if (ContextWindowId == Guid.Empty)
        {
            throw new DomainException("SegmentContextAssignment ContextWindowId must not be empty.");
        }
    }
}
