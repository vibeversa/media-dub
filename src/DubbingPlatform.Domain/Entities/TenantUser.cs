using DubbingPlatform.Domain.Enums;
using DubbingPlatform.Domain.Exceptions;

namespace DubbingPlatform.Domain.Entities;

public sealed class TenantUser
{
    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public string ExternalSubject { get; private set; }

    public string Email { get; private set; }

    public string DisplayName { get; private set; }

    public TenantUserStatus Status { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    private TenantUser()
    {
        ExternalSubject = string.Empty;
        Email = string.Empty;
        DisplayName = string.Empty;
    }

    public TenantUser(
        Guid id,
        Guid tenantId,
        string externalSubject,
        string email,
        string displayName,
        TenantUserStatus status,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt)
    {
        Id = id;
        TenantId = tenantId;
        ExternalSubject = externalSubject;
        Email = email;
        DisplayName = displayName;
        Status = status;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;

        Validate();
    }

    public void Validate()
    {
        if (Id == Guid.Empty)
        {
            throw new DomainException("TenantUser Id must not be empty.");
        }

        if (TenantId == Guid.Empty)
        {
            throw new DomainException("TenantUser TenantId must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(ExternalSubject))
        {
            throw new DomainException("TenantUser ExternalSubject must not be empty.");
        }

        if (ExternalSubject.Length > 256)
        {
            throw new DomainException("TenantUser ExternalSubject must be at most 256 chars.");
        }

        if (string.IsNullOrWhiteSpace(Email))
        {
            throw new DomainException("TenantUser Email must not be empty.");
        }

        if (Email.Length > 256)
        {
            throw new DomainException("TenantUser Email must be at most 256 chars.");
        }

        if (!Email.Contains('@', StringComparison.Ordinal))
        {
            throw new DomainException("TenantUser Email must contain '@'.");
        }

        if (string.IsNullOrWhiteSpace(DisplayName))
        {
            throw new DomainException("TenantUser DisplayName must not be empty.");
        }

        if (DisplayName.Length > 256)
        {
            throw new DomainException("TenantUser DisplayName must be at most 256 chars.");
        }

        if (!Enum.IsDefined(Status))
        {
            throw new DomainException("TenantUser Status is not defined.");
        }
    }

    public void Disable(DateTimeOffset updatedAt)
    {
        Status = TenantUserStatus.Disabled;
        UpdatedAt = updatedAt;
        Validate();
    }

    public void Enable(DateTimeOffset updatedAt)
    {
        Status = TenantUserStatus.Active;
        UpdatedAt = updatedAt;
        Validate();
    }
}
