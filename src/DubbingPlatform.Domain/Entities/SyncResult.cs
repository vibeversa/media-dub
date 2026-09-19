using DubbingPlatform.Domain.Enums;
using DubbingPlatform.Domain.Exceptions;

namespace DubbingPlatform.Domain.Entities;

public sealed class SyncResult
{
    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid ProjectId { get; private set; }

    public Guid RunId { get; private set; }

    public Guid SegmentId { get; private set; }

    public double SyncScore { get; private set; }

    public SyncStatus Status { get; private set; }

    public int TargetWindowMs { get; private set; }

    public int ActualDurationMs { get; private set; }

    public double RateDelta { get; private set; }

    public double StretchFactor { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    private SyncResult()
    {
    }

    public SyncResult(
        Guid id,
        Guid tenantId,
        Guid projectId,
        Guid runId,
        Guid segmentId,
        double syncScore,
        SyncStatus status,
        int targetWindowMs,
        int actualDurationMs,
        double rateDelta,
        double stretchFactor,
        DateTimeOffset createdAt)
    {
        Id = id;
        TenantId = tenantId;
        ProjectId = projectId;
        RunId = runId;
        SegmentId = segmentId;
        SyncScore = syncScore;
        Status = status;
        TargetWindowMs = targetWindowMs;
        ActualDurationMs = actualDurationMs;
        RateDelta = rateDelta;
        StretchFactor = stretchFactor;
        CreatedAt = createdAt;

        Validate();
    }

    public void Validate()
    {
        if (Id == Guid.Empty)
        {
            throw new DomainException("SyncResult Id must not be empty.");
        }

        if (TenantId == Guid.Empty)
        {
            throw new DomainException("SyncResult TenantId must not be empty.");
        }

        if (ProjectId == Guid.Empty)
        {
            throw new DomainException("SyncResult ProjectId must not be empty.");
        }

        if (RunId == Guid.Empty)
        {
            throw new DomainException("SyncResult RunId must not be empty.");
        }

        if (SegmentId == Guid.Empty)
        {
            throw new DomainException("SyncResult SegmentId must not be empty.");
        }

        if (double.IsNaN(SyncScore) || SyncScore < 0.0 || SyncScore > 1.0)
        {
            throw new DomainException("SyncResult SyncScore must be in 0..1.");
        }

        if (TargetWindowMs < 0)
        {
            throw new DomainException("SyncResult TargetWindowMs must be >= 0.");
        }

        if (ActualDurationMs < 0)
        {
            throw new DomainException("SyncResult ActualDurationMs must be >= 0.");
        }

        if (double.IsNaN(RateDelta))
        {
            throw new DomainException("SyncResult RateDelta must not be NaN.");
        }

        if (double.IsNaN(StretchFactor) || StretchFactor <= 0.0)
        {
            throw new DomainException("SyncResult StretchFactor must be > 0.");
        }
    }
}
