using DubbingPlatform.Application.Abstractions;
using DubbingPlatform.Application.Errors;
using DubbingPlatform.Application.Exceptions;
using DubbingPlatform.Application.MultiTenancy;
using DubbingPlatform.Application.Services;
using DubbingPlatform.Application.Storage;
using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Domain.Enums;
using DubbingPlatform.Domain.Exceptions;
using Microsoft.EntityFrameworkCore;

namespace DubbingPlatform.Application.Services;

/// <summary>
/// Result of an atomic artifact publication.
/// </summary>
public sealed record PublishResult(
    Guid ArtifactId,
    Guid ContentObjectId,
    string StorageKey,
    string ContentHash,
    bool ReusedContent,
    long SizeBytes);

/// <summary>
/// Immutable tenant-scoped publication workflow. Content bytes are hashed
/// streaming (memory capped at 16MB, larger payloads spill to <c>/tmp</c>),
/// deduplicated per tenant via the <c>(tenant_id, content_hash)</c> unique
/// index, and stored under
/// <c>{tenant:N}/{project:N}/{run:N}/{stage}/{artifactType}/{hash}{ext}</c>.
/// Reuse path creates a new committed logical row without uploading.
/// New-content path uploads first, then commits ContentObject + Artifact +
/// lineage + optional StageOutput in a single EF transaction; any failure
/// after upload leaves the blob orphan for the reconciler (except checksum
/// mismatch, which deletes the blob). Concurrent duplicates serialize on the
/// unique constraint: the loser deletes its duplicate blob (best effort) and
/// reuses the winner. Cross-tenant reuse never happens (all lookups scoped).
/// Callers publish <c>StageCompleted</c> after this returns, inside the same
/// MassTransit consumer context, so the stage event joins the EF outbox
/// atomically with the caller's commit.
/// </summary>
public sealed class ArtifactService
{
    private const long MaxMemoryBytes = 16L * 1024L * 1024L;

    private static readonly TimeSpan DefaultUrlExpiry = TimeSpan.FromMinutes(15);

    private readonly IStageExecutionContextFactory _contextFactory;
    private readonly IArtifactStorage _storage;
    private readonly IQuotaGate? _quotaGate;

