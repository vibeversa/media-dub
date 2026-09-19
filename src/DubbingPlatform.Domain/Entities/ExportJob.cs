using DubbingPlatform.Domain.Enums;
using DubbingPlatform.Domain.Exceptions;

namespace DubbingPlatform.Domain.Entities;

public sealed class ExportJob
{
    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid ProjectId { get; private set; }

    public Guid ProcessingRunId { get; private set; }

    public ExportFormat Format { get; private set; }

    public ExportJobStatus Status { get; private set; }

    public string? ArtifactIdRef { get; private set; }

    public string? CompletenessJson { get; private set; }

    public bool IsPartial { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    private ExportJob()
    {
    }

    public ExportJob(
        Guid id,
        Guid tenantId,
        Guid projectId,
        Guid processingRunId,
        ExportFormat format,
        ExportJobStatus status,
        string? artifactIdRef,
        string? completenessJson,
        bool isPartial,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt)
    {
        Id = id;
        TenantId = tenantId;
        ProjectId = projectId;
        ProcessingRunId = processingRunId;
        Format = format;
        Status = status;
        ArtifactIdRef = artifactIdRef;
        CompletenessJson = completenessJson;
        IsPartial = isPartial;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;

        Validate();
    }

    public void Validate()
    {
        if (Id == Guid.Empty)
        {
            throw new DomainException("ExportJob Id must not be empty.");
        }

        if (TenantId == Guid.Empty)
        {
            throw new DomainException("ExportJob TenantId must not be empty.");
        }

        if (ProjectId == Guid.Empty)
        {
            throw new DomainException("ExportJob ProjectId must not be empty.");
        }

        if (ProcessingRunId == Guid.Empty)
        {
            throw new DomainException("ExportJob ProcessingRunId must not be empty.");
        }

        if (ArtifactIdRef is not null && string.IsNullOrWhiteSpace(ArtifactIdRef))
        {
            throw new DomainException("ExportJob ArtifactIdRef must not be empty when set.");
        }

        if (CompletenessJson is not null && string.IsNullOrWhiteSpace(CompletenessJson))
        {
            throw new DomainException("ExportJob CompletenessJson must not be empty when set.");
        }
    }
}
