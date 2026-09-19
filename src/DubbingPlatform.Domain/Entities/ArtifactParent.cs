using DubbingPlatform.Domain.Exceptions;

namespace DubbingPlatform.Domain.Entities;

/// <summary>
/// Relational artifact lineage edge: child derived from parent.
/// Lineage must stay relational (this table plus stage input/output tables), never JSON-only.
/// </summary>
public sealed class ArtifactParent
{
    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid ChildArtifactId { get; private set; }

    public Guid ParentArtifactId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    private ArtifactParent()
    {
    }

    public ArtifactParent(
        Guid id,
        Guid tenantId,
        Guid childArtifactId,
        Guid parentArtifactId,
        DateTimeOffset createdAt)
    {
        Id = id;
        TenantId = tenantId;
        ChildArtifactId = childArtifactId;
        ParentArtifactId = parentArtifactId;
        CreatedAt = createdAt;

        Validate();
    }

    public void Validate()
    {
        if (Id == Guid.Empty)
        {
            throw new DomainException("ArtifactParent Id must not be empty.");
        }

        if (TenantId == Guid.Empty)
        {
            throw new DomainException("ArtifactParent TenantId must not be empty.");
        }

        if (ChildArtifactId == Guid.Empty)
        {
            throw new DomainException("ArtifactParent ChildArtifactId must not be empty.");
        }

        if (ParentArtifactId == Guid.Empty)
        {
            throw new DomainException("ArtifactParent ParentArtifactId must not be empty.");
        }

        if (ChildArtifactId == ParentArtifactId)
        {
            throw new DomainException("ArtifactParent ChildArtifactId and ParentArtifactId must differ.");
        }
    }
}
