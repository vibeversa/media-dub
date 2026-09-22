using DubbingPlatform.Domain.Enums;
using DubbingPlatform.Domain.Exceptions;

namespace DubbingPlatform.Domain.Entities;

public sealed class ProjectMembership
{
    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid ProjectId { get; private set; }

    public Guid UserId { get; private set; }

    public ProjectRole Role { get; private set; }

    public Guid? GrantedByUserId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    private ProjectMembership()
    {
    }

    public ProjectMembership(
        Guid id,
        Guid tenantId,
        Guid projectId,
        Guid userId,
        ProjectRole role,
        Guid? grantedByUserId,
        DateTimeOffset createdAt)
    {
        Id = id;
        TenantId = tenantId;
        ProjectId = projectId;
        UserId = userId;
        Role = role;
        GrantedByUserId = grantedByUserId;
        CreatedAt = createdAt;

        Validate();
    }

    public void Validate()
    {
        if (Id == Guid.Empty)
        {
            throw new DomainException("ProjectMembership Id must not be empty.");
        }

        if (TenantId == Guid.Empty)
        {
            throw new DomainException("ProjectMembership TenantId must not be empty.");
        }

        if (ProjectId == Guid.Empty)
        {
            throw new DomainException("ProjectMembership ProjectId must not be empty.");
        }

        if (UserId == Guid.Empty)
        {
            throw new DomainException("ProjectMembership UserId must not be empty.");
        }

        if (!Enum.IsDefined(Role))
        {
            throw new DomainException("ProjectMembership Role is not defined.");
        }

        if (GrantedByUserId.HasValue && GrantedByUserId.Value == Guid.Empty)
        {
            throw new DomainException("ProjectMembership GrantedByUserId must not be empty when set.");
        }
    }

    public void ChangeRole(ProjectRole role)
    {
        Role = role;
        Validate();
    }
}
