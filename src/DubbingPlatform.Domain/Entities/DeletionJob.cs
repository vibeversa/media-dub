using DubbingPlatform.Domain.Exceptions;

namespace DubbingPlatform.Domain.Entities;

/// <summary>
/// Logical-then-physical deletion job.
/// Allowed <see cref="Status"/> values: Pending, Running, Completed, Failed, Cancelled.
/// Consumers must reject unknown statuses.
/// </summary>
public sealed class DeletionJob
{
    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid? ProjectId { get; private set; }

    public string Scope { get; private set; }

    public string Status { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    private DeletionJob()
    {
        Scope = string.Empty;
        Status = string.Empty;
    }

    public DeletionJob(
        Guid id,
        Guid tenantId,
        Guid? projectId,
        string scope,
        string status,
        DateTimeOffset createdAt,
        DateTimeOffset? completedAt)
    {
        Id = id;
        TenantId = tenantId;
        ProjectId = projectId;
        Scope = scope;
        Status = status;
        CreatedAt = createdAt;
        CompletedAt = completedAt;

        Validate();
    }

    public void Validate()
    {
        if (Id == Guid.Empty)
        {
            throw new DomainException("DeletionJob Id must not be empty.");
        }

        if (TenantId == Guid.Empty)
        {
            throw new DomainException("DeletionJob TenantId must not be empty.");
        }

        if (ProjectId.HasValue && ProjectId.Value == Guid.Empty)
        {
            throw new DomainException("DeletionJob ProjectId must not be empty when set.");
        }

        if (string.IsNullOrWhiteSpace(Scope))
        {
            throw new DomainException("DeletionJob Scope must not be empty.");
        }

        if (Status != "Pending"
            && Status != "Running"
            && Status != "Completed"
            && Status != "Failed"
            && Status != "Cancelled")
        {
            throw new DomainException("DeletionJob Status must be one of Pending, Running, Completed, Failed, Cancelled.");
        }

        if (CompletedAt.HasValue && CompletedAt.Value < CreatedAt)
        {
            throw new DomainException("DeletionJob CompletedAt must not be before CreatedAt.");
        }
    }
}
