using DubbingPlatform.Domain.Enums;
using DubbingPlatform.Domain.Exceptions;

namespace DubbingPlatform.Domain.Entities;

public sealed class ReviewDecision
{
    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid ReviewItemId { get; private set; }

    public ReviewDecisionType Type { get; private set; }

    public string Reviewer { get; private set; }

    public string Reason { get; private set; }

    public string? MetadataJson { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    private ReviewDecision()
    {
        Reviewer = string.Empty;
        Reason = string.Empty;
    }

    public ReviewDecision(
        Guid id,
        Guid tenantId,
        Guid reviewItemId,
        ReviewDecisionType type,
        string reviewer,
        string reason,
        string? metadataJson,
        DateTimeOffset createdAt)
    {
        Id = id;
        TenantId = tenantId;
        ReviewItemId = reviewItemId;
        Type = type;
        Reviewer = reviewer;
        Reason = reason;
        MetadataJson = metadataJson;
        CreatedAt = createdAt;

        Validate();
    }

    public void Validate()
    {
        if (Id == Guid.Empty)
        {
            throw new DomainException("ReviewDecision Id must not be empty.");
        }

        if (TenantId == Guid.Empty)
        {
            throw new DomainException("ReviewDecision TenantId must not be empty.");
        }

        if (ReviewItemId == Guid.Empty)
        {
            throw new DomainException("ReviewDecision ReviewItemId must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(Reviewer))
        {
            throw new DomainException("ReviewDecision Reviewer must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(Reason))
        {
            throw new DomainException("ReviewDecision Reason must not be empty.");
        }

        if (MetadataJson is not null && string.IsNullOrWhiteSpace(MetadataJson))
        {
            throw new DomainException("ReviewDecision MetadataJson must not be empty when set.");
        }
    }
}
