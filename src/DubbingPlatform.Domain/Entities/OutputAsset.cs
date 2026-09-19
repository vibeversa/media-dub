using DubbingPlatform.Domain.Exceptions;

namespace DubbingPlatform.Domain.Entities;

/// <summary>
/// Rendered output asset referencing its artifact.
/// Allowed <see cref="MediaKind"/> values: Video, Audio.
/// Consumers must reject unknown media kinds.
/// </summary>
public sealed class OutputAsset
{
    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid ProjectId { get; private set; }

    public Guid ProcessingRunId { get; private set; }

    public Guid ArtifactId { get; private set; }

    public string MediaKind { get; private set; }

    public int DurationMs { get; private set; }

    public string Container { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    private OutputAsset()
    {
        MediaKind = string.Empty;
        Container = string.Empty;
    }

    public OutputAsset(
        Guid id,
        Guid tenantId,
        Guid projectId,
        Guid processingRunId,
        Guid artifactId,
        string mediaKind,
        int durationMs,
        string container,
        DateTimeOffset createdAt)
    {
        Id = id;
        TenantId = tenantId;
        ProjectId = projectId;
        ProcessingRunId = processingRunId;
        ArtifactId = artifactId;
        MediaKind = mediaKind;
        DurationMs = durationMs;
        Container = container;
        CreatedAt = createdAt;

        Validate();
    }

    public void Validate()
    {
        if (Id == Guid.Empty)
        {
            throw new DomainException("OutputAsset Id must not be empty.");
        }

        if (TenantId == Guid.Empty)
        {
            throw new DomainException("OutputAsset TenantId must not be empty.");
        }

        if (ProjectId == Guid.Empty)
        {
            throw new DomainException("OutputAsset ProjectId must not be empty.");
        }

        if (ProcessingRunId == Guid.Empty)
        {
            throw new DomainException("OutputAsset ProcessingRunId must not be empty.");
        }

        if (ArtifactId == Guid.Empty)
        {
            throw new DomainException("OutputAsset ArtifactId must not be empty.");
        }

        if (MediaKind != "Video" && MediaKind != "Audio")
        {
            throw new DomainException("OutputAsset MediaKind must be one of Video, Audio.");
        }

        if (DurationMs < 0)
        {
            throw new DomainException("OutputAsset DurationMs must be >= 0.");
        }

        if (string.IsNullOrWhiteSpace(Container))
        {
            throw new DomainException("OutputAsset Container must not be empty.");
        }
    }
}
