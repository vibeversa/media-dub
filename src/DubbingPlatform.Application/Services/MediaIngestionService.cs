using System.Security.Cryptography;
using System.Text.Json;
using DubbingPlatform.Application.Abstractions;
using DubbingPlatform.Application.Errors;
using DubbingPlatform.Application.Exceptions;
using DubbingPlatform.Application.MultiTenancy;
using DubbingPlatform.Application.Options;
using DubbingPlatform.Application.StateMachines;
using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Domain.Enums;
using DubbingPlatform.Domain.Exceptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DubbingPlatform.Application.Services;

/// <summary>
/// Result of media ingestion for one upload session.
/// </summary>
public sealed record MediaIngestionResult(
    Guid MediaAssetId,
    Guid ContentObjectId,
    Guid FfprobeArtifactId,
    string ContentHash,
    bool IsValid,
    string? FailureCode,
    string? FailureReason,
    bool IsDuplicate,
    Guid? ExistingAssetId);

/// <summary>
/// Pure media-validation rules. Never trusts client MIME/extension: all
/// decisions use ffprobe-sniffed <c>Container</c>/codecs plus measured
/// <c>sizeBytes</c>. Size/duration/container/codec violations are
/// <c>MEDIA_UNSUPPORTED</c> (415); ffprobe execution/parse failures are
/// <c>MEDIA_CORRUPT</c> (422) and are thrown by the probe service before
/// this validator runs.
/// </summary>
public static class MediaValidator
{
    private static readonly HashSet<string> DecodableAudioSubstrings = new(StringComparer.OrdinalIgnoreCase)
    {
        "aac",
        "mp3",
        "pcm",
        "flac",
        "opus",
        "vorbis",
    };

    private static readonly HashSet<string> AllowedVideoCodecs = new(StringComparer.OrdinalIgnoreCase)
    {
        "h264",
        "avc",
        "hevc",
        "h265",
        "vp9",
        "av1",
    };

