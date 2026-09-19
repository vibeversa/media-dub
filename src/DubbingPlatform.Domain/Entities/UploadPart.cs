using DubbingPlatform.Domain.Exceptions;

namespace DubbingPlatform.Domain.Entities;

public sealed class UploadPart
{
    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid UploadSessionId { get; private set; }

    public int PartNumber { get; private set; }

    public string ETag { get; private set; }

    public long SizeBytes { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    private UploadPart()
    {
        ETag = string.Empty;
    }

    public UploadPart(
        Guid id,
        Guid tenantId,
        Guid uploadSessionId,
        int partNumber,
        string eTag,
        long sizeBytes,
        DateTimeOffset createdAt)
    {
        Id = id;
        TenantId = tenantId;
        UploadSessionId = uploadSessionId;
        PartNumber = partNumber;
        ETag = eTag;
        SizeBytes = sizeBytes;
        CreatedAt = createdAt;

        Validate();
    }

    public void Validate()
    {
        if (Id == Guid.Empty)
        {
            throw new DomainException("UploadPart Id must not be empty.");
        }

        if (TenantId == Guid.Empty)
        {
            throw new DomainException("UploadPart TenantId must not be empty.");
        }

        if (UploadSessionId == Guid.Empty)
        {
            throw new DomainException("UploadPart UploadSessionId must not be empty.");
        }

        if (PartNumber < 1 || PartNumber > 10000)
        {
            throw new DomainException("UploadPart PartNumber must be in 1..10000.");
        }

        if (string.IsNullOrWhiteSpace(ETag))
        {
            throw new DomainException("UploadPart ETag must not be empty.");
        }

        if (SizeBytes < 0)
        {
            throw new DomainException("UploadPart SizeBytes must be >= 0.");
        }
    }
}
