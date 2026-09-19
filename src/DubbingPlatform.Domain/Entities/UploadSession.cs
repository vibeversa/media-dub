using DubbingPlatform.Domain.Enums;
using DubbingPlatform.Domain.Exceptions;

namespace DubbingPlatform.Domain.Entities;

public sealed class UploadSession
{
    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid ProjectId { get; private set; }

    public string FileName { get; private set; }

    public string DeclaredContentType { get; private set; }

    public long DeclaredSizeBytes { get; private set; }

    public long MaxPartBytes { get; private set; }

    public string StorageKey { get; private set; }

    public string? MultipartUploadId { get; private set; }

    public UploadStatus Status { get; private set; }

    public string? ClientSha256Hex { get; private set; }

    public string? ContentHash { get; private set; }

    public int PartCount { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    private UploadSession()
    {
        FileName = string.Empty;
        DeclaredContentType = string.Empty;
        StorageKey = string.Empty;
    }

    public UploadSession(
        Guid id,
        Guid tenantId,
        Guid projectId,
        string fileName,
        string declaredContentType,
        long declaredSizeBytes,
        long maxPartBytes,
        string storageKey,
        string? multipartUploadId,
        UploadStatus status,
        string? clientSha256Hex,
        string? contentHash,
        int partCount,
        DateTimeOffset createdAt,
        DateTimeOffset expiresAt)
    {
        Id = id;
        TenantId = tenantId;
        ProjectId = projectId;
        FileName = fileName;
        DeclaredContentType = declaredContentType;
        DeclaredSizeBytes = declaredSizeBytes;
        MaxPartBytes = maxPartBytes;
        StorageKey = storageKey;
        MultipartUploadId = multipartUploadId;
        Status = status;
        ClientSha256Hex = clientSha256Hex;
        ContentHash = contentHash;
        PartCount = partCount;
        CreatedAt = createdAt;
        ExpiresAt = expiresAt;

        Validate();
    }

    public void Validate()
    {
        if (Id == Guid.Empty)
        {
            throw new DomainException("UploadSession Id must not be empty.");
        }

        if (TenantId == Guid.Empty)
        {
            throw new DomainException("UploadSession TenantId must not be empty.");
        }

        if (ProjectId == Guid.Empty)
        {
            throw new DomainException("UploadSession ProjectId must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(FileName))
        {
            throw new DomainException("UploadSession FileName must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(DeclaredContentType))
        {
            throw new DomainException("UploadSession DeclaredContentType must not be empty.");
        }

        if (DeclaredSizeBytes < 0)
        {
            throw new DomainException("UploadSession DeclaredSizeBytes must be >= 0.");
        }

        if (MaxPartBytes <= 0)
        {
            throw new DomainException("UploadSession MaxPartBytes must be > 0.");
        }

        if (string.IsNullOrWhiteSpace(StorageKey))
        {
            throw new DomainException("UploadSession StorageKey must not be empty.");
        }

        if (PartCount < 0)
        {
            throw new DomainException("UploadSession PartCount must be >= 0.");
        }

        if (ExpiresAt < CreatedAt)
        {
            throw new DomainException("UploadSession ExpiresAt must not be before CreatedAt.");
        }
    }
}
