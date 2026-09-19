using DubbingPlatform.Application.Abstractions;
using DubbingPlatform.Application.Errors;
using DubbingPlatform.Application.Exceptions;
using DubbingPlatform.Domain.Exceptions;

namespace DubbingPlatform.Infrastructure.Media;

/// <summary>
/// Disk-space guard over <c>DriveInfo.AvailableFreeSpace</c>. Throws
/// <c>RESOURCE_EXHAUSTED</c> (fail fast; the worker records <c>Failed</c> and
/// no partial artifact is committed) when free space is below the required
/// bytes. Volume I/O failures propagate as <c>IOException</c> (transient).
/// </summary>
public sealed class DiskSpaceChecker : IDiskSpaceChecker
{
    public void EnsureFree(string path, long requiredBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (requiredBytes < 0)
        {
            throw new DomainException("RequiredBytes must be >= 0.");
        }

        var free = GetFreeBytes(path);
        if (free < requiredBytes)
        {
            throw new ErrorCodeException(
                ErrorCodes.ResourceExhausted,
                $"Disk free space ({free} bytes) is below required ({requiredBytes} bytes); failing fast with no partial artifact.");
        }
    }

    public long GetFreeBytes(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex)
        {
            throw new DomainException("Path is invalid.", ex);
        }

        var root = Path.GetPathRoot(full);
        if (string.IsNullOrEmpty(root))
        {
            throw new DomainException("Path has no volume root.");
        }

        return new DriveInfo(root).AvailableFreeSpace;
    }
}
