using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DubbingPlatform.Application.MultiTenancy;
using DubbingPlatform.Application.Options;
using DubbingPlatform.Application.Security;
using DubbingPlatform.Application.Services;
using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Domain.Enums;
using DubbingPlatform.Domain.Exceptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DubbingPlatform.Application.Previews;

/// <summary>
/// Outcome of one preview generation pass for a run.
/// <see cref="PreviewDegraded"/> is true when generation failed but the
/// parent stage still completed; <see cref="PeaksMissing"/> is true when no
/// usable waveform peaks exist (source without an audio track, or a failed
/// pass). Artifact ids are tenant-scoped preview rows that never reuse
/// final-audio artifact ids.
/// </summary>
public sealed record MediaPreviewResult(
    bool PreviewDegraded,
    Guid? MediaPreviewAudioArtifactId,
    Guid? WaveformPeaksArtifactId,
    Guid? VideoPreviewArtifactId,
    bool PeaksMissing);

/// <summary>
/// Preview-lane generation hooked into analysis/audio-prep completion.
/// Produces lightweight <c>ContentObject</c>-backed artifacts per accepted
/// media: <c>MediaPreviewAudio</c> (short playable WAV proxy, capped length),
/// <c>WaveformPeaks</c> (deterministic multi-resolution JSON holding 64, 256,
/// and 1024 peaks derived from the source content hash), and
/// <c>VideoPreview</c> (reservation manifest, only when the source has a
/// video track and the plan flag <c>preview.video.enabled</c> — config key
/// <c>Preview:VideoPreviewEnabled</c> — is true). Reruns are idempotent:
/// existing preview artifacts for the run are reused unless the source
/// content hash changed. Media without an audio track skips the audio proxy
/// and stores an empty-flagged peaks artifact (<c>peaksMissing</c>); the
/// parent stage still completes. Failures are logged with the correlation id
/// and surface as <c>PreviewDegraded = true</c> — generation never throws
/// (except on cancellation) and never fails the parent stage. Only ids,
/// durations, counts, and hashes are logged or persisted in metadata — never
/// media bytes or secrets.
/// </summary>
public sealed class MediaPreviewGenerator
{
    /// <summary>Peak resolutions always present in a usable peaks payload.</summary>
    public static readonly int[] PeakResolutions = [64, 256, 1024];

    /// <summary>Maximum preview audio proxy length (ms).</summary>
    public const int PreviewAudioCapMs = 10000;

    /// <summary>Audit action appended per generation pass.</summary>
    public const string AuditAction = "media.preview_generated";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IStageExecutionContextFactory _contextFactory;
    private readonly ArtifactService _artifacts;
    private readonly AuditService _audit;
    private readonly PreviewOptions _preview;
    private readonly ILogger<MediaPreviewGenerator> _logger;

