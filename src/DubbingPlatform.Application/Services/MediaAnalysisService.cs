using System.Text.Json;
using DubbingPlatform.Application.Abstractions;
using DubbingPlatform.Application.Errors;
using DubbingPlatform.Application.Exceptions;
using DubbingPlatform.Application.MultiTenancy;
using DubbingPlatform.Application.Options;
using DubbingPlatform.Application.Previews;
using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Domain.Enums;
using DubbingPlatform.Domain.Exceptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DubbingPlatform.Application.Services;

/// <summary>
/// Result of media analysis for one run.
/// </summary>
public sealed record MediaAnalysisResult(
    Guid ArtifactId,
    Guid ContentObjectId,
    string ContentHash,
    int DurationMs,
    int EstimatedSegments,
    bool NeedsSeparation);

/// <summary>
/// Run-scoped media analysis. Verifies the source <c>ContentObject</c> hash by
/// streaming re-hash when <c>Media:VerifyOnUse</c> is true (default), probes
/// the source bytes via <see cref="IFFprobeService"/> (never trusting stored
/// metadata alone), derives the downstream plan, and publishes one
/// <c>FfprobeAnalysis</c> JSON artifact
/// (<c>duration/streams/plan{needsSeparation,estimatedSegments}</c>) with the
/// source artifact as parent. <c>needsSeparation</c> is a deterministic MVP
/// heuristic (multichannel implies a background worth separating; Task 021
/// owns the real separation policy). Corrupt sources fail permanently
/// (<c>MEDIA_CORRUPT</c>, no retry); storage I/O failures propagate as
/// transient.
/// </summary>
public sealed class MediaAnalysisService
{
    private readonly IStageExecutionContextFactory _contextFactory;
    private readonly ArtifactService _artifacts;
    private readonly IFFprobeService _ffprobe;
    private readonly MediaOptions _media;
    private readonly QuotaOptions _quota;
    private readonly MediaPreviewGenerator? _previews;
    private readonly ILogger<MediaAnalysisService> _logger;

