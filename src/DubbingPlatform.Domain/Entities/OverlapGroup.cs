using DubbingPlatform.Domain.Exceptions;

namespace DubbingPlatform.Domain.Entities;

public sealed class OverlapGroup
{
    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid ProjectId { get; private set; }

    public Guid RunId { get; private set; }

    public int StartMs { get; private set; }

    public int EndMs { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    private OverlapGroup()
    {
    }

    public OverlapGroup(
        Guid id,
        Guid tenantId,
        Guid projectId,
        Guid runId,
        int startMs,
        int endMs,
        DateTimeOffset createdAt)
    {
        Id = id;
        TenantId = tenantId;
        ProjectId = projectId;
        RunId = runId;
        StartMs = startMs;
        EndMs = endMs;
        CreatedAt = createdAt;

        Validate();
    }

    public void Validate()
    {
        if (Id == Guid.Empty)
        {
            throw new DomainException("OverlapGroup Id must not be empty.");
        }

        if (TenantId == Guid.Empty)
        {
            throw new DomainException("OverlapGroup TenantId must not be empty.");
        }

        if (ProjectId == Guid.Empty)
        {
            throw new DomainException("OverlapGroup ProjectId must not be empty.");
        }

        if (RunId == Guid.Empty)
        {
            throw new DomainException("OverlapGroup RunId must not be empty.");
        }

        if (StartMs < 0)
        {
            throw new DomainException("OverlapGroup StartMs must be >= 0.");
        }

        if (EndMs <= StartMs)
        {
            throw new DomainException("OverlapGroup EndMs must be greater than StartMs.");
        }

        var duration = (long)EndMs - StartMs;
        if (duration <= 0 || duration > int.MaxValue)
        {
            throw new DomainException("OverlapGroup duration is out of range.");
        }
    }
}
