namespace DubbingPlatform.Infrastructure.Storage;

/// <summary>
/// One listed storage object.
/// </summary>
public sealed record StorageObjectInfo(string Key, DateTimeOffset LastModified, long Size);

/// <summary>
/// Inventory listing for reconciliation. Kept separate from
/// <see cref="Application.Abstractions.IArtifactStorage"/> so that interface
/// stays exactly as specified while reconcilers can enumerate keys.
/// </summary>
public interface IStorageInventory
{
    Task<IReadOnlyList<StorageObjectInfo>> ListObjectsAsync(string prefix, CancellationToken cancellationToken = default);

    Task QuarantineAsync(string storageKey, CancellationToken cancellationToken = default);
}