    public ArtifactService(IStageExecutionContextFactory contextFactory, IArtifactStorage storage, IQuotaGate? quotaGate = null)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        ArgumentNullException.ThrowIfNull(storage);
        _contextFactory = contextFactory;
        _storage = storage;
        _quotaGate = quotaGate;
    }

    /// <summary>
    /// Publishes content atomically. See class docs for ordering and orphan semantics.
    /// </summary>
    public async Task<PublishResult> PublishAsync(
        Guid tenantId,
        Guid projectId,
        Guid processingRunId,
        StageType stageType,
        ArtifactType artifactType,
        Stream content,
        string? extension,
        string contentType,
        string? provider,
        string? model,
        string? configurationHash,
        string? executionSnapshotHash,
        IReadOnlyList<Guid> parentArtifactIds,
        Guid? stageExecutionId = null,
        CancellationToken cancellationToken = default)
    {
        RequireTenant(tenantId);
        RequireId(projectId, nameof(projectId));
        RequireId(processingRunId, nameof(processingRunId));
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(parentArtifactIds);
        if (string.IsNullOrWhiteSpace(contentType))
        {
            throw new DomainException("ContentType must not be empty.");
        }

        if (contentType.Length > 128)
        {
            throw new DomainException("ContentType must be at most 128 chars.");
        }

        ValidateOptional(provider, nameof(provider), 128);
        ValidateOptional(model, nameof(model), 128);
        ValidateOptional(configurationHash, nameof(configurationHash), 64);
        ValidateOptional(executionSnapshotHash, nameof(executionSnapshotHash), 64);
        foreach (var parentId in parentArtifactIds)
        {
            RequireId(parentId, "parentArtifactIds item");
        }

        if (stageExecutionId.HasValue && stageExecutionId.Value == Guid.Empty)
        {
            throw new DomainException("StageExecutionId must not be empty when set.");
        }

        if (!content.CanRead)
        {
            throw new DomainException("Content stream must be readable.");
        }

        // Fail fast on extension shape before any hashing, storage, or DB I/O.
        StorageKeyBuilder.NormalizeExtension(extension);

        // 1. Buffer + hash (single streaming pass, memory capped, spill to /tmp).
        var buffered = await BufferWithHashAsync(content, cancellationToken).ConfigureAwait(false);
        var tempPath = buffered.TempPath;
        var memoryBuffer = buffered.MemoryBuffer;
        try
        {
            var hash = buffered.ContentHash;
            var size = buffered.SizeBytes;

            // 2. Dedup check before upload (read-only).
            var existing = await FindCommittedAsync(tenantId, hash, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                await TouchAsync(tenantId, existing.Id, cancellationToken).ConfigureAwait(false);
                var reused = await CreateArtifactRowAsync(
                    tenantId, projectId, processingRunId, stageType, artifactType,
                    existing.Id, provider, model, configurationHash, executionSnapshotHash,
                    parentArtifactIds, stageExecutionId, cancellationToken).ConfigureAwait(false);
                return new PublishResult(reused, existing.Id, existing.StorageKey, hash, true, size);
            }

            // 3. Upload to content-addressed key.
            var storageKey = StorageKeyBuilder.BuildKey(
                tenantId, projectId, processingRunId,
                stageType.ToString(), artifactType.ToString(), hash, extension);
            try
            {
                using var uploadStream = OpenBufferedStream(tempPath, memoryBuffer);
                await _storage.UploadAsync(uploadStream, storageKey, contentType, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (!IsDomainOrApp(ex))
            {
                throw ToStorageUnavailable(ex);
            }

            // 4. Verify against authoritative storage checksum when available.
            // Never re-download to hash: only compare when the store returns a SHA-256.
            string? storageChecksum = null;
            try
            {
                storageChecksum = await _storage.GetStorageChecksumAsync(storageKey, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (!IsDomainOrApp(ex))
            {
                throw ToStorageUnavailable(ex);
            }

            if (storageChecksum is not null
                && StorageKeyBuilder.IsLowerHex64(storageChecksum)
                && !string.Equals(storageChecksum, hash, StringComparison.Ordinal))
            {
                try
                {
                    await _storage.DeleteAsync(storageKey, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Best effort: reconciler covers leftovers.
                }

                throw new ErrorCodeException(
                    ErrorCodes.ArtifactChecksumMismatch,
                    "Uploaded bytes failed checksum verification; blob deleted.");
            }

            // 5. Commit rows + lineage in one transaction. Existence checks happen
            // here (after upload) so any DB failure leaves the blob orphan for the
            // reconciler, per spec. Re-check dedup to catch concurrent winners.
            try
            {
                return await CommitNewAsync(
                    tenantId, projectId, processingRunId, stageType, artifactType,
                    hash, size, contentType, storageKey,
                    provider, model, configurationHash, executionSnapshotHash,
                    parentArtifactIds, stageExecutionId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsUniqueViolation(ex))
            {
                // Concurrent duplicate won: drop our blob (best effort) and reuse.
                try
                {
                    await _storage.DeleteAsync(storageKey, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Reconciler covers leftovers.
                }

                var winner = await FindCommittedAsync(tenantId, hash, cancellationToken).ConfigureAwait(false);
                if (winner is null)
                {
                    throw new ConflictException("Content object already exists.", ex);
                }

                await TouchAsync(tenantId, winner.Id, cancellationToken).ConfigureAwait(false);
                var reused = await CreateArtifactRowAsync(
                    tenantId, projectId, processingRunId, stageType, artifactType,
                    winner.Id, provider, model, configurationHash, executionSnapshotHash,
                    parentArtifactIds, stageExecutionId, cancellationToken).ConfigureAwait(false);
                return new PublishResult(reused, winner.Id, winner.StorageKey, hash, true, size);
            }
        }
        finally
        {
            memoryBuffer?.Dispose();
            DeleteTempQuietly(tempPath);
        }
    }

    /// <summary>
    /// Issues a presigned download URL after ownership checks.
    /// Wrong tenant or project throws <see cref="ForbiddenException"/> (403);
    /// missing or uncommitted content throws ARTIFACT_UNAVAILABLE (404).
    /// </summary>
    public async Task<string> GetDownloadUrlAsync(
        Guid tenantId,
        Guid projectId,
        Guid artifactId,
        TimeSpan? expiry = null,
        CancellationToken cancellationToken = default)
    {
        RequireTenant(tenantId);
        RequireId(projectId, nameof(projectId));
        RequireId(artifactId, nameof(artifactId));

        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            var artifact = await db.Set<Artifact>()
                .AsNoTracking()
                .FirstOrDefaultAsync(a => a.Id == artifactId, cancellationToken).ConfigureAwait(false);
            if (artifact is null)
            {
                throw new ErrorCodeException(ErrorCodes.ArtifactUnavailable, "Artifact was not found.");
            }

            if (artifact.TenantId != tenantId || artifact.ProjectId != projectId)
            {
                throw new ForbiddenException("Artifact does not belong to the current tenant/project.");
            }

            var content = await db.Set<ContentObject>()
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == artifact.ContentObjectId, cancellationToken).ConfigureAwait(false);
            if (content is null || content.Status != ContentObjectStatus.Committed)
            {
                throw new ErrorCodeException(ErrorCodes.ArtifactUnavailable, "Artifact content is unavailable.");
            }

            try
            {
                return await _storage.GetPresignedDownloadUrlAsync(
                    content.StorageKey, expiry ?? DefaultUrlExpiry, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (!IsDomainOrApp(ex))
            {
                throw ToStorageUnavailable(ex);
            }
        }
    }

    /// <summary>
    /// Returns parent artifacts via the relational lineage table (never JSON).
    /// </summary>
    public async Task<IReadOnlyList<Artifact>> GetParentsAsync(
        Guid tenantId,
        Guid artifactId,
        CancellationToken cancellationToken = default)
    {
        RequireTenant(tenantId);
        RequireId(artifactId, nameof(artifactId));

        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            var artifact = await db.Set<Artifact>()
                .AsNoTracking()
                .FirstOrDefaultAsync(a => a.Id == artifactId, cancellationToken).ConfigureAwait(false);
            if (artifact is null)
            {
                throw new ErrorCodeException(ErrorCodes.ArtifactUnavailable, "Artifact was not found.");
            }

            if (artifact.TenantId != tenantId)
            {
                throw new ForbiddenException("Artifact does not belong to the current tenant.");
            }

            var parentIds = await db.Set<ArtifactParent>()
                .Where(p => p.ChildArtifactId == artifactId)
                .Select(p => p.ParentArtifactId)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            if (parentIds.Count == 0)
            {
                return [];
            }

            return await db.Set<Artifact>()
                .AsNoTracking()
                .Where(a => parentIds.Contains(a.Id))
                .ToListAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Optional integrity revalidation (QC/render): streams the blob, recomputes
    /// SHA-256, and throws ARTIFACT_CHECKSUM_MISMATCH on drift.
    /// Returns the recomputed hash when intact.
    /// </summary>
    public async Task<string> VerifyIntegrityAsync(
        Guid tenantId,
        Guid artifactId,
        CancellationToken cancellationToken = default)
    {
        RequireTenant(tenantId);
        RequireId(artifactId, nameof(artifactId));

        ContentObject content;
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            var artifact = await db.Set<Artifact>()
                .AsNoTracking()
                .FirstOrDefaultAsync(a => a.Id == artifactId, cancellationToken).ConfigureAwait(false);
            if (artifact is null || artifact.TenantId != tenantId)
            {
                throw new ErrorCodeException(ErrorCodes.ArtifactUnavailable, "Artifact was not found.");
            }

            var loaded = await db.Set<ContentObject>()
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == artifact.ContentObjectId, cancellationToken).ConfigureAwait(false);
            if (loaded is null)
            {
                throw new ErrorCodeException(ErrorCodes.ArtifactUnavailable, "Artifact content is unavailable.");
            }

            content = loaded;
        }

        Stream download;
        try
        {
            download = await _storage.DownloadAsync(content.StorageKey, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!IsDomainOrApp(ex))
        {
            throw ToStorageUnavailable(ex);
        }

        using (download)
        {
            var recomputed = await HashStreamAsync(download, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(recomputed, content.Sha256Hex, StringComparison.Ordinal))
            {
                throw new ErrorCodeException(
                    ErrorCodes.ArtifactChecksumMismatch,
                    "Stored bytes failed integrity revalidation.");
            }

            return recomputed;
        }
    }

    private async Task<PublishResult> CommitNewAsync(
        Guid tenantId,
        Guid projectId,
        Guid runId,
        StageType stageType,
        ArtifactType artifactType,
        string hash,
        long size,
        string contentType,
        string storageKey,
        string? provider,
        string? model,
        string? configurationHash,
        string? executionSnapshotHash,
        IReadOnlyList<Guid> parentIds,
        Guid? stageExecutionId,
        CancellationToken cancellationToken)
    {
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                // Re-check dedup inside the commit transaction.
                var winner = await db.Set<ContentObject>()
                    .FirstOrDefaultAsync(
                        c => c.TenantId == tenantId && c.ContentHash == hash,
                        cancellationToken).ConfigureAwait(false);
                if (winner is not null && winner.Status == ContentObjectStatus.Committed)
                {
                    await db.Database.RollbackTransactionAsync(cancellationToken).ConfigureAwait(false);
                    await TouchAsync(tenantId, winner.Id, cancellationToken).ConfigureAwait(false);
                    try
                    {
                        await _storage.DeleteAsync(storageKey, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                        // Reconciler covers leftovers.
                    }

                    var reusedId = await CreateArtifactRowAsync(
                        tenantId, projectId, runId, stageType, artifactType,
                        winner.Id, provider, model, configurationHash, executionSnapshotHash,
                        parentIds, stageExecutionId, cancellationToken).ConfigureAwait(false);
                    return new PublishResult(reusedId, winner.Id, winner.StorageKey, hash, true, size);
                }

                if (winner is not null)
                {
                    // A pending row from a concurrent publisher: unique violation path
                    // owns this case; surface conflict so the outer catch reuses.
                    throw new ConflictException("Content object already exists.");
                }

                // Task 036 storage hook: new bytes only (dedup-reuse above skips).
                // Fail-closed: a denying gate throws QUOTA_EXCEEDED before rows.
                if (_quotaGate is not null
                    && !await _quotaGate.CheckStorageAsync(tenantId, size, cancellationToken).ConfigureAwait(false))
                {
                    throw new QuotaExceededException(
                        $"Storage quota would be exceeded by {size.ToString(System.Globalization.CultureInfo.InvariantCulture)} bytes (dimension storage).");
                }

                await ValidateParentsAsync(db, tenantId, parentIds, cancellationToken).ConfigureAwait(false);
                StageExecution? execution = null;
                if (stageExecutionId.HasValue)
                {
                    execution = await db.Set<StageExecution>()
                        .FirstOrDefaultAsync(e => e.Id == stageExecutionId.Value, cancellationToken).ConfigureAwait(false);
                    if (execution is null)
                    {
                        throw new NotFoundException($"Stage execution '{stageExecutionId.Value}' was not found.");
                    }

                    if (execution.TenantId != tenantId)
                    {
                        throw new ForbiddenException("Stage execution does not belong to the current tenant.");
                    }
                }

                var now = DateTimeOffset.UtcNow;
                var contentObject = new ContentObject(
                    Guid.NewGuid(), tenantId, hash, hash, size, contentType, storageKey,
                    ContentObjectStatus.Committed, now, now);
                db.Set<ContentObject>().Add(contentObject);

                var artifactId = Guid.NewGuid();
                db.Set<Artifact>().Add(new Artifact(
                    artifactId, tenantId, projectId, runId, stageType, artifactType, "1",
                    contentObject.Id, provider, model, configurationHash, executionSnapshotHash,
                    ArtifactStatus.Committed, null, now));

                foreach (var parentId in parentIds.Distinct())
                {
                    db.Set<ArtifactParent>().Add(new ArtifactParent(
                        Guid.NewGuid(), tenantId, artifactId, parentId, now));
                }

                if (execution is not null)
                {
                    db.Set<StageOutputArtifact>().Add(new StageOutputArtifact(
                        Guid.NewGuid(), tenantId, execution.Id, artifactId, now));
                }

                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new PublishResult(artifactId, contentObject.Id, storageKey, hash, false, size);
            }
            catch
            {
                try
                {
                    await db.Database.RollbackTransactionAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Rollback best effort; original exception propagates.
                }

                throw;
            }
        }
    }

    private async Task<Guid> CreateArtifactRowAsync(
        Guid tenantId,
        Guid projectId,
        Guid runId,
        StageType stageType,
        ArtifactType artifactType,
        Guid contentObjectId,
        string? provider,
        string? model,
        string? configurationHash,
        string? executionSnapshotHash,
        IReadOnlyList<Guid> parentIds,
        Guid? stageExecutionId,
        CancellationToken cancellationToken)
    {
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await ValidateParentsAsync(db, tenantId, parentIds, cancellationToken).ConfigureAwait(false);
                StageExecution? execution = null;
                if (stageExecutionId.HasValue)
                {
                    execution = await db.Set<StageExecution>()
                        .FirstOrDefaultAsync(e => e.Id == stageExecutionId.Value, cancellationToken).ConfigureAwait(false);
                    if (execution is null)
                    {
                        throw new NotFoundException($"Stage execution '{stageExecutionId.Value}' was not found.");
                    }

                    if (execution.TenantId != tenantId)
                    {
                        throw new ForbiddenException("Stage execution does not belong to the current tenant.");
                    }
                }

                var now = DateTimeOffset.UtcNow;
                var artifactId = Guid.NewGuid();
                db.Set<Artifact>().Add(new Artifact(
                    artifactId, tenantId, projectId, runId, stageType, artifactType, "1",
                    contentObjectId, provider, model, configurationHash, executionSnapshotHash,
                    ArtifactStatus.Committed, null, now));

                foreach (var parentId in parentIds.Distinct())
                {
                    db.Set<ArtifactParent>().Add(new ArtifactParent(
                        Guid.NewGuid(), tenantId, artifactId, parentId, now));
                }

                if (execution is not null)
                {
                    db.Set<StageOutputArtifact>().Add(new StageOutputArtifact(
                        Guid.NewGuid(), tenantId, execution.Id, artifactId, now));
                }

                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return artifactId;
            }
            catch
            {
                try
                {
                    await db.Database.RollbackTransactionAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception)
                {
                }

                throw;
            }
        }
    }

    private static async Task ValidateParentsAsync(
        Microsoft.EntityFrameworkCore.DbContext db,
        Guid tenantId,
        IReadOnlyList<Guid> parentIds,
        CancellationToken cancellationToken)
    {
        foreach (var parentId in parentIds.Distinct())
        {
            var parent = await db.Set<Artifact>()
                .AsNoTracking()
                .FirstOrDefaultAsync(a => a.Id == parentId, cancellationToken).ConfigureAwait(false);
            if (parent is null)
            {
                throw new ErrorCodeException(ErrorCodes.ArtifactUnavailable, "Parent artifact was not found.");
            }

            if (parent.TenantId != tenantId)
            {
                throw new ForbiddenException("Parent artifact does not belong to the current tenant.");
            }
        }
    }

    private async Task<ContentObject?> FindCommittedAsync(Guid tenantId, string hash, CancellationToken cancellationToken)
    {
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            return await db.Set<ContentObject>()
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    c => c.TenantId == tenantId && c.ContentHash == hash && c.Status == ContentObjectStatus.Committed,
                    cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task TouchAsync(Guid tenantId, Guid contentObjectId, CancellationToken cancellationToken)
    {
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            await db.Database.ExecuteSqlRawAsync(
                "UPDATE content_objects SET last_referenced_at = {0} WHERE id = {1} AND tenant_id = {2}",
                DateTimeOffset.UtcNow, contentObjectId, tenantId).ConfigureAwait(false);
        }
    }

    private sealed record BufferedContent(string ContentHash, long SizeBytes, string? TempPath, System.IO.MemoryStream? MemoryBuffer);

    private static async Task<BufferedContent> BufferWithHashAsync(Stream content, CancellationToken cancellationToken)
    {
        // Small payloads stay in memory (capped at 16MB); larger spill to /tmp.
        // Single streaming pass computes SHA-256 while buffering.
        const int bufferSize = 81920;
        var buffer = new byte[bufferSize];
        long total = 0;

        if (content.CanSeek && content.Length >= 0 && content.Length <= MaxMemoryBytes)
        {
            using var sha = System.Security.Cryptography.SHA256.Create();
            var memory = new MemoryStream((int)content.Length);
            try
            {
                int read;
                while ((read = await content.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    total += read;
                    if (total > MaxMemoryBytes)
                    {
                        break;
                    }

                    sha.TransformBlock(buffer, 0, read, null, 0);
                    await memory.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }

                if (total > MaxMemoryBytes)
                {
                    memory.Dispose();
                }
                else
                {
                    sha.TransformFinalBlock([], 0, 0);
                    var hash = Convert.ToHexString(sha.Hash!).ToLowerInvariant();
                    memory.Position = 0;
                    return new BufferedContent(hash, total, null, memory);
                }
            }
            catch
            {
                memory.Dispose();
                throw;
            }
        }

        if (content.CanSeek)
        {
            try
            {
                content.Position = 0;
            }
            catch (Exception)
            {
                // Non-seekable in practice; continue from current position.
            }
        }

        total = 0;

        var tempPath = Path.Combine(Path.GetTempPath(), string.Concat("dubbing-artifact-", Guid.NewGuid().ToString("N"), ".tmp"));
        FileStream? temp = null;
        try
        {
            temp = new FileStream(tempPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, bufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var sha2 = System.Security.Cryptography.SHA256.Create();
            int read;
            while ((read = await content.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                total += read;
                sha2.TransformBlock(buffer, 0, read, null, 0);
                await temp.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }

            sha2.TransformFinalBlock([], 0, 0);
            var hash = Convert.ToHexString(sha2.Hash!).ToLowerInvariant();
            await temp.FlushAsync(cancellationToken).ConfigureAwait(false);
            temp.Dispose();
            temp = null;
            return new BufferedContent(hash, total, tempPath, null);
        }
        finally
        {
            temp?.Dispose();
        }
    }

    private static Stream OpenBufferedStream(string? tempPath, MemoryStream? memory)
    {
        if (memory is not null)
        {
            memory.Position = 0;
            return new NonDisposingStream(memory);
        }

        return new FileStream(tempPath!, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
    }

    private static async Task<string> HashStreamAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        var buffer = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            sha.TransformBlock(buffer, 0, read, null, 0);
        }

        sha.TransformFinalBlock([], 0, 0);
        return Convert.ToHexString(sha.Hash!).ToLowerInvariant();
    }

    private static void DeleteTempQuietly(string? tempPath)
    {
        if (string.IsNullOrEmpty(tempPath))
        {
            return;
        }

        try
        {
            File.Delete(tempPath);
        }
        catch (Exception)
        {
            // Best effort; OS temp cleaners cover leftovers.
        }
    }

    private sealed class NonDisposingStream : Stream
    {
        private readonly MemoryStream _inner;

        public NonDisposingStream(MemoryStream inner)
        {
            _inner = inner;
        }

        public override bool CanRead => _inner.CanRead;

        public override bool CanSeek => _inner.CanSeek;

        public override bool CanWrite => false;

        public override long Length => _inner.Length;

        public override long Position { get => _inner.Position; set => _inner.Position = value; }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            // Owner disposes the MemoryStream; do not double-dispose here.
        }
    }

    private static bool IsUniqueViolation(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is DomainException domain
                && domain.Message.Contains("CONFLICT", StringComparison.Ordinal))
            {
                return true;
            }

            if (current is DbUpdateException)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsDomainOrApp(Exception exception)
    {
        return exception is DomainException || exception is AppException;
    }

    private static ErrorCodeException ToStorageUnavailable(Exception exception)
    {
        return new ErrorCodeException(
            ErrorCodes.StorageUnavailable,
            "Storage is temporarily unavailable; retry the operation.",
            exception);
    }

    private static void ValidateOptional(string? value, string name, int maxLength)
    {
        if (value is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            throw new DomainException($"{name} must not be empty when set.");
        }

        if (value.Length > maxLength)
        {
            throw new DomainException($"{name} must be at most {maxLength} chars.");
        }
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
