using DubbingPlatform.Domain.Exceptions;

namespace DubbingPlatform.Domain.Entities;

public sealed class StageInputArtifact
{
    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid StageExecutionId { get; private set; }

    public Guid ArtifactId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    private StageInputArtifact()
    {
    }

    public StageInputArtifact(
        Guid id,
        Guid tenantId,
        Guid stageExecutionId,
        Guid artifactId,
        DateTimeOffset createdAt)
    {
        Id = id;
        TenantId = tenantId;
        StageExecutionId = stageExecutionId;
        ArtifactId = artifactId;
        CreatedAt = createdAt;

        Validate();
    }

    public void Validate()
    {
        if (Id == Guid.Empty)
        {
            throw new DomainException("StageInputArtifact Id must not be empty.");
        }

        if (TenantId == Guid.Empty)
        {
            throw new DomainException("StageInputArtifact TenantId must not be empty.");
        }

        if (StageExecutionId == Guid.Empty)
        {
            throw new DomainException("StageInputArtifact StageExecutionId must not be empty.");
        }

        if (ArtifactId == Guid.Empty)
        {
            throw new DomainException("StageInputArtifact ArtifactId must not be empty.");
        }
    }
}
