namespace DubbingPlatform.Application.Abstractions;

/// <summary>
/// S3 multipart session client. Storage keys are tenant-prefixed upload keys
/// (<c>{tenant:N}/{project:N}/{upload:N}/{fileName}</c>); implementations must
/// reject traversal keys. Presigned part URLs default to 15 minutes. The
/// authoritative part listing comes from <see cref="ListPartsAsync"/>; the
/// database reconciles from it and never invents parts.
/// </summary>
public interface IMultipartUploadClient
{
    Task<string> CreateMultipartUploadAsync(string storageKey, string contentType, CancellationToken ct);

    Task<string> GetPresignedPartUrlAsync(string storageKey, string multipartUploadId, int partNumber, TimeSpan expiry, CancellationToken ct);

    Task<IReadOnlyList<MultipartPart>> ListPartsAsync(string storageKey, string multipartUploadId, CancellationToken ct);

    Task CompleteMultipartUploadAsync(string storageKey, string multipartUploadId, IReadOnlyList<MultipartPart> parts, CancellationToken ct);

    Task AbortMultipartUploadAsync(string storageKey, string multipartUploadId, CancellationToken ct);
}

/// <summary>
/// A single uploaded part as reported by the object store.
/// </summary>
public sealed record MultipartPart(int PartNumber, string ETag, long SizeBytes);
