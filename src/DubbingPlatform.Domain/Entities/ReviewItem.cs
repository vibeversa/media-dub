using DubbingPlatform.Domain.Enums;
using DubbingPlatform.Domain.Exceptions;

namespace DubbingPlatform.Domain.Entities;

public sealed class ReviewItem
{
    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid ProjectId { get; private set; }

    public Guid ProcessingRunId { get; private set; }

    public ScopeType ScopeType { get; private set; }

    public string ScopeId { get; private set; }

    public Guid? SegmentId { get; private set; }

    public ReviewStatus Status { get; private set; }

    public string Reason { get; private set; }

    public string? PayloadJson { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public DateTimeOffset? ResolvedAt { get; private set; }

    private ReviewItem()
    {
        ScopeId = string.Empty;
        Reason = string.Empty;
    }

    public ReviewItem(
        Guid id,
        Guid tenantId,
        Guid projectId,
        Guid processingRunId,
        ScopeType scopeType,
        string scopeId,
        Guid? segmentId,
        ReviewStatus status,
        string reason,
        string? payloadJson,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt,
        DateTimeOffset? resolvedAt)
    {
        Id = id;
        TenantId = tenantId;
        ProjectId = projectId;
        ProcessingRunId = processingRunId;
        ScopeType = scopeType;
        ScopeId = scopeId;
        SegmentId = segmentId;
        Status = status;
        Reason = reason;
        PayloadJson = payloadJson;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
        ResolvedAt = resolvedAt;

        Validate();
    }

    public void Validate()
    {
        if (Id == Guid.Empty)
        {
            throw new DomainException("ReviewItem Id must not be empty.");
        }

        if (TenantId == Guid.Empty)
        {
            throw new DomainException("ReviewItem TenantId must not be empty.");
        }

        if (ProjectId == Guid.Empty)
        {
            throw new DomainException("ReviewItem ProjectId must not be empty.");
        }

        if (ProcessingRunId == Guid.Empty)
        {
            throw new DomainException("ReviewItem ProcessingRunId must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(ScopeId))
        {
            throw new DomainException("ReviewItem ScopeId must not be empty.");
        }

        if (SegmentId.HasValue && SegmentId.Value == Guid.Empty)
        {
            throw new DomainException("ReviewItem SegmentId must not be empty when set.");
        }

        if (string.IsNullOrWhiteSpace(Reason))
        {
            throw new DomainException("ReviewItem Reason must not be empty.");
        }

        if (PayloadJson is not null && string.IsNullOrWhiteSpace(PayloadJson))
        {
            throw new DomainException("ReviewItem PayloadJson must not be empty when set.");
        }

        if (ResolvedAt.HasValue && ResolvedAt.Value < CreatedAt)
        {
            throw new DomainException("ReviewItem ResolvedAt must not be before CreatedAt.");
        }
    }
}
