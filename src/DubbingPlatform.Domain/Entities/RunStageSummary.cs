using DubbingPlatform.Domain.Enums;
using DubbingPlatform.Domain.Exceptions;

namespace DubbingPlatform.Domain.Entities;

public sealed class RunStageSummary
{
    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid ProcessingRunId { get; private set; }

    public StageType StageType { get; private set; }

    public int ExpectedUnits { get; private set; }

    public int CompletedUnits { get; private set; }

    public int FailedUnits { get; private set; }

    public int SkippedUnits { get; private set; }

    public int ReviewUnits { get; private set; }

    public int CancelledUnits { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    private RunStageSummary()
    {
    }

    public RunStageSummary(
        Guid id,
        Guid tenantId,
        Guid processingRunId,
        StageType stageType,
        int expectedUnits,
        int completedUnits,
        int failedUnits,
        int skippedUnits,
        int reviewUnits,
        int cancelledUnits,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt)
    {
        Id = id;
        TenantId = tenantId;
        ProcessingRunId = processingRunId;
        StageType = stageType;
        ExpectedUnits = expectedUnits;
        CompletedUnits = completedUnits;
        FailedUnits = failedUnits;
        SkippedUnits = skippedUnits;
        ReviewUnits = reviewUnits;
        CancelledUnits = cancelledUnits;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;

        Validate();
    }

    public void Validate()
    {
        if (Id == Guid.Empty)
        {
            throw new DomainException("RunStageSummary Id must not be empty.");
        }

        if (TenantId == Guid.Empty)
        {
            throw new DomainException("RunStageSummary TenantId must not be empty.");
        }

        if (ProcessingRunId == Guid.Empty)
        {
            throw new DomainException("RunStageSummary ProcessingRunId must not be empty.");
        }

        if (ExpectedUnits < 0)
        {
            throw new DomainException("RunStageSummary ExpectedUnits must be >= 0.");
        }

        if (CompletedUnits < 0)
        {
            throw new DomainException("RunStageSummary CompletedUnits must be >= 0.");
        }

        if (FailedUnits < 0)
        {
            throw new DomainException("RunStageSummary FailedUnits must be >= 0.");
        }

        if (SkippedUnits < 0)
        {
            throw new DomainException("RunStageSummary SkippedUnits must be >= 0.");
        }

        if (ReviewUnits < 0)
        {
            throw new DomainException("RunStageSummary ReviewUnits must be >= 0.");
        }

        if (CancelledUnits < 0)
        {
            throw new DomainException("RunStageSummary CancelledUnits must be >= 0.");
        }
    }
}
