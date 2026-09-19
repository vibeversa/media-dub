using System.Text.Json;
using DubbingPlatform.Application.Abstractions;
using DubbingPlatform.Application.Errors;
using DubbingPlatform.Application.Exceptions;
using DubbingPlatform.Application.MultiTenancy;
using DubbingPlatform.Application.Options;
using DubbingPlatform.Domain.Enums;
using DubbingPlatform.Domain.Exceptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DubbingPlatform.Application.Services;

/// <summary>
/// Result of canonical-audio preparation for one run.
/// </summary>
public sealed record AudioPreparationResult(
    Guid ArtifactId,
    Guid ContentObjectId,
    IReadOnlyList<string> FfmpegArgs,
    int SampleRate,
    int Channels,
    string ChannelLayout,
    int DurationMs);

/// <summary>
/// Canonical-audio preparation. Downloads the validated source to an isolated
/// temp work dir, requires &gt;2x source bytes free (<c>RESOURCE_EXHAUSTED</c>,
/// no partial artifact), serializes FFmpeg execution through the process-wide
/// <c>Media:MaxConcurrentMediaJobs</c> gate, extracts 48kHz 24-bit FLAC
/// preserving layout via <see cref="IFFmpegService"/>, verifies
/// sample-rate/layout/duration(±100ms) via <see cref="IFFprobeService"/>, and
/// publishes one <c>CanonicalAudio</c> artifact with the exact FFmpeg args in
/// artifact <c>metadata_json</c> (no provider row: no provider is involved).
/// Domain failures throw <c>AppException</c>/<c>DomainException</c> (the worker
/// records them on the execution and publishes <c>StageFailed</c>);
/// <c>TimeoutException</c>/<c>IOException</c> propagate as transient for
/// transport retry. Temp dirs are always deleted.
/// </summary>
public sealed class AudioPreparationService
{
    /// <summary>
    /// Duration tolerance between source and canonical audio (ms).
    /// </summary>
    public const int DurationToleranceMs = 100;

    private readonly IStageExecutionContextFactory _contextFactory;
    private readonly IArtifactStorage _storage;
    private readonly ArtifactService _artifacts;
    private readonly IFFprobeService _ffprobe;
    private readonly IFFmpegService _ffmpeg;
    private readonly IDiskSpaceChecker _disk;
    private readonly MediaOptions _media;
    private readonly ILogger<AudioPreparationService> _logger;

