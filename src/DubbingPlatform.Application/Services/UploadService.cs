using DubbingPlatform.Application.Abstractions;
using DubbingPlatform.Application.Errors;
using DubbingPlatform.Application.Exceptions;
using DubbingPlatform.Application.MultiTenancy;
using DubbingPlatform.Application.Options;
using DubbingPlatform.Application.StateMachines;
using DubbingPlatform.Application.Storage;
using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Domain.Enums;
using DubbingPlatform.Domain.Exceptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DubbingPlatform.Application.Services;

/// <summary>
/// Upload-session result.
/// </summary>
public sealed record UploadSessionResult(
    Guid UploadId,
    string MultipartUploadId,
    long PartSizeBytes,
    string StorageKey,
    string Status,
    DateTimeOffset ExpiresAt);

/// <summary>
/// Upload-status result (S3-authoritative parts plus DB reconcile).
/// </summary>
public sealed record UploadStatusResult(
    Guid UploadId,
    string Status,
    IReadOnlyList<int> CompletedParts,
    IReadOnlyList<int> MissingParts,
    long PartSizeBytes,
    long DeclaredSizeBytes,
    DateTimeOffset ExpiresAt);

/// <summary>
/// Completed-upload result for <c>MediaUploaded</c> publishing by callers.
/// </summary>
public sealed record UploadCompleteResult(
    Guid UploadId,
    string StorageKey,
    string Status,
    IReadOnlyList<int> CompletedParts);

/// <summary>
/// Multipart upload-session workflow. Sessions validate filename (no path
/// traversal, max 256 chars), content-type allowlist, and declared size
/// (<c>1..MediaOptions.MaxUploadBytes</c>, else 400 VALIDATION_FAILED;
/// quota exhaustion maps to 429 QUOTA_EXCEEDED via the stub-allow check).
/// Storage keys are <c>{tenant:N}/{project:N}/{upload:N}/{fileName}</c>.
/// Part URLs are presigned PUTs with 15-minute expiry. <c>GetStatusAsync</c>
/// lists S3 parts (authoritative) and reconciles <c>UploadParts</c> rows;
/// when S3 is unreachable it falls back to the DB ledger so reads stay
/// available. <c>CompleteAsync</c> requires all expected parts
/// (<c>ceil(declaredSize/partSize)</c>) else 400 UPLOAD_INCOMPLETE, completes
/// the multipart upload, marks <c>Completed</c>, and returns the storage key
/// for the controller to publish <c>MediaUploaded</c>. <c>AbortAsync</c>
/// aborts the multipart upload (best effort) and marks <c>Aborted</c>.
/// </summary>
public sealed class UploadService
{
    public static readonly long PartSizeBytes = 8L * 1024L * 1024L;

    public static readonly TimeSpan PartUrlExpiry = TimeSpan.FromMinutes(15);

    public static readonly TimeSpan SessionLifetime = TimeSpan.FromDays(7);

