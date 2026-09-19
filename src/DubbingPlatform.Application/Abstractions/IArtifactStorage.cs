namespace DubbingPlatform.Application.Abstractions;

/// <summary>
/// Tenant-scoped immutable blob storage. Keys are always built via
/// <see cref="Storage.StorageKeyBuilder"/> and start with the tenant id;
/// implementations must reject keys with traversal (<c>..</c>) or absolute
/// paths. All operations stream; implementations never buffer whole blobs in
/// memory. Presigned URLs default to 15 minutes (see
/// <see cref="Storage.StoragePresignedUrls"/>).
/// </summary>
public interface IArtifactStorage
{
    Task UploadAsync(Stream content, string storageKey, string contentType, CancellationToken ct);

    Task<Stream> DownloadAsync(string storageKey, CancellationToken ct);

    Task<bool> ExistsAsync(string storageKey, CancellationToken ct);

    Task DeleteAsync(string storageKey, CancellationToken ct);

    Task<string> GetPresignedDownloadUrlAsync(string storageKey, TimeSpan expiry, CancellationToken ct);

    Task<string> GetPresignedUploadUrlAsync(string storageKey, TimeSpan expiry, CancellationToken ct);

    Task<string?> GetStorageChecksumAsync(string storageKey, CancellationToken ct);
}
