using DubbingPlatform.Domain.Enums;
using DubbingPlatform.Domain.Exceptions;

namespace DubbingPlatform.Domain.Entities;

public sealed class QualityResult
{
    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid ProjectId { get; private set; }

    public Guid ProcessingRunId { get; private set; }

    public ScopeType ScopeType { get; private set; }

    public string ScopeId { get; private set; }

    public Guid? SegmentId { get; private set; }

    public QualityStatus Status { get; private set; }

    public string Code { get; private set; }

    public string Severity { get; private set; }

    public string Message { get; private set; }

    public string? DetailsJson { get; private set; }

    public Guid? ArtifactId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    private QualityResult()
    {
        ScopeId = string.Empty;
        Code = string.Empty;
        Severity = string.Empty;
        Message = string.Empty;
    }

    public QualityResult(
        Guid id,
        Guid tenantId,
        Guid projectId,
        Guid processingRunId,
        ScopeType scopeType,
        string scopeId,
        Guid? segmentId,
        QualityStatus status,
        string code,
        string severity,
        string message,
        string? detailsJson,
        Guid? artifactId,
        DateTimeOffset createdAt)
    {
        Id = id;
        TenantId = tenantId;
        ProjectId = projectId;
        ProcessingRunId = processingRunId;
        ScopeType = scopeType;
        ScopeId = scopeId;
        SegmentId = segmentId;
        Status = status;
        Code = code;
        Severity = severity;
        Message = message;
        DetailsJson = detailsJson;
        ArtifactId = artifactId;
        CreatedAt = createdAt;

        Validate();
    }

    public void Validate()
    {
        if (Id == Guid.Empty)
        {
            throw new DomainException("QualityResult Id must not be empty.");
        }

        if (TenantId == Guid.Empty)
        {
            throw new DomainException("QualityResult TenantId must not be empty.");
        }

        if (ProjectId == Guid.Empty)
        {
            throw new DomainException("QualityResult ProjectId must not be empty.");
        }

        if (ProcessingRunId == Guid.Empty)
        {
            throw new DomainException("QualityResult ProcessingRunId must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(ScopeId))
        {
            throw new DomainException("QualityResult ScopeId must not be empty.");
        }

        if (SegmentId.HasValue && SegmentId.Value == Guid.Empty)
        {
            throw new DomainException("QualityResult SegmentId must not be empty when set.");
        }

        if (string.IsNullOrWhiteSpace(Code))
        {
            throw new DomainException("QualityResult Code must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(Severity))
        {
            throw new DomainException("QualityResult Severity must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(Message))
        {
            throw new DomainException("QualityResult Message must not be empty.");
        }

        if (DetailsJson is not null && string.IsNullOrWhiteSpace(DetailsJson))
        {
            throw new DomainException("QualityResult DetailsJson must not be empty when set.");
        }

        if (ArtifactId.HasValue && ArtifactId.Value == Guid.Empty)
        {
            throw new DomainException("QualityResult ArtifactId must not be empty when set.");
        }
    }
}
