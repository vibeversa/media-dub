using DubbingPlatform.Domain.Exceptions;

namespace DubbingPlatform.Domain.Entities;

public sealed class Tenant
{
    public Guid Id { get; private set; }

    public string Name { get; private set; }

    public string Slug { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    private Tenant()
    {
        Name = string.Empty;
        Slug = string.Empty;
    }

    public Tenant(Guid id, string name, string slug, DateTimeOffset createdAt)
    {
        Id = id;
        Name = name;
        Slug = slug;
        CreatedAt = createdAt;

        Validate();
    }

    public void Validate()
    {
        if (Id == Guid.Empty)
        {
            throw new DomainException("Tenant Id must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(Name))
        {
            throw new DomainException("Tenant Name must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(Slug))
        {
            throw new DomainException("Tenant Slug must not be empty.");
        }
    }
}