    public MediaAnalysisService(
        IStageExecutionContextFactory contextFactory,
        ArtifactService artifacts,
        IFFprobeService ffprobe,
        IOptions<MediaOptions> media,
        IOptions<QuotaOptions> quota,
        ILogger<MediaAnalysisService> logger,
        MediaPreviewGenerator? previewGenerator = null)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        ArgumentNullException.ThrowIfNull(artifacts);
        ArgumentNullException.ThrowIfNull(ffprobe);
        ArgumentNullException.ThrowIfNull(media);
        ArgumentNullException.ThrowIfNull(quota);
        ArgumentNullException.ThrowIfNull(logger);
        _contextFactory = contextFactory;
        _artifacts = artifacts;
        _ffprobe = ffprobe;
        _media = media.Value;
        _quota = quota.Value;
        _previews = previewGenerator;
        _logger = logger;
    }

    public async Task<MediaAnalysisResult> AnalyzeAsync(
        Guid tenantId,
        Guid projectId,
        Guid runId,
        Guid executionId,
        string owner,
        string token,
        CancellationToken cancellationToken = default)
    {
        RequireTenant(tenantId);
        RequireId(projectId, nameof(projectId));
        RequireId(runId, nameof(runId));
        RequireId(executionId, nameof(executionId));
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        var source = await SourceMediaLoader.LoadAsync(_contextFactory, tenantId, projectId, cancellationToken).ConfigureAwait(false);

        if (_media.VerifyOnUse)
        {
            await VerifySourceHashAsync(tenantId, source, cancellationToken).ConfigureAwait(false);
        }

        FfprobeResult probe;
        try
        {
            probe = await _ffprobe.ProbeAsync(source.Content.StorageKey, cancellationToken).ConfigureAwait(false);
        }
        catch (ErrorCodeException ex) when (string.Equals(ex.ErrorCode, ErrorCodes.MediaCorrupt, StringComparison.Ordinal))
        {
            throw new ErrorCodeException(
                ErrorCodes.MediaCorrupt,
                $"Source media for project '{projectId}' is corrupt or undecodable.", ex);
        }

        var audio = probe.Streams.FirstOrDefault(s => string.Equals(s.CodecType, "audio", StringComparison.OrdinalIgnoreCase));
        if (audio is null || !MediaValidator.IsDecodableAudio(audio.Codec))
        {
            throw new ErrorCodeException(
                ErrorCodes.MediaUnsupported,
                $"Source media for project '{projectId}' has no decodable audio stream.");
        }

        var durationMs = (int)Math.Min(Math.Max(probe.DurationMs, 0), int.MaxValue);
        var channels = audio.Channels is > 0 ? audio.Channels.Value : source.Asset.Channels;
        var needsSeparation = channels >= 2;
        var estimatedSegments = (int)Math.Clamp(probe.DurationMs / 5000L, 1L, Math.Max(1, _quota.MaxSegmentCount));

        var analysisJson = JsonSerializer.Serialize(new
        {
            durationMs,
            container = probe.Container,
            audioCodec = audio.Codec,
            sampleRate = audio.SampleRate ?? source.Asset.SampleRate,
            channels,
            channelLayout = audio.ChannelLayout ?? source.Asset.ChannelLayout,
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
            plan = new
            {
                needsSeparation,
                estimatedSegments,
                sourceAssetId = source.Asset.Id.ToString("N"),
                sourceContentHash = source.Content.ContentHash,
            },
        });

        PublishResult published;
        using (var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(analysisJson), writable: false))
        {
            published = await _artifacts.PublishAsync(
                tenantId, projectId, runId,
                StageType.MediaAnalysis, ArtifactType.FfprobeAnalysis,
                stream, ".json", "application/json",
                null, null, null, null,
                source.SourceArtifactId.HasValue ? [source.SourceArtifactId.Value] : [],
                executionId,
                cancellationToken).ConfigureAwait(false);
        }

        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            await db.Database.ExecuteSqlRawAsync(
                "UPDATE artifacts SET metadata_json = {0} WHERE id = {1} AND tenant_id = {2}",
                analysisJson, published.ArtifactId, tenantId).ConfigureAwait(false);
        }

        var stages = new StageExecutionService(_contextFactory, Microsoft.Extensions.Options.Options.Create(new RetryOptions()));
        await stages.CompleteAsync(
            tenantId, executionId, owner, token,
            [published.ArtifactId.ToString("N")],
            cancellationToken).ConfigureAwait(false);

        await TryGeneratePreviewsAsync(tenantId, projectId, runId, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Media analysis complete for run {RunId}: duration {DurationMs}ms, segments ~{Segments}, separation {NeedsSeparation}.",
            runId, durationMs, estimatedSegments, needsSeparation);

        return new MediaAnalysisResult(
            published.ArtifactId, published.ContentObjectId,
            published.ContentHash, durationMs, estimatedSegments, needsSeparation);
    }

    /// <summary>
    /// Preview-lane hook (Task B-004). Runs after the stage commits: the
    /// generator never throws (degraded results are logged, not raised), and
    /// this guard ensures even a contract breach cannot fail analysis.
    /// </summary>
    private async Task TryGeneratePreviewsAsync(
        Guid tenantId,
        Guid projectId,
        Guid runId,
        CancellationToken cancellationToken)
    {
        if (_previews is null)
        {
            return;
        }

        try
        {
            var previews = await _previews.GenerateForRunAsync(
                tenantId, projectId, runId, runId.ToString("N"), cancellationToken).ConfigureAwait(false);
            if (previews.PreviewDegraded)
            {
                _logger.LogWarning("Preview lane degraded for run {RunId}; analysis result unaffected.", runId);
            }
        }
#pragma warning disable CA1031 // Parent-stage guarantee: preview failures must never fail analysis.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            _logger.LogWarning(ex, "Preview lane degraded for run {RunId}; analysis result unaffected.", runId);
        }
    }

    private async Task VerifySourceHashAsync(Guid tenantId, ValidSourceMedia source, CancellationToken cancellationToken)
    {
        if (!source.SourceArtifactId.HasValue)
        {
            throw new ErrorCodeException(
                ErrorCodes.ArtifactUnavailable,
                "Source artifact is missing; cannot verify source integrity.");
        }

        try
        {
            await _artifacts.VerifyIntegrityAsync(tenantId, source.SourceArtifactId.Value, cancellationToken).ConfigureAwait(false);
        }
        catch (ErrorCodeException ex) when (string.Equals(ex.ErrorCode, ErrorCodes.ArtifactChecksumMismatch, StringComparison.Ordinal))
        {
            throw new ErrorCodeException(
                ErrorCodes.ArtifactChecksumMismatch,
                "Source bytes failed integrity revalidation.", ex);
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
