using DubbingPlatform.Domain.Exceptions;

namespace DubbingPlatform.Domain.Entities;

public sealed class PromptTemplate
{
    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public string Name { get; private set; }

    public string Description { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    private PromptTemplate()
    {
        Name = string.Empty;
        Description = string.Empty;
    }

    public PromptTemplate(
        Guid id,
        Guid tenantId,
        string name,
        string description,
        DateTimeOffset createdAt)
    {
        Id = id;
        TenantId = tenantId;
        Name = name;
        Description = description;
        CreatedAt = createdAt;

        Validate();
    }

    public void Validate()
    {
        if (Id == Guid.Empty)
        {
            throw new DomainException("PromptTemplate Id must not be empty.");
        }

        if (TenantId == Guid.Empty)
        {
            throw new DomainException("PromptTemplate TenantId must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(Name))
        {
            throw new DomainException("PromptTemplate Name must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(Description))
        {
            throw new DomainException("PromptTemplate Description must not be empty.");
        }
    }
}
