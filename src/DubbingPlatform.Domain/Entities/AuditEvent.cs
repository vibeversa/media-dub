using DubbingPlatform.Domain.Exceptions;

namespace DubbingPlatform.Domain.Entities;

/// <summary>
/// Append-only audit event. Immutable by convention: no UpdatedAt and no update mutators.
/// </summary>
public sealed class AuditEvent
{
    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid? ProjectId { get; private set; }

    public string Actor { get; private set; }

    public string Action { get; private set; }

    public string ResourceType { get; private set; }

    public string ResourceId { get; private set; }

    public string? DetailsJson { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    private AuditEvent()
    {
        Actor = string.Empty;
        Action = string.Empty;
        ResourceType = string.Empty;
        ResourceId = string.Empty;
    }

    public AuditEvent(
        Guid id,
        Guid tenantId,
        Guid? projectId,
        string actor,
        string action,
        string resourceType,
        string resourceId,
        string? detailsJson,
        DateTimeOffset createdAt)
    {
        Id = id;
        TenantId = tenantId;
        ProjectId = projectId;
        Actor = actor;
        Action = action;
        ResourceType = resourceType;
        ResourceId = resourceId;
        DetailsJson = detailsJson;
        CreatedAt = createdAt;

        Validate();
    }

    public void Validate()
    {
        if (Id == Guid.Empty)
        {
            throw new DomainException("AuditEvent Id must not be empty.");
        }

        if (TenantId == Guid.Empty)
        {
            throw new DomainException("AuditEvent TenantId must not be empty.");
        }

        if (ProjectId.HasValue && ProjectId.Value == Guid.Empty)
        {
            throw new DomainException("AuditEvent ProjectId must not be empty when set.");
        }

        if (string.IsNullOrWhiteSpace(Actor))
        {
            throw new DomainException("AuditEvent Actor must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(Action))
        {
            throw new DomainException("AuditEvent Action must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(ResourceType))
        {
            throw new DomainException("AuditEvent ResourceType must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(ResourceId))
        {
            throw new DomainException("AuditEvent ResourceId must not be empty.");
        }

        if (DetailsJson is not null && string.IsNullOrWhiteSpace(DetailsJson))
        {
            throw new DomainException("AuditEvent DetailsJson must not be empty when set.");
        }
    }
}
