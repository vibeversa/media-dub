using DubbingPlatform.Domain.Exceptions;

namespace DubbingPlatform.Domain.Entities;

public sealed class SegmentOverlap
{
    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid OverlapGroupId { get; private set; }

    public Guid SegmentId { get; private set; }

    public string RelationType { get; private set; }

    public int Order { get; private set; }

    public int OverlapStartMs { get; private set; }

    public int OverlapEndMs { get; private set; }

    private SegmentOverlap()
    {
        RelationType = string.Empty;
    }

    public SegmentOverlap(
        Guid id,
        Guid tenantId,
        Guid overlapGroupId,
        Guid segmentId,
        string relationType,
        int order,
        int overlapStartMs,
        int overlapEndMs)
    {
        Id = id;
        TenantId = tenantId;
        OverlapGroupId = overlapGroupId;
        SegmentId = segmentId;
        RelationType = relationType;
        Order = order;
        OverlapStartMs = overlapStartMs;
        OverlapEndMs = overlapEndMs;

        Validate();
    }

    public void Validate()
    {
        if (Id == Guid.Empty)
        {
            throw new DomainException("SegmentOverlap Id must not be empty.");
        }

        if (TenantId == Guid.Empty)
        {
            throw new DomainException("SegmentOverlap TenantId must not be empty.");
        }

        if (OverlapGroupId == Guid.Empty)
        {
            throw new DomainException("SegmentOverlap OverlapGroupId must not be empty.");
        }

        if (SegmentId == Guid.Empty)
        {
            throw new DomainException("SegmentOverlap SegmentId must not be empty.");
        }

        if (RelationType != "Overlap"
            && RelationType != "Contains"
            && RelationType != "ContainedBy"
            && RelationType != "Adjacent")
        {
            throw new DomainException("SegmentOverlap RelationType must be one of Overlap, Contains, ContainedBy, Adjacent.");
        }

        if (Order < 0)
        {
            throw new DomainException("SegmentOverlap Order must be >= 0.");
        }

        if (OverlapStartMs < 0)
        {
            throw new DomainException("SegmentOverlap OverlapStartMs must be >= 0.");
        }

        if (OverlapEndMs <= OverlapStartMs)
        {
            throw new DomainException("SegmentOverlap OverlapEndMs must be greater than OverlapStartMs.");
        }

        var duration = (long)OverlapEndMs - OverlapStartMs;
        if (duration <= 0 || duration > int.MaxValue)
        {
            throw new DomainException("SegmentOverlap duration is out of range.");
        }
    }
}
