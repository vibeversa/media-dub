using DubbingPlatform.Domain.Exceptions;

namespace DubbingPlatform.Domain.Entities;

public sealed class QuotaUsage
{
    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public string Dimension { get; private set; }

    public long Used { get; private set; }

    public long Limit { get; private set; }

    public DateTimeOffset WindowStart { get; private set; }

    public DateTimeOffset WindowEnd { get; private set; }

    private QuotaUsage()
    {
        Dimension = string.Empty;
    }

    public QuotaUsage(
        Guid id,
        Guid tenantId,
        string dimension,
        long used,
        long limit,
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd)
    {
        Id = id;
        TenantId = tenantId;
        Dimension = dimension;
        Used = used;
        Limit = limit;
        WindowStart = windowStart;
        WindowEnd = windowEnd;

        Validate();
    }

    public void Validate()
    {
        if (Id == Guid.Empty)
        {
            throw new DomainException("QuotaUsage Id must not be empty.");
        }

        if (TenantId == Guid.Empty)
        {
            throw new DomainException("QuotaUsage TenantId must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(Dimension))
        {
            throw new DomainException("QuotaUsage Dimension must not be empty.");
        }

        if (Used < 0)
        {
            throw new DomainException("QuotaUsage Used must be >= 0.");
        }

        if (Limit < 0)
        {
            throw new DomainException("QuotaUsage Limit must be >= 0.");
        }

        if (WindowEnd < WindowStart)
        {
            throw new DomainException("QuotaUsage WindowEnd must not be before WindowStart.");
        }
    }
}
