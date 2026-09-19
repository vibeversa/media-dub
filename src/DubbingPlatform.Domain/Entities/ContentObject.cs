using DubbingPlatform.Domain.Enums;
using DubbingPlatform.Domain.Exceptions;

namespace DubbingPlatform.Domain.Entities;

public sealed class ContentObject
{
    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public string ContentHash { get; private set; }

    public string Sha256Hex { get; private set; }

    public long SizeBytes { get; private set; }

    public string MediaFormat { get; private set; }

    public string StorageKey { get; private set; }

    public ContentObjectStatus Status { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset LastReferencedAt { get; private set; }

    private ContentObject()
    {
        ContentHash = string.Empty;
        Sha256Hex = string.Empty;
        MediaFormat = string.Empty;
        StorageKey = string.Empty;
    }

    public ContentObject(
        Guid id,
        Guid tenantId,
        string contentHash,
        string sha256Hex,
        long sizeBytes,
        string mediaFormat,
        string storageKey,
        ContentObjectStatus status,
        DateTimeOffset createdAt,
        DateTimeOffset lastReferencedAt)
    {
        Id = id;
        TenantId = tenantId;
        ContentHash = contentHash;
        Sha256Hex = sha256Hex;
        SizeBytes = sizeBytes;
        MediaFormat = mediaFormat;
        StorageKey = storageKey;
        Status = status;
        CreatedAt = createdAt;
        LastReferencedAt = lastReferencedAt;

        Validate();
    }

    public void Validate()
    {
        if (Id == Guid.Empty)
        {
            throw new DomainException("ContentObject Id must not be empty.");
        }

        if (TenantId == Guid.Empty)
        {
            throw new DomainException("ContentObject TenantId must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(ContentHash))
        {
            throw new DomainException("ContentObject ContentHash must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(Sha256Hex))
        {
            throw new DomainException("ContentObject Sha256Hex must not be empty.");
        }

        if (SizeBytes < 0)
        {
            throw new DomainException("ContentObject SizeBytes must be >= 0.");
        }

        if (string.IsNullOrWhiteSpace(MediaFormat))
        {
            throw new DomainException("ContentObject MediaFormat must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(StorageKey))
        {
            throw new DomainException("ContentObject StorageKey must not be empty.");
        }
    }
}