    private static readonly HashSet<string> AllowedContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "video/mp4",
        "video/quicktime",
        "video/x-matroska",
        "audio/wav",
        "audio/mpeg",
        "audio/flac",
        "audio/x-wav",
    };

    private readonly IStageExecutionContextFactory _contextFactory;
    private readonly IMultipartUploadClient _multipart;
    private readonly AuditService _audit;
    private readonly MediaOptions _media;
    private readonly QuotaOptions _quota;

    public UploadService(
        IStageExecutionContextFactory contextFactory,
        IMultipartUploadClient multipart,
        AuditService audit,
        IOptions<MediaOptions> media,
        IOptions<QuotaOptions> quota)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        ArgumentNullException.ThrowIfNull(multipart);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(media);
        ArgumentNullException.ThrowIfNull(quota);
        _contextFactory = contextFactory;
        _multipart = multipart;
        _audit = audit;
        _media = media.Value;
        _quota = quota.Value;
    }

    /// <summary>
    /// Creates an upload session (project must exist and belong to the tenant).
    /// </summary>
    public async Task<UploadSessionResult> CreateSessionAsync(
        Guid tenantId,
        Guid projectId,
        string fileName,
        string contentType,
        long declaredSizeBytes,
        string actor,
        CancellationToken cancellationToken = default)
    {
        RequireTenant(tenantId);
        RequireId(projectId, nameof(projectId));
        var safeName = ValidateFileName(fileName);
        var safeContentType = ValidateContentType(contentType);
        ValidateDeclaredSize(declaredSizeBytes);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);

        await RequireProjectAsync(tenantId, projectId, cancellationToken).ConfigureAwait(false);

        var now = DateTimeOffset.UtcNow;
        var uploadId = Guid.NewGuid();
        var storageKey = string.Concat(
            tenantId.ToString("N"), "/",
            projectId.ToString("N"), "/",
            uploadId.ToString("N"), "/",
            safeName);
        StorageKeyBuilder.ValidateKey(storageKey);

        string multipartUploadId;
        try
        {
            multipartUploadId = await _multipart.CreateMultipartUploadAsync(
                storageKey, safeContentType, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!IsDomainOrApp(ex))
        {
            throw new ErrorCodeException(ErrorCodes.StorageUnavailable, "Storage is temporarily unavailable; retry the operation.", ex);
        }

        if (string.IsNullOrWhiteSpace(multipartUploadId))
        {
            throw new ErrorCodeException(ErrorCodes.StorageUnavailable, "Storage did not return a multipart upload id.");
        }

        var expiresAt = now.Add(SessionLifetime);
        var session = new UploadSession(
            uploadId, tenantId, projectId, safeName, safeContentType,
            declaredSizeBytes, PartSizeBytes, storageKey, multipartUploadId.Trim(),
            UploadStatus.InProgress, null, null, 0, now, expiresAt);

        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            db.Set<UploadSession>().Add(session);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        await _audit.LogAsync(
            tenantId, projectId, actor.Trim(), "upload.create",
            "upload", uploadId.ToString("N"), null, cancellationToken).ConfigureAwait(false);

        return new UploadSessionResult(uploadId, multipartUploadId.Trim(), PartSizeBytes, storageKey, UploadStatus.InProgress.ToString(), expiresAt);
    }

    /// <summary>
    /// Issues presigned part-upload URLs (15 minutes each).
    /// </summary>
    public async Task<IReadOnlyDictionary<int, string>> GetPartUrlsAsync(
        Guid tenantId,
        Guid projectId,
        Guid uploadId,
        IReadOnlyList<int> partNumbers,
        CancellationToken cancellationToken = default)
    {
        RequireTenant(tenantId);
        RequireId(projectId, nameof(projectId));
        RequireId(uploadId, nameof(uploadId));
        ArgumentNullException.ThrowIfNull(partNumbers);
        if (partNumbers.Count == 0)
        {
            throw new DomainException("PartNumbers must contain at least one entry.");
        }

        if (partNumbers.Count > 1000)
        {
            throw new DomainException("PartNumbers must contain at most 1000 entries.");
        }

        var distinct = new SortedSet<int>();
        foreach (var part in partNumbers)
        {
            if (part is < 1 or > 10000)
            {
                throw new DomainException("PartNumber must be in 1..10000.");
            }

            if (!distinct.Add(part))
            {
                throw new DomainException("PartNumbers must be distinct.");
            }
        }

        var session = await LoadOwnedSessionAsync(tenantId, projectId, uploadId, cancellationToken).ConfigureAwait(false);
        EnsureActive(session);
        if (string.IsNullOrWhiteSpace(session.MultipartUploadId))
        {
            throw new ErrorCodeException(ErrorCodes.StorageUnavailable, "Upload session has no multipart upload id.");
        }

        var urls = new Dictionary<int, string>();
        foreach (var part in distinct)
        {
            string url;
            try
            {
                url = await _multipart.GetPresignedPartUrlAsync(
                    session.StorageKey, session.MultipartUploadId!, part, PartUrlExpiry, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (!IsDomainOrApp(ex))
            {
                throw new ErrorCodeException(ErrorCodes.StorageUnavailable, "Storage is temporarily unavailable; retry the operation.", ex);
            }

            urls[part] = url;
        }

        return urls;
    }

    /// <summary>
    /// Gets session status with S3-authoritative parts plus DB reconcile.
    /// </summary>
    public async Task<UploadStatusResult> GetStatusAsync(
        Guid tenantId,
        Guid projectId,
        Guid uploadId,
        CancellationToken cancellationToken = default)
    {
        RequireTenant(tenantId);
        RequireId(projectId, nameof(projectId));
        RequireId(uploadId, nameof(uploadId));

        var session = await LoadOwnedSessionAsync(tenantId, projectId, uploadId, cancellationToken).ConfigureAwait(false);
        var completed = await ListAuthoritativePartsAsync(session, cancellationToken).ConfigureAwait(false);
        await ReconcilePartsAsync(tenantId, session, completed, cancellationToken).ConfigureAwait(false);

        var expected = ExpectedPartCount(session.DeclaredSizeBytes);
        var completedSet = new HashSet<int>(completed.Select(p => p.PartNumber));
        var missing = new List<int>();
        for (var i = 1; i <= expected; i++)
        {
            if (!completedSet.Contains(i))
            {
                missing.Add(i);
            }
        }

        var ordered = completedSet.OrderBy(p => p).ToList();
        return new UploadStatusResult(
            session.Id, session.Status.ToString(), ordered, missing,
            session.MaxPartBytes, session.DeclaredSizeBytes, session.ExpiresAt);
    }

    /// <summary>
    /// Completes a session. Requires all expected parts, else 400 UPLOAD_INCOMPLETE.
    /// </summary>
    public async Task<UploadCompleteResult> CompleteAsync(
        Guid tenantId,
        Guid projectId,
        Guid uploadId,
        string actor,
        CancellationToken cancellationToken = default)
    {
        RequireTenant(tenantId);
        RequireId(projectId, nameof(projectId));
        RequireId(uploadId, nameof(uploadId));
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);

        var session = await LoadOwnedSessionAsync(tenantId, projectId, uploadId, cancellationToken).ConfigureAwait(false);
        EnsureActive(session);

        var parts = await ListAuthoritativePartsAsync(session, cancellationToken).ConfigureAwait(false);
        var expected = ExpectedPartCount(session.DeclaredSizeBytes);
        var numbers = new HashSet<int>(parts.Select(p => p.PartNumber));
        if (parts.Count == 0 || numbers.Count < expected)
        {
            throw new ErrorCodeException(ErrorCodes.UploadIncomplete, $"Upload is incomplete: {numbers.Count}/{expected} parts present.");
        }

        for (var i = 1; i <= expected; i++)
        {
            if (!numbers.Contains(i))
            {
                throw new ErrorCodeException(ErrorCodes.UploadIncomplete, $"Upload is incomplete: missing part {i}/{expected}.");
            }
        }

        var ordered = parts.OrderBy(p => p.PartNumber).ToList();
        try
        {
            await _multipart.CompleteMultipartUploadAsync(
                session.StorageKey, session.MultipartUploadId!, ordered, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!IsDomainOrApp(ex))
        {
            throw new ErrorCodeException(ErrorCodes.StorageUnavailable, "Storage is temporarily unavailable; retry the operation.", ex);
        }

        await ReconcilePartsAsync(tenantId, session, ordered, cancellationToken).ConfigureAwait(false);
        await TransitionAsync(tenantId, session.Id, UploadStatus.Completed, ordered.Count, cancellationToken).ConfigureAwait(false);

        await _audit.LogAsync(
            tenantId, projectId, actor.Trim(), "upload.complete",
            "upload", uploadId.ToString("N"), null, cancellationToken).ConfigureAwait(false);

        var completedNumbers = ordered.Select(p => p.PartNumber).OrderBy(p => p).ToList();
        return new UploadCompleteResult(session.Id, session.StorageKey, UploadStatus.Completed.ToString(), completedNumbers);
    }

    /// <summary>
    /// Aborts a session (multipart abort is best effort).
    /// </summary>
    public async Task AbortAsync(
        Guid tenantId,
        Guid projectId,
        Guid uploadId,
        string actor,
        CancellationToken cancellationToken = default)
    {
        RequireTenant(tenantId);
        RequireId(projectId, nameof(projectId));
        RequireId(uploadId, nameof(uploadId));
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);

        var session = await LoadOwnedSessionAsync(tenantId, projectId, uploadId, cancellationToken).ConfigureAwait(false);
        EnsureActive(session);

        if (!string.IsNullOrWhiteSpace(session.MultipartUploadId))
        {
            try
            {
                await _multipart.AbortMultipartUploadAsync(
                    session.StorageKey, session.MultipartUploadId!, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Best effort: the DB transition still records the abort.
            }
        }

        await TransitionAsync(tenantId, session.Id, UploadStatus.Aborted, session.PartCount, cancellationToken).ConfigureAwait(false);

        await _audit.LogAsync(
            tenantId, projectId, actor.Trim(), "upload.abort",
            "upload", uploadId.ToString("N"), null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Lists sessions for a project, newest last.
    /// </summary>
    public async Task<(IReadOnlyList<UploadSession> Items, long Total)> ListAsync(
        Guid tenantId,
        Guid projectId,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        RequireTenant(tenantId);
        RequireId(projectId, nameof(projectId));
        await RequireProjectAsync(tenantId, projectId, cancellationToken).ConfigureAwait(false);

        var normalizedPage = page < 1 ? 1 : page;
        var normalizedSize = pageSize < 1 ? 1 : pageSize > 100 ? 100 : pageSize;

        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            var query = db.Set<UploadSession>().AsNoTracking()
                .Where(s => s.ProjectId == projectId);
            var total = await query.LongCountAsync(cancellationToken).ConfigureAwait(false);
            var items = await query
                .OrderBy(s => s.CreatedAt)
                .Skip((normalizedPage - 1) * normalizedSize)
                .Take(normalizedSize)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            return (items, total);
        }
    }

    internal static string ValidateFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            throw new DomainException("FileName must not be empty.");
        }

        var trimmed = fileName.Trim();
        if (trimmed.Length > 256)
        {
            throw new DomainException("FileName must be at most 256 chars.");
        }

        if (trimmed.Contains('/', StringComparison.Ordinal)
            || trimmed.Contains('\\')
            || trimmed.Contains("..", StringComparison.Ordinal))
        {
            throw new DomainException("FileName must not contain path traversal.");
        }

        if (string.Equals(trimmed, ".", StringComparison.Ordinal)
            || string.Equals(trimmed, "..", StringComparison.Ordinal))
        {
            throw new DomainException("FileName must not be '.' or '..'.");
        }

        foreach (var c in trimmed)
        {
            if (char.IsControl(c))
            {
                throw new DomainException("FileName must not contain control characters.");
            }
        }

        return trimmed;
    }

    internal static string ValidateContentType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            throw new DomainException("ContentType must not be empty.");
        }

        var trimmed = contentType.Trim();
        if (!AllowedContentTypes.Contains(trimmed))
        {
            throw new DomainException($"ContentType '{trimmed}' is not supported.");
        }

        return trimmed;
    }

    internal static int ExpectedPartCount(long declaredSizeBytes)
    {
        return Math.Max(1, (int)((declaredSizeBytes + PartSizeBytes - 1) / PartSizeBytes));
    }

    private void ValidateDeclaredSize(long declaredSizeBytes)
    {
        if (declaredSizeBytes < 1)
        {
            throw new DomainException("DeclaredSize must be at least 1 byte.");
        }

        if (declaredSizeBytes > _media.MaxUploadBytes)
        {
            throw new DomainException($"DeclaredSize must not exceed {_media.MaxUploadBytes} bytes.");
        }

        if (declaredSizeBytes > _quota.MaxStorageBytes)
        {
            throw new QuotaExceededException("Storage quota would be exceeded by this upload.");
        }
    }

    private async Task<DubbingProject> RequireProjectAsync(Guid tenantId, Guid projectId, CancellationToken cancellationToken)
    {
        DubbingProject? project;
        bool isDeleted;
        using (TenantContext.BeginMaintenanceScope())
        {
            using var db = _contextFactory.CreateDbContext();
            project = await db.Set<DubbingProject>()
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken).ConfigureAwait(false);
            if (project is null)
            {
                throw new NotFoundException($"Project '{projectId}' was not found.");
            }

            isDeleted = await db.Set<DubbingProject>()
                .Where(p => p.Id == projectId)
                .Select(p => EF.Property<bool>(p, "IsDeleted"))
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        }

        if (project.TenantId != tenantId)
        {
            throw new ForbiddenException($"Project '{projectId}' does not belong to the current tenant.");
        }

        if (isDeleted)
        {
            throw new NotFoundException($"Project '{projectId}' was not found.");
        }

        return project;
    }

    private async Task<UploadSession> LoadOwnedSessionAsync(
        Guid tenantId,
        Guid projectId,
        Guid uploadId,
        CancellationToken cancellationToken)
    {
        await RequireProjectAsync(tenantId, projectId, cancellationToken).ConfigureAwait(false);

        UploadSession? session;
        using (TenantContext.BeginMaintenanceScope())
        {
            using var db = _contextFactory.CreateDbContext();
            session = await db.Set<UploadSession>()
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.Id == uploadId, cancellationToken).ConfigureAwait(false);
        }

        if (session is null)
        {
            throw new NotFoundException($"Upload '{uploadId}' was not found.");
        }

        if (session.TenantId != tenantId || session.ProjectId != projectId)
        {
            throw new ForbiddenException($"Upload '{uploadId}' does not belong to the current tenant/project.");
        }

        return session;
    }

    private static void EnsureActive(UploadSession session)
    {
        if (session.Status is UploadStatus.Completed or UploadStatus.Aborted or UploadStatus.Expired or UploadStatus.Duplicate)
        {
            throw new ConflictException($"Upload '{session.Id}' is already {session.Status}.");
        }
    }

    private async Task<IReadOnlyList<MultipartPart>> ListAuthoritativePartsAsync(
        UploadSession session,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(session.MultipartUploadId))
        {
            return [];
        }

        try
        {
            return await _multipart.ListPartsAsync(
                session.StorageKey, session.MultipartUploadId!, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            using (TenantContext.BeginScope(session.TenantId))
            {
                using var db = _contextFactory.CreateDbContext();
                return await db.Set<UploadPart>()
                    .AsNoTracking()
                    .Where(p => p.UploadSessionId == session.Id)
                    .OrderBy(p => p.PartNumber)
                    .Select(p => new MultipartPart(p.PartNumber, p.ETag, p.SizeBytes))
                    .ToListAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task ReconcilePartsAsync(
        Guid tenantId,
        UploadSession session,
        IReadOnlyList<MultipartPart> authoritative,
        CancellationToken cancellationToken)
    {
        if (authoritative.Count == 0)
        {
            return;
        }

        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            var existing = await db.Set<UploadPart>()
                .Where(p => p.UploadSessionId == session.Id)
                .Select(p => p.PartNumber)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            var known = new HashSet<int>(existing);
            var now = DateTimeOffset.UtcNow;
            foreach (var part in authoritative)
            {
                if (known.Contains(part.PartNumber))
                {
                    continue;
                }

                db.Set<UploadPart>().Add(new UploadPart(
                    Guid.NewGuid(), tenantId, session.Id,
                    part.PartNumber, part.ETag, part.SizeBytes, now));
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task TransitionAsync(
        Guid tenantId,
        Guid uploadId,
        UploadStatus target,
        int partCount,
        CancellationToken cancellationToken)
    {
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            var session = await db.Set<UploadSession>()
                .FirstOrDefaultAsync(s => s.Id == uploadId, cancellationToken).ConfigureAwait(false);
            if (session is null)
            {
                throw new NotFoundException($"Upload '{uploadId}' was not found.");
            }

            UploadStateMachine.EnsureCanTransition(session.Status, target);

            await db.Database.ExecuteSqlRawAsync(
                "UPDATE upload_sessions SET status = {0}, part_count = {1} WHERE id = {2} AND tenant_id = {3}",
                target.ToString(), partCount, uploadId, tenantId).ConfigureAwait(false);
        }
    }

    private static bool IsDomainOrApp(Exception exception)
    {
        return exception is DomainException || exception is AppException;
    }

    private static void RequireTenant(Guid tenantId)
    {
        if (tenantId == Guid.Empty)
        {
            throw new DomainException("TenantId must not be empty.");
        }
    }

    private static void RequireId(Guid id, string name)
    {
        if (id == Guid.Empty)
        {
            throw new DomainException($"{name} must not be empty.");
        }
    }
}