    public MediaPreviewGenerator(
        IStageExecutionContextFactory contextFactory,
        ArtifactService artifacts,
        AuditService audit,
        IOptions<PreviewOptions> previewOptions,
        ILogger<MediaPreviewGenerator> logger)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        ArgumentNullException.ThrowIfNull(artifacts);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(previewOptions);
        ArgumentNullException.ThrowIfNull(logger);
        _contextFactory = contextFactory;
        _artifacts = artifacts;
        _audit = audit;
        _preview = previewOptions.Value;
        _logger = logger;
    }

    /// <summary>
    /// Whether the codec denotes a present audio track. Pure. Empty or
    /// <c>none</c> (case-insensitive) means the media has no audio track.
    /// </summary>
    public static bool HasAudioTrack(string? audioCodec)
    {
        return !string.IsNullOrWhiteSpace(audioCodec)
            && !string.Equals(audioCodec.Trim(), "none", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether the codec denotes a present video track. Pure.
    /// </summary>
    public static bool HasVideoTrack(string? videoCodec)
    {
        return !string.IsNullOrWhiteSpace(videoCodec);
    }

    /// <summary>
    /// Builds the deterministic waveform-peaks JSON for a source content
    /// hash. Pure. Always contains the 64/256/1024 resolutions; values are
    /// hash-seeded pseudo-peaks in 0..1 rounded to 4 decimals, so reruns for
    /// identical content produce identical bytes (dedup-friendly).
    /// </summary>
    public static string BuildWaveformPeaksJson(string contentHash, int durationMs, int sampleRate)
    {
        if (string.IsNullOrWhiteSpace(contentHash))
        {
            throw new DomainException("ContentHash must not be empty.");
        }

        if (durationMs < 0)
        {
            throw new DomainException("DurationMs must be >= 0.");
        }

        if (sampleRate <= 0)
        {
            throw new DomainException("SampleRate must be > 0.");
        }

        var resolutions = new Dictionary<string, double[]>(StringComparer.Ordinal);
        foreach (var count in PeakResolutions)
        {
            resolutions[count.ToString(System.Globalization.CultureInfo.InvariantCulture)] = BuildPeaks(contentHash, count);
        }

        return JsonSerializer.Serialize(new
        {
            durationMs,
            sampleRate,
            resolutions,
        }, JsonOptions);
    }

    /// <summary>
    /// Builds the empty-flagged peaks JSON for media without an audio track.
    /// Pure. Resolutions are present but empty; consumers check
    /// <c>peaksMissing</c>.
    /// </summary>
    public static string BuildEmptyPeaksJson(int durationMs, int sampleRate)
    {
        if (durationMs < 0)
        {
            throw new DomainException("DurationMs must be >= 0.");
        }

        if (sampleRate <= 0)
        {
            throw new DomainException("SampleRate must be > 0.");
        }

        var resolutions = new Dictionary<string, double[]>(StringComparer.Ordinal);
        foreach (var count in PeakResolutions)
        {
            resolutions[count.ToString(System.Globalization.CultureInfo.InvariantCulture)] = [];
        }

        return JsonSerializer.Serialize(new
        {
            durationMs,
            sampleRate,
            peaksMissing = true,
            resolutions,
        }, JsonOptions);
    }

    /// <summary>
    /// Generates (or reuses) preview artifacts for one run. Never throws
    /// except on cancellation; failures yield <c>PreviewDegraded = true</c>.
    /// </summary>
    public async Task<MediaPreviewResult> GenerateForRunAsync(
        Guid tenantId,
        Guid projectId,
        Guid runId,
        string? correlationId = null,
        CancellationToken cancellationToken = default)
    {
        RequireTenant(tenantId);
        RequireId(projectId, nameof(projectId));
        RequireId(runId, nameof(runId));
        var correlation = string.IsNullOrWhiteSpace(correlationId) ? runId.ToString("N") : correlationId.Trim();

        try
        {
            return await GenerateCoreAsync(tenantId, projectId, runId, correlation, cancellationToken).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Preview-lane contract: generation failures degrade (never fail the parent stage); only ids are logged.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            _logger.LogWarning(
                ex,
                "Preview generation degraded for run {RunId} (correlation {CorrelationId}): {Error}.",
                runId, correlation, ex.Message);
            await AuditAsync(tenantId, projectId, runId, true, null, null, null, true, cancellationToken).ConfigureAwait(false);
            return new MediaPreviewResult(true, null, null, null, true);
        }
    }

    private async Task<MediaPreviewResult> GenerateCoreAsync(
        Guid tenantId,
        Guid projectId,
        Guid runId,
        string correlation,
        CancellationToken cancellationToken)
    {
        DubbingProject? project;
        MediaAsset? asset;
        ContentObject? content;
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            project = await db.Set<DubbingProject>()
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken)
                .ConfigureAwait(false);
            if (project is null || project.TenantId != tenantId)
            {
                throw new DomainException($"Project '{projectId:D}' was not found for preview generation.");
            }

            if (!project.SourceMediaAssetId.HasValue)
            {
                throw new DomainException($"Project '{projectId:D}' has no source media for preview generation.");
            }

            asset = await db.Set<MediaAsset>()
                .AsNoTracking()
                .FirstOrDefaultAsync(m => m.Id == project.SourceMediaAssetId.Value, cancellationToken)
                .ConfigureAwait(false);
            if (asset is null)
            {
                throw new DomainException($"Source media for project '{projectId:D}' was not found for preview generation.");
            }

            content = await db.Set<ContentObject>()
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == asset.ContentObjectId, cancellationToken)
                .ConfigureAwait(false);
            if (content is null)
            {
                throw new DomainException($"Source content for project '{projectId:D}' was not found for preview generation.");
            }
        }

        var hasAudio = HasAudioTrack(asset.AudioCodec);
        var sourceHash = content.ContentHash;
        var durationMs = Math.Max(0, asset.DurationMs);
        var sampleRate = asset.SampleRate > 0 ? asset.SampleRate : 8000;

        var existing = await LoadExistingAsync(tenantId, runId, cancellationToken).ConfigureAwait(false);
        Guid? audioId = null;
        Guid? peaksId = null;
        Guid? videoId = null;

        if (hasAudio)
        {
            var previewDuration = Math.Min(durationMs == 0 ? PreviewAudioCapMs : durationMs, PreviewAudioCapMs);
            var audioBytes = PreviewAudio.BuildWav(previewDuration);
            audioId = await EnsureArtifactAsync(
                tenantId, projectId, runId, existing, ArtifactType.MediaPreviewAudio,
                StageType.AudioPreparation, audioBytes, ".wav", "audio/wav",
                JsonSerializer.Serialize(new
                {
                    durationMs = previewDuration,
                    sampleRate = PreviewAudio.SampleRateHz,
                    sourceMediaId = asset.Id.ToString("N"),
                }, JsonOptions),
                sourceHash, cancellationToken).ConfigureAwait(false);

            var peaksJson = BuildWaveformPeaksJson(sourceHash, durationMs, sampleRate);
            var peaksBytes = Encoding.UTF8.GetBytes(peaksJson);
            peaksId = await EnsureArtifactAsync(
                tenantId, projectId, runId, existing, ArtifactType.WaveformPeaks,
                StageType.MediaAnalysis, peaksBytes, ".json", "application/json",
                JsonSerializer.Serialize(new
                {
                    durationMs,
                    sampleRate,
                    resolutions = PeakResolutions,
                    sourceMediaId = asset.Id.ToString("N"),
                }, JsonOptions),
                sourceHash, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var emptyJson = BuildEmptyPeaksJson(durationMs, sampleRate);
            var emptyBytes = Encoding.UTF8.GetBytes(emptyJson);
            peaksId = await EnsureArtifactAsync(
                tenantId, projectId, runId, existing, ArtifactType.WaveformPeaks,
                StageType.MediaAnalysis, emptyBytes, ".json", "application/json",
                JsonSerializer.Serialize(new
                {
                    durationMs,
                    sampleRate,
                    resolutions = PeakResolutions,
                    sourceMediaId = asset.Id.ToString("N"),
                    peaksMissing = true,
                }, JsonOptions),
                sourceHash, cancellationToken).ConfigureAwait(false);
        }

        if (_preview.VideoPreviewEnabled && HasVideoTrack(asset.VideoCodec))
        {
            var manifest = JsonSerializer.Serialize(new
            {
                sourceMediaId = asset.Id.ToString("N"),
                durationMs,
                target = "low-res proxy",
                videoCodec = asset.VideoCodec,
            }, JsonOptions);
            var manifestBytes = Encoding.UTF8.GetBytes(manifest);
            videoId = await EnsureArtifactAsync(
                tenantId, projectId, runId, existing, ArtifactType.VideoPreview,
                StageType.MediaAnalysis, manifestBytes, ".json", "application/json",
                JsonSerializer.Serialize(new
                {
                    durationMs,
                    sampleRate,
                    sourceMediaId = asset.Id.ToString("N"),
                }, JsonOptions),
                sourceHash, cancellationToken).ConfigureAwait(false);
        }

        var peaksMissing = !hasAudio;
        await AuditAsync(tenantId, projectId, runId, false, audioId, peaksId, videoId, peaksMissing, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Preview generation complete for run {RunId} (correlation {CorrelationId}): audio {AudioId}, peaks {PeaksId}, video {VideoId}, peaksMissing {PeaksMissing}.",
            runId, correlation,
            audioId?.ToString("N") ?? "skipped",
            peaksId?.ToString("N") ?? "missing",
            videoId?.ToString("N") ?? "skipped",
            peaksMissing);
        return new MediaPreviewResult(false, audioId, peaksId, videoId, peaksMissing);
    }

    private async Task<Guid> EnsureArtifactAsync(
        Guid tenantId,
        Guid projectId,
        Guid runId,
        IReadOnlyDictionary<ArtifactType, (Guid ArtifactId, string? SourceHash)> existing,
        ArtifactType type,
        StageType stage,
        byte[] bytes,
        string extension,
        string contentType,
        string metadataJson,
        string sourceHash,
        CancellationToken cancellationToken)
    {
        if (existing.TryGetValue(type, out var reuse)
            && string.Equals(reuse.SourceHash, sourceHash, StringComparison.Ordinal))
        {
            return reuse.ArtifactId;
        }

        PublishResult published;
        using (var stream = new MemoryStream(bytes, writable: false))
        {
            published = await _artifacts.PublishAsync(
                tenantId, projectId, runId, stage, type,
                stream, extension, contentType,
                "preview", "preview-v1", null, null,
                [],
                null,
                cancellationToken).ConfigureAwait(false);
        }

        var stamped = JsonSerializer.Serialize(new
        {
            metadata = JsonSerializer.Deserialize<JsonElement>(metadataJson, JsonOptions),
            sourceContentHash = sourceHash,
        }, JsonOptions);

        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            await db.Database.ExecuteSqlRawAsync(
                "UPDATE artifacts SET metadata_json = {0} WHERE id = {1} AND tenant_id = {2}",
                stamped, published.ArtifactId, tenantId).ConfigureAwait(false);
        }

        return published.ArtifactId;
    }

    private async Task<Dictionary<ArtifactType, (Guid ArtifactId, string? SourceHash)>> LoadExistingAsync(
        Guid tenantId,
        Guid runId,
        CancellationToken cancellationToken)
    {
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            var rows = await db.Set<Artifact>()
                .AsNoTracking()
                .Where(a => a.ProcessingRunId == runId
                    && (a.Type == ArtifactType.MediaPreviewAudio
                        || a.Type == ArtifactType.WaveformPeaks
                        || a.Type == ArtifactType.VideoPreview))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            var result = new Dictionary<ArtifactType, (Guid, string?)>();
            foreach (var row in rows)
            {
                string? sourceHash = null;
                if (!string.IsNullOrWhiteSpace(row.MetadataJson))
                {
                    try
                    {
                        var parsed = JsonSerializer.Deserialize<JsonElement>(row.MetadataJson, JsonOptions);
                        if (parsed.ValueKind == JsonValueKind.Object
                            && parsed.TryGetProperty("sourceContentHash", out var hash)
                            && hash.ValueKind == JsonValueKind.String)
                        {
                            sourceHash = hash.GetString();
                        }
                    }
#pragma warning disable CA1031 // Metadata is best-effort: unparsable rows are treated as stale and regenerated.
                    catch (JsonException)
#pragma warning restore CA1031
                    {
                        sourceHash = null;
                    }
                }

                result[row.Type] = (row.Id, sourceHash);
            }

            return result;
        }
    }

    private Task AuditAsync(
        Guid tenantId,
        Guid projectId,
        Guid runId,
        bool degraded,
        Guid? audioId,
        Guid? peaksId,
        Guid? videoId,
        bool peaksMissing,
        CancellationToken cancellationToken)
    {
        return _audit.LogAsync(
            tenantId, projectId, "media-preview-generator", AuditAction,
            "ProcessingRun", runId.ToString("N"),
            SecretRedactor.Redact(JsonSerializer.Serialize(new
            {
                runId = runId.ToString("N"),
                previewDegraded = degraded,
                audioArtifactId = audioId?.ToString("N"),
                peaksArtifactId = peaksId?.ToString("N"),
                videoArtifactId = videoId?.ToString("N"),
                peaksMissing,
            }, JsonOptions)),
            cancellationToken);
    }

    private static double[] BuildPeaks(string contentHash, int count)
    {
        var seedBytes = SHA256.HashData(Encoding.UTF8.GetBytes(string.Concat(contentHash, ":", count.ToString(System.Globalization.CultureInfo.InvariantCulture))));
        var seed = BitConverter.ToInt32(seedBytes, 0);
        var random = new Random(seed);
        var peaks = new double[count];
        for (var i = 0; i < count; i++)
        {
            peaks[i] = Math.Round(random.NextDouble(), 4);
        }

        return peaks;
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
            throw new DomainException(string.Concat(name, " must not be empty."));
        }
    }
}
