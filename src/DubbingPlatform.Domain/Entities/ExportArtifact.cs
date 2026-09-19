using DubbingPlatform.Domain.Exceptions;

namespace DubbingPlatform.Domain.Entities;

public sealed class ExportArtifact
{
    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid ExportJobId { get; private set; }

    public Guid ArtifactId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    private ExportArtifact()
    {
    }

    public ExportArtifact(
        Guid id,
        Guid tenantId,
        Guid exportJobId,
        Guid artifactId,
        DateTimeOffset createdAt)
    {
        Id = id;
        TenantId = tenantId;
        ExportJobId = exportJobId;
        ArtifactId = artifactId;
        CreatedAt = createdAt;

        Validate();
    }

    public void Validate()
    {
        if (Id == Guid.Empty)
        {
            throw new DomainException("ExportArtifact Id must not be empty.");
        }

        if (TenantId == Guid.Empty)
        {
            throw new DomainException("ExportArtifact TenantId must not be empty.");
        }

        if (ExportJobId == Guid.Empty)
        {
            throw new DomainException("ExportArtifact ExportJobId must not be empty.");
        }

        if (ArtifactId == Guid.Empty)
        {
            throw new DomainException("ExportArtifact ArtifactId must not be empty.");
        }
    }
}