    public AudioPreparationService(
        IStageExecutionContextFactory contextFactory,
        IArtifactStorage storage,
        ArtifactService artifacts,
        IFFprobeService ffprobe,
        IFFmpegService ffmpeg,
        IDiskSpaceChecker disk,
        IOptions<MediaOptions> media,
        ILogger<AudioPreparationService> logger)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(artifacts);
        ArgumentNullException.ThrowIfNull(ffprobe);
        ArgumentNullException.ThrowIfNull(ffmpeg);
        ArgumentNullException.ThrowIfNull(disk);
        ArgumentNullException.ThrowIfNull(media);
        ArgumentNullException.ThrowIfNull(logger);
        _contextFactory = contextFactory;
        _storage = storage;
        _artifacts = artifacts;
        _ffprobe = ffprobe;
        _ffmpeg = ffmpeg;
        _disk = disk;
        _media = media.Value;
        _logger = logger;
    }

    public async Task<AudioPreparationResult> PrepareAsync(
        Guid tenantId,
        Guid projectId,
        Guid runId,
        Guid executionId,
        string owner,
        string token,
        SemaphoreSlim? concurrencyGate,
        CancellationToken cancellationToken = default)
    {
        RequireTenant(tenantId);
        RequireId(projectId, nameof(projectId));
        RequireId(runId, nameof(runId));
        RequireId(executionId, nameof(executionId));
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        var source = await SourceMediaLoader.LoadAsync(_contextFactory, tenantId, projectId, cancellationToken).ConfigureAwait(false);

        string workDir;
        try
        {
            workDir = CreateTempWorkingDir();
        }
        catch (IOException ex)
        {
            throw new ErrorCodeException(ErrorCodes.StorageUnavailable, "Media preparation cannot create temp dir; retry the operation.", ex);
        }

        try
        {
            _disk.EnsureFree(workDir, checked(source.Asset.SizeBytes * 2L));

            var extension = ExtensionFor(source.Asset.FileName);
            var sourcePath = Path.Combine(workDir, string.Concat("source", extension));
            var destPath = Path.Combine(workDir, "canonical.flac");

            await DownloadToFileAsync(source.Content.StorageKey, sourcePath, cancellationToken).ConfigureAwait(false);

            FfmpegResult ffmpegResult;
            if (concurrencyGate is not null)
            {
                await concurrencyGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    ffmpegResult = await _ffmpeg.ExtractCanonicalAudioAsync(sourcePath, destPath, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    concurrencyGate.Release();
                }
            }
            else
            {
                ffmpegResult = await _ffmpeg.ExtractCanonicalAudioAsync(sourcePath, destPath, cancellationToken).ConfigureAwait(false);
            }

            var probe = await ProbeLocalAsync(destPath, cancellationToken).ConfigureAwait(false);
            VerifyCanonical(probe, source, projectId);
            var canonicalAudio = probe.Streams.First(s => string.Equals(s.CodecType, "audio", StringComparison.OrdinalIgnoreCase));
            var canonicalSampleRate = canonicalAudio.SampleRate ?? 48000;
            var canonicalChannels = canonicalAudio.Channels ?? source.Asset.Channels;
            var canonicalLayout = canonicalAudio.ChannelLayout ?? source.Asset.ChannelLayout;

            PublishResult published;
            using (var stream = new FileStream(destPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                published = await _artifacts.PublishAsync(
                    tenantId, projectId, runId,
                    StageType.AudioPreparation, ArtifactType.CanonicalAudio,
                    stream, ".flac", "audio/flac",
                    null, null, null, null,
                    source.SourceArtifactId.HasValue ? [source.SourceArtifactId.Value] : [],
                    executionId,
                    cancellationToken).ConfigureAwait(false);
            }

            var metadataJson = JsonSerializer.Serialize(new
            {
                ffmpegArgs = ffmpegResult.Args,
                sourceContentHash = source.Content.ContentHash,
                sourceAssetId = source.Asset.Id.ToString("N"),
                sampleRate = canonicalSampleRate,
                channels = canonicalChannels,
                channelLayout = canonicalLayout,
                durationMs = probe.DurationMs,
            });

            using (TenantContext.BeginScope(tenantId))
            {
                using var db = _contextFactory.CreateDbContext();
                await db.Database.ExecuteSqlRawAsync(
                    "UPDATE artifacts SET metadata_json = {0} WHERE id = {1} AND tenant_id = {2}",
                    metadataJson, published.ArtifactId, tenantId).ConfigureAwait(false);
            }

            var stages = new StageExecutionService(_contextFactory, Microsoft.Extensions.Options.Options.Create(new RetryOptions()));
            await stages.CompleteAsync(
                tenantId, executionId, owner, token,
                [published.ArtifactId.ToString("N")],
                cancellationToken).ConfigureAwait(false);

            _logger.LogInformation(
                "Canonical audio prepared for run {RunId}: artifact {ArtifactId} ({SampleRate}Hz, {Channels}ch).",
                runId, published.ArtifactId, canonicalSampleRate, canonicalChannels);

            return new AudioPreparationResult(
                published.ArtifactId, published.ContentObjectId, ffmpegResult.Args,
                canonicalSampleRate, canonicalChannels,
                canonicalLayout,
                (int)Math.Min(Math.Max(probe.DurationMs, 0), int.MaxValue));
        }
        finally
        {
            DeleteWorkDirQuietly(workDir);
        }
    }

    private void VerifyCanonical(FfprobeResult probe, ValidSourceMedia source, Guid projectId)
    {
        var audio = probe.Streams.FirstOrDefault(s => string.Equals(s.CodecType, "audio", StringComparison.OrdinalIgnoreCase));
        if (audio is null)
        {
            throw new ErrorCodeException(ErrorCodes.MediaCorrupt, $"Canonical audio for project '{projectId}' has no audio stream.");
        }

        if (audio.SampleRate != 48000)
        {
            throw new ErrorCodeException(
                ErrorCodes.PipelineInvariantViolation,
                $"Canonical audio sample rate is {audio.SampleRate?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"}Hz; expected 48000Hz.");
        }

        var channels = audio.Channels ?? 0;
        if (channels != source.Asset.Channels)
        {
            throw new ErrorCodeException(
                ErrorCodes.PipelineInvariantViolation,
                $"Canonical audio has {channels} channels; source layout has {source.Asset.Channels} (layout must be preserved).");
        }

        if (!string.IsNullOrWhiteSpace(audio.ChannelLayout)
            && !string.IsNullOrWhiteSpace(source.Asset.ChannelLayout)
            && !string.Equals(audio.ChannelLayout.Trim(), source.Asset.ChannelLayout.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            throw new ErrorCodeException(
                ErrorCodes.PipelineInvariantViolation,
                $"Canonical audio layout '{audio.ChannelLayout}' does not preserve source layout '{source.Asset.ChannelLayout}'.");
        }

        var drift = Math.Abs(probe.DurationMs - source.Asset.DurationMs);
        if (drift > DurationToleranceMs)
        {
            throw new ErrorCodeException(
                ErrorCodes.PipelineInvariantViolation,
                $"Canonical audio duration {probe.DurationMs}ms differs from source {source.Asset.DurationMs}ms by {drift}ms (tolerance {DurationToleranceMs}ms).");
        }
    }

    private async Task<FfprobeResult> ProbeLocalAsync(string destPath, CancellationToken cancellationToken)
    {
        try
        {
            return await _ffprobe.ProbeAsync(destPath, cancellationToken).ConfigureAwait(false);
        }
        catch (ErrorCodeException ex) when (string.Equals(ex.ErrorCode, ErrorCodes.MediaCorrupt, StringComparison.Ordinal))
        {
            throw new ErrorCodeException(ErrorCodes.MediaCorrupt, "Canonical audio is corrupt or undecodable.", ex);
        }
    }

    private async Task DownloadToFileAsync(string storageKey, string destPath, CancellationToken cancellationToken)
    {
        Stream download;
        try
        {
            download = await _storage.DownloadAsync(storageKey, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is DomainException || ex is AppException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new ErrorCodeException(ErrorCodes.StorageUnavailable, "Storage is temporarily unavailable; retry the operation.", ex);
        }

        using (download)
        {
            using var dest = new FileStream(destPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            try
            {
                await download.CopyToAsync(dest, cancellationToken).ConfigureAwait(false);
                await dest.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (IOException)
            {
                throw;
            }
            catch (Exception ex) when (ex is not DomainException && ex is not AppException)
            {
                throw new ErrorCodeException(ErrorCodes.StorageUnavailable, "Source download failed; retry the operation.", ex);
            }
        }
    }

    internal static string ExtensionFor(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return ".bin";
        }

        var extension = Path.GetExtension(fileName.Trim()).Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(extension) || extension.Length > 16)
        {
            return ".bin";
        }

        for (var i = 1; i < extension.Length; i++)
        {
            var c = extension[i];
            if (!((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')))
            {
                return ".bin";
            }
        }

        return extension;
    }

    private static string CreateTempWorkingDir()
    {
        // Mirrors ProcessRunner.CreateTempWorkingDir naming (Path.GetTempPath()/dubbing-*)
        // without referencing Infrastructure (Application never references Infrastructure).
        var dir = Path.Combine(Path.GetTempPath(), string.Concat("dubbing-", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void DeleteWorkDirQuietly(string workDir)
    {
        try
        {
            if (Directory.Exists(workDir))
            {
                Directory.Delete(workDir, recursive: true);
            }
        }
        catch (Exception)
        {
            // Best effort; OS temp cleaners cover leftovers.
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