    /// <summary>
    /// Validates a probed file. Returns metadata for persistence in both
    /// valid and invalid cases (invalid uses probe metadata with failure set).
    /// </summary>
    public static MediaValidationOutcome Validate(FfprobeResult probe, long sizeBytes, MediaOptions media)
    {
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(media);

        if (sizeBytes < 1 || sizeBytes > media.MaxUploadBytes)
        {
            return Invalid(probe, ErrorCodes.MediaUnsupported, $"Size {sizeBytes} bytes is outside 1..{media.MaxUploadBytes}.");
        }

        if (probe.DurationMs < 1000 || probe.DurationMs > media.MaxDurationMs)
        {
            return Invalid(probe, ErrorCodes.MediaUnsupported, $"Duration {probe.DurationMs}ms is outside 1000..{media.MaxDurationMs}.");
        }

        if (!IsAllowedContainer(probe.Container, media.AllowedContainers))
        {
            return Invalid(probe, ErrorCodes.MediaUnsupported, $"Container '{probe.Container}' is not supported.");
        }

        var audioStreams = probe.Streams
            .Where(s => string.Equals(s.CodecType, "audio", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (audioStreams.Count == 0 || audioStreams.All(s => !IsDecodableAudio(s.Codec)))
        {
            return Invalid(probe, ErrorCodes.MediaUnsupported, "No decodable audio stream (aac/mp3/pcm/flac/opus/vorbis) found.");
        }

        var videoStreams = probe.Streams
            .Where(s => string.Equals(s.CodecType, "video", StringComparison.OrdinalIgnoreCase))
            .ToList();
        foreach (var video in videoStreams)
        {
            if (!AllowedVideoCodecs.Contains((video.Codec ?? string.Empty).Trim()))
            {
                return Invalid(probe, ErrorCodes.MediaUnsupported, $"Video codec '{video.Codec}' is not supported (h264/hevc/vp9/av1).");
            }
        }

        var firstAudio = audioStreams.First(s => IsDecodableAudio(s.Codec));
        var firstVideo = videoStreams.FirstOrDefault();
        return new MediaValidationOutcome(
            true, null, null,
            probe.Container,
            (firstAudio.Codec ?? "unknown").Trim().ToLowerInvariant(),
            firstVideo is null ? null : (firstVideo.Codec ?? string.Empty).Trim().ToLowerInvariant(),
            probe.DurationMs,
            firstAudio.SampleRate is > 0 ? firstAudio.SampleRate.Value : 48000,
            firstAudio.Channels is > 0 ? firstAudio.Channels.Value : 2,
            string.IsNullOrWhiteSpace(firstAudio.ChannelLayout) ? "stereo" : firstAudio.ChannelLayout.Trim());
    }

    /// <summary>
    /// Builds an invalid outcome preserving probe metadata with safe fallbacks
    /// for <c>MediaAsset</c> persistence (which requires non-empty codec and
    /// positive sample rate/channels).
    /// </summary>
    public static MediaValidationOutcome Invalid(FfprobeResult probe, string code, string reason)
    {
        var firstAudio = probe.Streams.FirstOrDefault(s => string.Equals(s.CodecType, "audio", StringComparison.OrdinalIgnoreCase));
        var firstVideo = probe.Streams.FirstOrDefault(s => string.Equals(s.CodecType, "video", StringComparison.OrdinalIgnoreCase));
        var container = string.IsNullOrWhiteSpace(probe.Container) ? "unknown" : probe.Container;
        var audioCodec = firstAudio is null || string.IsNullOrWhiteSpace(firstAudio.Codec)
            ? "unknown"
            : firstAudio.Codec.Trim().ToLowerInvariant();
        return new MediaValidationOutcome(
            false, code, reason,
            container, audioCodec,
            firstVideo is null || string.IsNullOrWhiteSpace(firstVideo.Codec) ? null : firstVideo.Codec.Trim().ToLowerInvariant(),
            probe.DurationMs < 0 ? 0 : (int)Math.Min(probe.DurationMs, int.MaxValue),
            firstAudio?.SampleRate is > 0 ? firstAudio.SampleRate.Value : 48000,
            firstAudio?.Channels is > 0 ? firstAudio.Channels.Value : 1,
            string.IsNullOrWhiteSpace(firstAudio?.ChannelLayout) ? "mono" : firstAudio.ChannelLayout!.Trim());
    }

    public static bool IsDecodableAudio(string? codec)
    {
        if (string.IsNullOrWhiteSpace(codec))
        {
            return false;
        }

        var normalized = codec.Trim().ToLowerInvariant();
        foreach (var family in DecodableAudioSubstrings)
        {
            if (normalized.Contains(family, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsAllowedContainer(string container, string[] allowed)
    {
        if (string.IsNullOrWhiteSpace(container))
        {
            return false;
        }

        var normalized = container.Trim();
        foreach (var entry in allowed)
        {
            if (string.Equals(normalized, entry.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// Validated media metadata for persistence.
/// </summary>
public sealed record MediaValidationOutcome(
    bool IsValid,
    string? FailureCode,
    string? FailureReason,
    string Container,
    string AudioCodec,
    string? VideoCodec,
    long DurationMs,
    int SampleRate,
    int Channels,
    string ChannelLayout);

/// <summary>
/// Upload ingestion: hashing, duplicate handling, ffprobe validation, and
/// media-readiness transitions. Single S3 download streams to a temp file
/// while computing SHA-256 (no double-download); the temp file backs ffprobe
/// and the content-addressed re-upload via <see cref="ArtifactService"/>.
/// Dedup is tenant-scoped on <c>(tenant_id, content_hash)</c>: same-project
/// existing asset → mark <c>Duplicate</c>, link, publish nothing; cross-project
/// same-tenant → reuse <c>ContentObject</c> + new <c>MediaAsset</c>+<c>Artifact</c>;
/// concurrent winners serialize on the unique index (loser reuses via
/// <see cref="ArtifactService"/>). Corrupt/unsupported fail fast (no retry;
/// worker parks in <c>_skipped</c>); storage+I/O failures are transient
/// (worker retries into <c>_error</c>).
/// </summary>
public sealed class MediaIngestionService
{
    private readonly IStageExecutionContextFactory _contextFactory;
    private readonly IArtifactStorage _storage;
    private readonly ArtifactService _artifacts;
    private readonly IFFprobeService _ffprobe;
    private readonly IQuotaGate _quotaGate;
    private readonly MediaOptions _media;
    private readonly ILogger<MediaIngestionService> _logger;

    public MediaIngestionService(
        IStageExecutionContextFactory contextFactory,
        IArtifactStorage storage,
        ArtifactService artifacts,
        IFFprobeService ffprobe,
        IQuotaGate quotaGate,
        IOptions<MediaOptions> media,
        ILogger<MediaIngestionService> logger)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(artifacts);
        ArgumentNullException.ThrowIfNull(ffprobe);
        ArgumentNullException.ThrowIfNull(quotaGate);
        ArgumentNullException.ThrowIfNull(media);
        ArgumentNullException.ThrowIfNull(logger);
        _contextFactory = contextFactory;
        _storage = storage;
        _artifacts = artifacts;
        _ffprobe = ffprobe;
        _quotaGate = quotaGate;
        _media = media.Value;
        _logger = logger;
    }

    /// <summary>
    /// Ingests one completed upload session. Idempotent on redelivery: a
    /// completed session whose hash already maps to a same-project asset
    /// returns <c>IsDuplicate</c> without new rows.
    /// </summary>
    public async Task<MediaIngestionResult> IngestAsync(
        Guid tenantId,
        Guid projectId,
        Guid uploadId,
        CancellationToken cancellationToken = default)
    {
        RequireTenant(tenantId);
        RequireId(projectId, nameof(projectId));
        RequireId(uploadId, nameof(uploadId));

        var session = await LoadOwnedSessionAsync(tenantId, projectId, uploadId, cancellationToken).ConfigureAwait(false);
        if (session.Status != UploadStatus.Completed)
        {
            throw new ErrorCodeException(ErrorCodes.UploadIncomplete, $"Upload '{uploadId}' is {session.Status}; completion is required before ingestion.");
        }

        var project = await LoadOwnedProjectAsync(tenantId, projectId, cancellationToken).ConfigureAwait(false);

        bool exists;
        try
        {
            exists = await _storage.ExistsAsync(session.StorageKey, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!IsDomainOrApp(ex))
        {
            throw new ErrorCodeException(ErrorCodes.StorageUnavailable, "Storage is temporarily unavailable; retry the operation.", ex);
        }

        if (!exists)
        {
            throw new ErrorCodeException(ErrorCodes.UploadIncomplete, $"Upload bytes '{session.StorageKey}' are missing in storage.");
        }

        bool allowed;
        try
        {
            allowed = await _quotaGate.CheckStorageAsync(tenantId, session.DeclaredSizeBytes, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!IsDomainOrApp(ex))
        {
            throw new ErrorCodeException(ErrorCodes.StorageUnavailable, "Quota check is temporarily unavailable; retry the operation.", ex);
        }

        if (!allowed)
        {
            throw new QuotaExceededException("Storage quota would be exceeded by this upload.");
        }

        var tempPath = Path.Combine(Path.GetTempPath(), string.Concat("dubbing-ingest-", Guid.NewGuid().ToString("N"), ".tmp"));
        string contentHash;
        long sizeBytes;
        try
        {
            (contentHash, sizeBytes) = await DownloadHashToTempAsync(session.StorageKey, tempPath, cancellationToken).ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(session.ClientSha256Hex))
            {
                var claimed = session.ClientSha256Hex.Trim().ToLowerInvariant();
                if (!string.Equals(claimed, contentHash, StringComparison.Ordinal))
                {
                    throw new ErrorCodeException(ErrorCodes.ArtifactChecksumMismatch, "Client SHA-256 does not match computed content hash.");
                }
            }

            string? authoritative = null;
            try
            {
                authoritative = await _storage.GetStorageChecksumAsync(session.StorageKey, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (!IsDomainOrApp(ex))
            {
                throw new ErrorCodeException(ErrorCodes.StorageUnavailable, "Storage is temporarily unavailable; retry the operation.", ex);
            }

            if (authoritative is not null
                && Storage.StorageKeyBuilder.IsLowerHex64(authoritative.Trim().ToLowerInvariant())
                && !string.Equals(authoritative.Trim().ToLowerInvariant(), contentHash, StringComparison.Ordinal))
            {
                throw new ErrorCodeException(ErrorCodes.ArtifactChecksumMismatch, "Stored bytes failed checksum verification.");
            }

            var duplicate = await FindSameProjectAssetAsync(tenantId, projectId, contentHash, cancellationToken).ConfigureAwait(false);
            if (duplicate is not null)
            {
                await MarkDuplicateAsync(tenantId, uploadId, contentHash, cancellationToken).ConfigureAwait(false);
                await TouchContentAsync(tenantId, contentHash, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("Upload {UploadId} duplicates asset {AssetId}; linked without new pipeline.", uploadId, duplicate.Id);
                return new MediaIngestionResult(duplicate.Id, duplicate.ContentObjectId, Guid.Empty, contentHash, true, null, null, true, duplicate.Id);
            }

            FfprobeResult? probe = null;
            string? probeFailureCode = null;
            string? probeFailureReason = null;
            try
            {
                probe = await _ffprobe.ProbeAsync(tempPath, cancellationToken).ConfigureAwait(false);
            }
            catch (ErrorCodeException ex) when (string.Equals(ex.ErrorCode, ErrorCodes.MediaCorrupt, StringComparison.Ordinal))
            {
                probeFailureCode = ex.ErrorCode;
                probeFailureReason = ex.Message;
            }

            MediaValidationOutcome outcome;
            string ffprobeJson;
            if (probe is null)
            {
                outcome = new MediaValidationOutcome(
                    false, probeFailureCode ?? ErrorCodes.MediaCorrupt,
                    probeFailureReason ?? "Media is corrupt or undecodable.",
                    "unknown", "unknown", null, 0, 48000, 1, "mono");
                ffprobeJson = JsonSerializer.Serialize(new { error = outcome.FailureCode, message = outcome.FailureReason });
            }
            else
            {
                outcome = MediaValidator.Validate(probe, sizeBytes, _media);
                ffprobeJson = JsonSerializer.Serialize(new
                {
                    container = probe.Container,
                    durationMs = probe.DurationMs,
                    streams = probe.Streams.Select(s => new
                    {
                        codecType = s.CodecType,
                        codec = s.Codec,
                        width = s.Width,
                        height = s.Height,
                        fps = s.Fps,
                        sampleRate = s.SampleRate,
                        channels = s.Channels,
                        channelLayout = s.ChannelLayout,
                    }),
                });
            }

            return await PersistAsync(
                tenantId, project, session, tempPath, contentHash, sizeBytes,
                outcome, ffprobeJson, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            DeleteTempQuietly(tempPath);
        }
    }

    private async Task<MediaIngestionResult> PersistAsync(
        Guid tenantId,
        DubbingProject project,
        UploadSession session,
        string tempPath,
        string contentHash,
        long sizeBytes,
        MediaValidationOutcome outcome,
        string ffprobeJson,
        CancellationToken cancellationToken)
    {
        var projectId = project.Id;
        var uploadId = session.Id;
        var extension = ExtensionForKey(session.FileName);

        PublishResult source;
        using (var stream = new FileStream(tempPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            source = await _artifacts.PublishAsync(
                tenantId, projectId, uploadId,
                StageType.MediaValidation, ArtifactType.SourceOriginal,
                stream, extension, string.IsNullOrWhiteSpace(session.DeclaredContentType) ? "application/octet-stream" : session.DeclaredContentType.Trim(),
                null, null, null, null, [],
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        PublishResult ffprobeArtifact;
        using (var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(ffprobeJson), writable: false))
        {
            ffprobeArtifact = await _artifacts.PublishAsync(
                tenantId, projectId, uploadId,
                StageType.MediaValidation, ArtifactType.FfprobeAnalysis,
                stream, ".json", "application/json",
                null, null, null, null, [],
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        var now = DateTimeOffset.UtcNow;
        var assetId = Guid.NewGuid();
        var durationMs = (int)Math.Min(Math.Max(outcome.DurationMs, 0), int.MaxValue);
        var failureReason = outcome.IsValid
            ? null
            : string.Concat(outcome.FailureCode ?? ErrorCodes.MediaCorrupt, ": ", (outcome.FailureReason ?? "Media validation failed.").Trim());
        if (failureReason is not null && failureReason.Length > 1024)
        {
            failureReason = failureReason.Substring(0, 1024);
        }

        var asset = new MediaAsset(
            assetId, tenantId, projectId, source.ContentObjectId,
            session.FileName, outcome.Container, outcome.AudioCodec, outcome.VideoCodec,
            sizeBytes, durationMs, outcome.SampleRate, outcome.Channels, outcome.ChannelLayout,
            outcome.IsValid ? MediaAssetStatus.Valid : MediaAssetStatus.Invalid,
            failureReason, contentHash, now);

        var executionId = Guid.NewGuid();
        var outputJson = JsonSerializer.Serialize(new[] { source.ArtifactId.ToString("N"), ffprobeArtifact.ArtifactId.ToString("N") });
        var execution = new StageExecution(
            executionId, tenantId, projectId, uploadId,
            StageType.MediaValidation, ScopeType.Project, projectId.ToString("N"),
            null, 0, outcome.IsValid ? StageStatus.Completed : StageStatus.Failed,
            "MediaIngestionWorker", Guid.NewGuid().ToString("N"), 0,
            now.AddMinutes(5), now, now,
            contentHash, project.ConfigurationHash, contentHash,
            outputJson,
            outcome.IsValid ? null : outcome.FailureCode,
            outcome.IsValid ? null : failureReason,
            now, now);

        var targetProject = outcome.IsValid ? ProjectStatus.MediaReady : ProjectStatus.MediaRejected;

        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                db.Set<MediaAsset>().Add(asset);
                db.Set<StageExecution>().Add(execution);
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

                await db.Database.ExecuteSqlRawAsync(
                    "UPDATE upload_sessions SET content_hash = {0} WHERE id = {1} AND tenant_id = {2}",
                    contentHash, uploadId, tenantId).ConfigureAwait(false);

                await db.Database.ExecuteSqlRawAsync(
                    "UPDATE artifacts SET metadata_json = {0} WHERE id = {1} AND tenant_id = {2}",
                    ffprobeJson, ffprobeArtifact.ArtifactId, tenantId).ConfigureAwait(false);

                await TransitionProjectAsync(db, tenantId, project, targetProject, outcome.IsValid ? assetId : null, cancellationToken).ConfigureAwait(false);

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
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

        _logger.LogInformation(
            "Ingested upload {UploadId} as asset {AssetId} valid={IsValid} hash={Hash}.",
            uploadId, assetId, outcome.IsValid, contentHash);

        return new MediaIngestionResult(
            assetId, source.ContentObjectId, ffprobeArtifact.ArtifactId,
            contentHash, outcome.IsValid, outcome.FailureCode, failureReason, false, null);
    }

    private static async Task TransitionProjectAsync(
        Microsoft.EntityFrameworkCore.DbContext db,
        Guid tenantId,
        DubbingProject project,
        ProjectStatus target,
        Guid? sourceAssetId,
        CancellationToken cancellationToken)
    {
        var current = project.Status;
        if (current == target && sourceAssetId is null)
        {
            return;
        }

        if (current == target && sourceAssetId is not null)
        {
            await db.Database.ExecuteSqlRawAsync(
                "UPDATE dubbing_projects SET source_media_asset_id = {0}, updated_at = {1} WHERE id = {2} AND tenant_id = {3}",
                sourceAssetId.Value, DateTimeOffset.UtcNow, project.Id, tenantId).ConfigureAwait(false);
            return;
        }

        if (current == ProjectStatus.Created)
        {
            ProjectStateMachine.EnsureCanTransition(ProjectStatus.Created, ProjectStatus.Uploading);
            await db.Database.ExecuteSqlRawAsync(
                "UPDATE dubbing_projects SET status = {0}, updated_at = {1} WHERE id = {2} AND tenant_id = {3}",
                ProjectStatus.Uploading.ToString(), DateTimeOffset.UtcNow, project.Id, tenantId).ConfigureAwait(false);
            current = ProjectStatus.Uploading;
        }

        if (ProjectStateMachine.CanTransition(current, target))
        {
            ProjectStateMachine.EnsureCanTransition(current, target);
        }
        else if (current is ProjectStatus.MediaReady or ProjectStatus.MediaRejected
            && target is ProjectStatus.MediaReady or ProjectStatus.MediaRejected)
        {
            // Latest upload wins: the state machine models a single upload cycle,
            // re-uploads replace the readiness outcome.
        }
        else
        {
            ProjectStateMachine.EnsureCanTransition(current, target);
        }

        if (sourceAssetId.HasValue)
        {
            await db.Database.ExecuteSqlRawAsync(
                "UPDATE dubbing_projects SET status = {0}, source_media_asset_id = {1}, updated_at = {2} WHERE id = {3} AND tenant_id = {4}",
                target.ToString(), sourceAssetId.Value, DateTimeOffset.UtcNow, project.Id, tenantId).ConfigureAwait(false);
        }
        else
        {
            await db.Database.ExecuteSqlRawAsync(
                "UPDATE dubbing_projects SET status = {0}, updated_at = {1} WHERE id = {2} AND tenant_id = {3}",
                target.ToString(), DateTimeOffset.UtcNow, project.Id, tenantId).ConfigureAwait(false);
        }
    }

    private async Task<(string Hash, long Size)> DownloadHashToTempAsync(string storageKey, string tempPath, CancellationToken cancellationToken)
    {
        Stream download;
        try
        {
            download = await _storage.DownloadAsync(storageKey, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!IsDomainOrApp(ex))
        {
            throw new ErrorCodeException(ErrorCodes.StorageUnavailable, "Storage is temporarily unavailable; retry the operation.", ex);
        }

        using (download)
        {
            using var sha = SHA256.Create();
            using var temp = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = await download.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                total += read;
                sha.TransformBlock(buffer, 0, read, null, 0);
                await temp.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }

            sha.TransformFinalBlock([], 0, 0);
            await temp.FlushAsync(cancellationToken).ConfigureAwait(false);
            return (Convert.ToHexString(sha.Hash!).ToLowerInvariant(), total);
        }
    }

    private async Task<UploadSession> LoadOwnedSessionAsync(Guid tenantId, Guid projectId, Guid uploadId, CancellationToken cancellationToken)
    {
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

    private async Task<DubbingProject> LoadOwnedProjectAsync(Guid tenantId, Guid projectId, CancellationToken cancellationToken)
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

    private async Task<MediaAsset?> FindSameProjectAssetAsync(Guid tenantId, Guid projectId, string hash, CancellationToken cancellationToken)
    {
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            return await db.Set<MediaAsset>()
                .AsNoTracking()
                .Where(a => a.ProjectId == projectId && a.ContentHash == hash)
                .OrderBy(a => a.CreatedAt)
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task MarkDuplicateAsync(Guid tenantId, Guid uploadId, string hash, CancellationToken cancellationToken)
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

            UploadStateMachine.EnsureCanTransition(session.Status, UploadStatus.Duplicate);
            await db.Database.ExecuteSqlRawAsync(
                "UPDATE upload_sessions SET status = {0}, content_hash = {1} WHERE id = {2} AND tenant_id = {3}",
                UploadStatus.Duplicate.ToString(), hash, uploadId, tenantId).ConfigureAwait(false);
        }
    }

    private async Task TouchContentAsync(Guid tenantId, string hash, CancellationToken cancellationToken)
    {
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            await db.Database.ExecuteSqlRawAsync(
                "UPDATE content_objects SET last_referenced_at = {0} WHERE tenant_id = {1} AND content_hash = {2}",
                DateTimeOffset.UtcNow, tenantId, hash).ConfigureAwait(false);
        }
    }

    internal static string ExtensionForKey(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return string.Empty;
        }

        var extension = Path.GetExtension(fileName.Trim()).Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(extension) || extension.Length > 16)
        {
            return string.Empty;
        }

        for (var i = 1; i < extension.Length; i++)
        {
            var c = extension[i];
            if (!((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')))
            {
                return string.Empty;
            }
        }

        return extension;
    }

    private static void DeleteTempQuietly(string tempPath)
    {
        try
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
        catch (Exception)
        {
            // Best effort; OS temp cleaners cover leftovers.
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
