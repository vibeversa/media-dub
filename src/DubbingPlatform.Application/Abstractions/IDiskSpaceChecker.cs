namespace DubbingPlatform.Application.Abstractions;

/// <summary>
/// Disk-space guard for media work. Implementations throw
/// <c>ErrorCodeException</c> with <c>RESOURCE_EXHAUSTED</c> (fail fast, no
/// partial artifact) when free space is below <paramref name="requiredBytes"/>.
/// </summary>
public interface IDiskSpaceChecker
{
    /// <summary>
    /// Throws <c>RESOURCE_EXHAUSTED</c> unless <paramref name="path"/>'s volume
    /// has at least <paramref name="requiredBytes"/> free.
    /// </summary>
    void EnsureFree(string path, long requiredBytes);

    /// <summary>
    /// Returns free bytes on <paramref name="path"/>'s volume.
    /// </summary>
    long GetFreeBytes(string path);
}
