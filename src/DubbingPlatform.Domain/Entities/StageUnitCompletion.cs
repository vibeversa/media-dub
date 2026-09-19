using DubbingPlatform.Domain.Enums;
using DubbingPlatform.Domain.Exceptions;

namespace DubbingPlatform.Domain.Entities;

/// <summary>
/// Tracks completion of a single stage unit.
/// Allowed <see cref="UnitState"/> values: Completed, Skipped, Failed, ManualReviewRequired, Cancelled.
/// Consumers must reject unknown unit states.
/// </summary>
public sealed class StageUnitCompletion
{
    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid ProcessingRunId { get; private set; }

    public StageType StageType { get; private set; }

    public ScopeType ScopeType { get; private set; }

    public string ScopeId { get; private set; }

    public Guid StageExecutionId { get; private set; }

    public string UnitState { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    private StageUnitCompletion()
    {
        ScopeId = string.Empty;
        UnitState = string.Empty;
    }

    public StageUnitCompletion(
        Guid id,
        Guid tenantId,
        Guid processingRunId,
        StageType stageType,
        ScopeType scopeType,
        string scopeId,
        Guid stageExecutionId,
        string unitState,
        DateTimeOffset createdAt)
    {
        Id = id;
        TenantId = tenantId;
        ProcessingRunId = processingRunId;
        StageType = stageType;
        ScopeType = scopeType;
        ScopeId = scopeId;
        StageExecutionId = stageExecutionId;
        UnitState = unitState;
        CreatedAt = createdAt;

        Validate();
    }

    public void Validate()
    {
        if (Id == Guid.Empty)
        {
            throw new DomainException("StageUnitCompletion Id must not be empty.");
        }

        if (TenantId == Guid.Empty)
        {
            throw new DomainException("StageUnitCompletion TenantId must not be empty.");
        }

        if (ProcessingRunId == Guid.Empty)
        {
            throw new DomainException("StageUnitCompletion ProcessingRunId must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(ScopeId))
        {
            throw new DomainException("StageUnitCompletion ScopeId must not be empty.");
        }

        if (StageExecutionId == Guid.Empty)
        {
            throw new DomainException("StageUnitCompletion StageExecutionId must not be empty.");
        }

        if (UnitState != "Completed"
            && UnitState != "Skipped"
            && UnitState != "Failed"
            && UnitState != "ManualReviewRequired"
            && UnitState != "Cancelled")
        {
            throw new DomainException("StageUnitCompletion UnitState must be one of Completed, Skipped, Failed, ManualReviewRequired, Cancelled.");
        }
    }
}
