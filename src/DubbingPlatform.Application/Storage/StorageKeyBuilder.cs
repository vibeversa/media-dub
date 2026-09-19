using DubbingPlatform.Domain.Exceptions;

namespace DubbingPlatform.Application.Storage;

/// <summary>
/// Storage key conventions and presigned-URL defaults.
/// Key format: <c>{tenant:N}/{project:N}/{run:N}/{stage}/{artifactType}/{hash}{extension}</c>
/// where all Guids use <c>N</c> format (32 lowercase hex, no dashes).
/// Stage and artifact type keep their enum names; only Guids are normalized.
/// </summary>
public static class StorageKeyBuilder
{
    /// <summary>
    /// Builds a tenant-prefixed storage key exactly per convention.
    /// </summary>
    public static string BuildKey(
        Guid tenantId,
        Guid projectId,
        Guid processingRunId,
        string stageType,
        string artifactType,
        string contentHash,
        string? extension)
    {
        if (tenantId == Guid.Empty)
        {
            throw new DomainException("TenantId must not be empty.");
        }

        if (projectId == Guid.Empty)
        {
            throw new DomainException("ProjectId must not be empty.");
        }

        if (processingRunId == Guid.Empty)
        {
            throw new DomainException("ProcessingRunId must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(stageType))
        {
            throw new DomainException("StageType must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(artifactType))
        {
            throw new DomainException("ArtifactType must not be empty.");
        }

        if (!IsLowerHex64(contentHash))
        {
            throw new DomainException("ContentHash must be 64 lowercase hex chars.");
        }

        var normalizedExtension = NormalizeExtension(extension);

        var stage = stageType.Trim();
        var artifact = artifactType.Trim();
        if (stage.Contains('/', StringComparison.Ordinal) || artifact.Contains('/', StringComparison.Ordinal))
        {
            throw new DomainException("StageType and ArtifactType must not contain '/'.");
        }

        return string.Concat(
            tenantId.ToString("N"), "/",
            projectId.ToString("N"), "/",
            processingRunId.ToString("N"), "/",
            stage, "/",
            artifact, "/",
            contentHash,
            normalizedExtension);
    }

    /// <summary>
    /// Validates a storage key: tenant-prefixed, no traversal, no absolute path.
    /// </summary>
    public static void ValidateKey(string storageKey)
    {
        if (string.IsNullOrWhiteSpace(storageKey))
        {
            throw new DomainException("StorageKey must not be empty.");
        }

        if (storageKey.StartsWith("/", StringComparison.Ordinal)
            || storageKey.Contains('\\')
            || storageKey.Contains("..", StringComparison.Ordinal))
        {
            throw new DomainException("StorageKey must not contain traversal or absolute paths.");
        }

        if (!storageKey.Contains('/', StringComparison.Ordinal))
        {
            throw new DomainException("StorageKey must be tenant-prefixed ('{tenant}/...').");
        }

        if (storageKey.Length > 1024)
        {
            throw new DomainException("StorageKey must be at most 1024 chars.");
        }
    }

    /// <summary>
    /// Normalizes an extension (empty or dot-prefixed lowercase alnum).
    /// </summary>
    public static string NormalizeExtension(string? extension)
    {
        if (string.IsNullOrEmpty(extension))
        {
            return string.Empty;
        }

        if (!extension.StartsWith(".", StringComparison.Ordinal))
        {
            throw new DomainException("Extension must start with '.' when set.");
        }

        if (extension.Length > 16)
        {
            throw new DomainException("Extension must be at most 16 chars.");
        }

        for (var i = 1; i < extension.Length; i++)
        {
            var c = extension[i];
            var ok = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9');
            if (!ok)
            {
                throw new DomainException("Extension must contain only lowercase letters and digits after '.'.");
            }
        }

        return extension;
    }

    /// <summary>
    /// Whether the value is 64 lowercase hex chars (SHA-256).
    /// </summary>
    public static bool IsLowerHex64(string? value)
    {
        if (value is null || value.Length != 64)
        {
            return false;
        }

        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            var isDigit = c >= '0' && c <= '9';
            var isHex = c >= 'a' && c <= 'f';
            if (!isDigit && !isHex)
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// Presigned-URL defaults. URLs expire after 15 minutes and require
/// authentication plus ownership checks before issuance (see ArtifactService).
/// </summary>
public static class StoragePresignedUrls
{
    /// <summary>
    /// Default presigned URL lifetime (15 minutes).
    /// </summary>
    public static readonly TimeSpan DefaultExpiry = TimeSpan.FromMinutes(15);
}
