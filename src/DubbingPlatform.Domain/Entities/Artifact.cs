using DubbingPlatform.Domain.Enums;
using DubbingPlatform.Domain.Exceptions;

namespace DubbingPlatform.Domain.Entities;

public sealed class Artifact
{
    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid ProjectId { get; private set; }

    public Guid ProcessingRunId { get; private set; }

    public StageType? ProducedByStage { get; private set; }

    public ArtifactType Type { get; private set; }

    public string? SchemaVersion { get; private set; }

    public Guid ContentObjectId { get; private set; }

    public string? Provider { get; private set; }

    public string? Model { get; private set; }

    public string? ConfigurationHash { get; private set; }

    public string? ExecutionSnapshotHash { get; private set; }

    public ArtifactStatus Status { get; private set; }

    public string? MetadataJson { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    private Artifact()
    {
    }

    public Artifact(
        Guid id,
        Guid tenantId,
        Guid projectId,
        Guid processingRunId,
        StageType? producedByStage,
        ArtifactType type,
        string? schemaVersion,
        Guid contentObjectId,
        string? provider,
        string? model,
        string? configurationHash,
        string? executionSnapshotHash,
        ArtifactStatus status,
        string? metadataJson,
        DateTimeOffset createdAt)
    {
        Id = id;
        TenantId = tenantId;
        ProjectId = projectId;
        ProcessingRunId = processingRunId;
        ProducedByStage = producedByStage;
        Type = type;
        SchemaVersion = schemaVersion;
        ContentObjectId = contentObjectId;
        Provider = provider;
        Model = model;
        ConfigurationHash = configurationHash;
        ExecutionSnapshotHash = executionSnapshotHash;
        Status = status;
        MetadataJson = metadataJson;
        CreatedAt = createdAt;

        Validate();
    }

    public void Validate()
    {
        if (Id == Guid.Empty)
        {
            throw new DomainException("Artifact Id must not be empty.");
        }

        if (TenantId == Guid.Empty)
        {
            throw new DomainException("Artifact TenantId must not be empty.");
        }

        if (ProjectId == Guid.Empty)
        {
            throw new DomainException("Artifact ProjectId must not be empty.");
        }

        if (ProcessingRunId == Guid.Empty)
        {
            throw new DomainException("Artifact ProcessingRunId must not be empty.");
        }

        if (ContentObjectId == Guid.Empty)
        {
            throw new DomainException("Artifact ContentObjectId must not be empty.");
        }

        if (SchemaVersion is not null && string.IsNullOrWhiteSpace(SchemaVersion))
        {
            throw new DomainException("Artifact SchemaVersion must not be empty when set.");
        }

        if (Provider is not null && string.IsNullOrWhiteSpace(Provider))
        {
            throw new DomainException("Artifact Provider must not be empty when set.");
        }

        if (Model is not null && string.IsNullOrWhiteSpace(Model))
        {
            throw new DomainException("Artifact Model must not be empty when set.");
        }

        if (ConfigurationHash is not null && string.IsNullOrWhiteSpace(ConfigurationHash))
        {
            throw new DomainException("Artifact ConfigurationHash must not be empty when set.");
        }

        if (ExecutionSnapshotHash is not null && string.IsNullOrWhiteSpace(ExecutionSnapshotHash))
        {
            throw new DomainException("Artifact ExecutionSnapshotHash must not be empty when set.");
        }

        if (MetadataJson is not null && string.IsNullOrWhiteSpace(MetadataJson))
        {
            throw new DomainException("Artifact MetadataJson must not be empty when set.");
        }
    }
}
