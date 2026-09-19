using System.Globalization;
using System.Text;
using System.Text.Json;
using DubbingPlatform.Application.Errors;
using DubbingPlatform.Application.Exceptions;
using DubbingPlatform.Application.MultiTenancy;
using DubbingPlatform.Application.Options;
using DubbingPlatform.Application.Services;
using DubbingPlatform.Contracts.Messages;
using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Domain.Enums;
using DubbingPlatform.Domain.Exceptions;
using DubbingPlatform.Infrastructure.Media;
using DubbingPlatform.Infrastructure.Messaging;
using DubbingPlatform.Infrastructure.Observability;
using DubbingPlatform.Infrastructure.Processes;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DubbingPlatform.Workers.Consumers;

/// <summary>
/// Project-scoped (single unit) audio mixing on <c>media.render</c> (the workload
/// queue for AudioMixing per <c>WorkQueueRouter</c>; the task text names scope
/// <c>Run</c>, but <c>StageGraph</c> scopes AudioMixing as <c>Project</c> with
/// <c>DispatchSingleWorkAsync</c> emitting <c>(Project, projectId D)</c> — code
/// truth wins, as in Tasks 022/025/030 — so this worker strictly requires
/// <c>ScopeType.Project</c>). Claims via <see cref="BaseConsumer{TMessage}"/>
/// (at-least-once, lease fenced), ignores other stages sharing
/// <c>media.render</c> before claiming, stages the timeline artifact plus the
/// selected dialogue audios plus the optional background stem to an isolated
/// temp dir, calls <see cref="FFmpegMixer"/> (source-start placement, silence
/// preserved, intentional overlaps via <c>amix</c>, background ducked with
/// <c>sidechaincompress+volume</c>, two-pass <c>loudnorm</c> to
/// <c>-16|-23/-1</c>, always 48kHz stereo — layout is normalized to stereo, the
/// deterministic superset — exact <c>-filter_complex</c> recorded in artifact
/// metadata), uploads one <c>MixedAudio</c> WAV artifact with parents
/// <c>[timeline + selected audios + background?]</c>, completes the execution,
/// and publishes <c>StageCompleted</c> (the saga then dispatches
/// QualityControl; that <c>StageCompleted</c> is the task's "QC request" — QC
/// itself stays Task 032). Loudness-measure failures and out-of-tolerance mixes
/// (after the mixer's internal retry) route to <c>MIX_QUALITY</c> review
/// (<c>ReviewItem</c> + <c>ManualReviewRequired</c> + <c>StageReviewRequired</c>);
/// persistent clipping fails with <c>QC_BLOCKED</c>; disk/timeout fails with
/// <c>RESOURCE_EXHAUSTED</c> and no commit. Barrier accounting stays
/// saga-owned. Never logs audio or secrets: only ids, counts, durations, and levels.
/// </summary>
public sealed class AudioMixerWorker : BaseConsumer<StageWorkRequested>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IStageExecutionContextFactory _contextFactory;
    private readonly IOptions<RetryOptions> _retryOptions;
    private readonly FFmpegMixer _mixer;
    private readonly ArtifactService _artifacts;
    private readonly DubbingPlatform.Application.Abstractions.IArtifactStorage _storage;
    private readonly MixingOptions _mixing;
    private readonly ILogger<AudioMixerWorker> _logger;

    public AudioMixerWorker(
        IStageExecutionContextFactory contextFactory,
        IOptions<RetryOptions> retryOptions,
        FFmpegMixer mixer,
        ArtifactService artifacts,
        DubbingPlatform.Application.Abstractions.IArtifactStorage storage,
        IOptions<MixingOptions> mixing,
        ILogger<AudioMixerWorker> logger,
        IDeferredSender? deferredSender = null)
        : base(contextFactory, retryOptions, deferredSender)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        ArgumentNullException.ThrowIfNull(retryOptions);
        ArgumentNullException.ThrowIfNull(mixer);
        ArgumentNullException.ThrowIfNull(artifacts);
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(mixing);
        ArgumentNullException.ThrowIfNull(logger);
        _contextFactory = contextFactory;
        _retryOptions = retryOptions;
        _mixer = mixer;
        _artifacts = artifacts;
        _storage = storage;
        _mixing = mixing.Value;
        _logger = logger;
    }

    protected override bool ShouldProcess(StageWorkRequested message)
    {
        var stage = message.StageTypeRequired ?? message.StageType;
        return string.Equals(stage?.Trim(), nameof(StageType.AudioMixing), StringComparison.Ordinal);
    }

    protected override async Task HandleAsync(
        ConsumeContext<StageWorkRequested> context,
        StageExecution? execution,
        CancellationToken cancellationToken)
    {
        if (execution is null)
        {
            return;
        }

        if (execution.Status is StageStatus.Completed or StageStatus.Skipped or StageStatus.ManualReviewRequired or StageStatus.Failed)
        {
            return;
        }

        try
        {
            if (execution.StageType != StageType.AudioMixing)
            {
                throw new DomainException($"Stage execution '{execution.Id}' is '{execution.StageType}', not AudioMixing.");
            }

            if (execution.ScopeType != ScopeType.Project)
            {
                throw new DomainException($"Stage execution '{execution.Id}' is '{execution.ScopeType}', not Project, for AudioMixing.");
            }

            await EnsureLeaseRunningAsync(execution, cancellationToken).ConfigureAwait(false);

            var result = await MixRunAsync(execution, cancellationToken).ConfigureAwait(false);

            await EnsureLeaseRunningAsync(execution, cancellationToken).ConfigureAwait(false);

            var stages = new StageExecutionService(_contextFactory, _retryOptions);
            await stages.CompleteAsync(
                execution.TenantId, execution.Id,
                execution.LeaseOwner, execution.LeaseToken,
                [result.ArtifactId.ToString("N")],
                cancellationToken).ConfigureAwait(false);

            var inbound = context.Message;
            var correlationId = string.IsNullOrWhiteSpace(inbound.CorrelationId)
                ? context.CorrelationId?.ToString() ?? Guid.NewGuid().ToString("N")
                : inbound.CorrelationId;
            await context.Publish(new StageCompleted(
                Guid.NewGuid(), correlationId,
                execution.TenantId, execution.ProjectId, execution.ProcessingRunId,
                execution.Id, execution.StageType.ToString(), execution.ScopeType.ToString(), execution.ScopeId,
                execution.SegmentId, MessageVersionPolicy.CurrentVersion, DateTimeOffset.UtcNow, execution.Attempt,
                execution.InputHash, execution.ConfigurationHash, execution.ExecutionSnapshotHash,
                nameof(StageType.AudioMixing),
                [result.ArtifactId.ToString("N")]), cancellationToken).ConfigureAwait(false);
        }
        catch (LeaseLostException)
        {
            throw;
        }
        catch (ErrorCodeException ex) when (string.Equals(ex.ErrorCode, ErrorCodes.ProviderInvalidResponse, StringComparison.Ordinal))
        {
            // Loudness-measure failure or out-of-tolerance mix (mixer already
            // retried once): route to MIX_QUALITY review, never fail the run.
            _logger.LogInformation(
                "Audio mixing for run {RunId} routed to review ({Code}).",
                execution.ProcessingRunId, ex.ErrorCode);
            await PublishReviewAsync(context, execution, ex.Message, cancellationToken).ConfigureAwait(false);
            return;
        }
#pragma warning disable CA1031 // Worker poison contract: MIX_QUALITY handled above; other permanent failures go via TryFailAsync; transient rethrows.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogWarning(
                "Audio mixing failed for run {RunId}: {Error}.",
                execution.ProcessingRunId, ex.Message);
            if (!await StageWorkerFailure.TryFailAsync(
                _contextFactory, _retryOptions, context, execution, ex,
                nameof(StageType.AudioMixing), cancellationToken).ConfigureAwait(false))
            {
                throw;
            }
        }
    }

    private sealed record MixRunOutcome(Guid ArtifactId, int EntryCount, double Integrated, double TruePeak);

    private async Task<MixRunOutcome> MixRunAsync(StageExecution execution, CancellationToken cancellationToken)
    {
        var tenantId = execution.TenantId;
        var projectId = execution.ProjectId;
        var runId = execution.ProcessingRunId;

        var project = await LoadOwnedProjectAsync(tenantId, projectId, cancellationToken).ConfigureAwait(false);
        await EnsureRunActiveAsync(tenantId, runId, projectId, cancellationToken).ConfigureAwait(false);

        var effectiveProfile = FFmpegMixer.HasLoudnessProfile(project.SettingsJson)
            ? FFmpegMixer.ParseLoudnessProfile(project.SettingsJson)
            : ((string.IsNullOrWhiteSpace(_mixing.Profile) ? "web" : _mixing.Profile.Trim().ToLowerInvariant()));

        var (timelineId, timelineJson) = await LoadTimelineAsync(tenantId, runId, cancellationToken).ConfigureAwait(false);
        var timeline = FFmpegMixer.ParseTimeline(timelineJson);

        string workDir;
        try
        {
            workDir = ProcessRunner.CreateTempWorkingDir();
        }
        catch (IOException ex)
        {
            throw new ErrorCodeException(ErrorCodes.ResourceExhausted, "Mixing cannot create temp dir; disk may be full.", ex);
        }

        try
        {
            var dialogueFiles = new List<string>(timeline.Entries.Count);
            var dialogueArtifactIds = new List<Guid>(timeline.Entries.Count);
            for (var i = 0; i < timeline.Entries.Count; i++)
            {
                var entry = timeline.Entries[i];
                if (!Guid.TryParse(entry.AudioArtifactId, out var audioId) || audioId == Guid.Empty)
                {
                    throw new ErrorCodeException(ErrorCodes.ArtifactUnavailable, $"Selected audio for timeline entry '{entry.SegmentId}' is unavailable.");
                }

                dialogueArtifactIds.Add(audioId);
                var local = Path.Combine(workDir, string.Concat("dlg-", i.ToString(CultureInfo.InvariantCulture), "-", audioId.ToString("N"), ".wav"));
                await DownloadArtifactAsync(tenantId, audioId, runId, local, cancellationToken).ConfigureAwait(false);
                dialogueFiles.Add(local);
            }

            string? backgroundFile = null;
            Guid? backgroundArtifactId = null;
            if (!string.IsNullOrWhiteSpace(timeline.BackgroundArtifactId)
                && Guid.TryParse(timeline.BackgroundArtifactId, out var bgId)
                && bgId != Guid.Empty)
            {
                backgroundArtifactId = bgId;
                backgroundFile = Path.Combine(workDir, string.Concat("bg-", bgId.ToString("N"), ".flac"));
                await DownloadArtifactAsync(tenantId, bgId, runId, backgroundFile, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                _logger.LogWarning(
                    "Audio mixing for run {RunId}: no background stem; mixing dialogue only.",
                    runId);
            }

            var outputWav = Path.Combine(workDir, "mixed.wav");
            await EnsureLeaseRunningAsync(execution, cancellationToken).ConfigureAwait(false);

            var mixed = await _mixer.MixAsync(
                timelineJson, dialogueFiles, backgroundFile, outputWav, effectiveProfile, cancellationToken).ConfigureAwait(false);

            await EnsureLeaseRunningAsync(execution, cancellationToken).ConfigureAwait(false);

            var parents = new List<Guid> { timelineId };
            parents.AddRange(dialogueArtifactIds);
            if (backgroundArtifactId.HasValue)
            {
                parents.Add(backgroundArtifactId.Value);
            }

            PublishResult published;
            using (var stream = new FileStream(outputWav, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                published = await _artifacts.PublishAsync(
                    tenantId, projectId, runId,
                    StageType.AudioMixing, ArtifactType.MixedAudio,
                    stream, ".wav", "audio/wav",
                    null, null, null, null,
                    parents.Distinct().ToList(),
                    execution.Id,
                    cancellationToken).ConfigureAwait(false);
            }

            var metadataJson = JsonSerializer.Serialize(new
            {
                schemaVersion = "1",
                runId = runId.ToString("N"),
                profile = mixed.Profile,
                targetLufs = mixed.TargetLufs,
                integratedLufs = mixed.IntegratedLufs,
                truePeakDbtp = mixed.TruePeakDbtp,
                durationMs = mixed.DurationMs,
                sampleRate = mixed.SampleRate,
                channels = mixed.Channels,
                duckingApplied = mixed.DuckingApplied,
                backgroundIncluded = mixed.BackgroundIncluded,
                entryCount = timeline.Entries.Count,
                sourceDurationMs = timeline.SourceDurationMs,
                timelineArtifactId = timelineId.ToString("N"),
                filterComplex = mixed.FilterComplex,
                premixFilter = mixed.PremixFilter,
                finalFilter = mixed.FinalFilter,
            }, JsonOptions);

            using (TenantContext.BeginScope(tenantId))
            {
                using var db = _contextFactory.CreateDbContext();
                await db.Database.ExecuteSqlRawAsync(
                    "UPDATE artifacts SET metadata_json = {0} WHERE id = {1} AND tenant_id = {2}",
                    metadataJson, published.ArtifactId, tenantId).ConfigureAwait(false);
            }

            _logger.LogInformation(
                "Audio mixed for run {RunId}: artifact {ArtifactId} ({Entries} entries, {Integrated} LUFS, peak {Peak} dBTP).",
                runId, published.ArtifactId, timeline.Entries.Count,
                mixed.IntegratedLufs.ToString("F2", CultureInfo.InvariantCulture),
                mixed.TruePeakDbtp.ToString("F2", CultureInfo.InvariantCulture));

            return new MixRunOutcome(published.ArtifactId, timeline.Entries.Count, mixed.IntegratedLufs, mixed.TruePeakDbtp);
        }
        finally
        {
            DeleteDirQuietly(workDir);
        }
    }

    private async Task PublishReviewAsync(
        ConsumeContext<StageWorkRequested> context,
        StageExecution execution,
        string detail,
        CancellationToken cancellationToken)
    {
        var reviewId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var payload = JsonSerializer.Serialize(new
        {
            reason = FFmpegMixer.ReviewReason,
            runId = execution.ProcessingRunId.ToString("N"),
            detail = string.IsNullOrWhiteSpace(detail) ? "Mix loudness verification failed." : detail.Trim(),
        }, JsonOptions);

        using (TenantContext.BeginScope(execution.TenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            db.Set<ReviewItem>().Add(new ReviewItem(
                reviewId, execution.TenantId, execution.ProjectId, execution.ProcessingRunId,
                ScopeType.Project, execution.ProjectId.ToString("D"), null,
                ReviewStatus.Open, FFmpegMixer.ReviewReason, payload, now, now, null));
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            PlatformMetrics.ReviewOpened(execution.TenantId);
        }

        var stages = new StageExecutionService(_contextFactory, _retryOptions);
        await stages.MarkReviewRequiredAsync(
            execution.TenantId, execution.Id,
            execution.LeaseOwner, execution.LeaseToken,
            cancellationToken).ConfigureAwait(false);

        var inbound = context.Message;
        var correlationId = string.IsNullOrWhiteSpace(inbound.CorrelationId)
            ? context.CorrelationId?.ToString() ?? Guid.NewGuid().ToString("N")
            : inbound.CorrelationId;
        await context.Publish(new StageReviewRequired(
            Guid.NewGuid(), correlationId,
            execution.TenantId, execution.ProjectId, execution.ProcessingRunId,
            execution.Id, execution.StageType.ToString(), execution.ScopeType.ToString(), execution.ScopeId,
            execution.SegmentId, MessageVersionPolicy.CurrentVersion, DateTimeOffset.UtcNow, execution.Attempt,
            execution.InputHash, execution.ConfigurationHash, execution.ExecutionSnapshotHash,
            nameof(StageType.AudioMixing),
            reviewId.ToString("N")), cancellationToken).ConfigureAwait(false);
    }

    private async Task<(Guid ArtifactId, string Json)> LoadTimelineAsync(
        Guid tenantId,
        Guid runId,
        CancellationToken cancellationToken)
    {
        Guid artifactId;
        string storageKey;
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            var row = await db.Set<Artifact>()
                .AsNoTracking()
                .Where(a => a.ProcessingRunId == runId && a.Type == ArtifactType.Timeline)
                .OrderByDescending(a => a.CreatedAt)
                .Select(a => new { a.Id, a.ContentObjectId })
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (row is null)
            {
                throw new ErrorCodeException(ErrorCodes.ArtifactUnavailable, "Timeline artifact for mixing is unavailable.");
            }

            artifactId = row.Id;
            var content = await db.Set<ContentObject>()
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == row.ContentObjectId, cancellationToken).ConfigureAwait(false);
            if (content is null)
            {
                throw new ErrorCodeException(ErrorCodes.ArtifactUnavailable, "Timeline artifact content is unavailable.");
            }

            storageKey = content.StorageKey;
        }

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
            using var reader = new StreamReader(download, Encoding.UTF8);
            var json = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(json))
            {
                throw new ErrorCodeException(ErrorCodes.ArtifactUnavailable, "Timeline artifact is empty.");
            }

            return (artifactId, json);
        }
    }

    private async Task DownloadArtifactAsync(
        Guid tenantId,
        Guid artifactId,
        Guid runId,
        string destPath,
        CancellationToken cancellationToken)
    {
        string storageKey;
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            var artifact = await db.Set<Artifact>()
                .AsNoTracking()
                .FirstOrDefaultAsync(a => a.Id == artifactId, cancellationToken).ConfigureAwait(false);
            if (artifact is null || artifact.TenantId != tenantId || artifact.ProcessingRunId != runId)
            {
                throw new ErrorCodeException(ErrorCodes.ArtifactUnavailable, $"Audio artifact '{artifactId:D}' is unavailable for mixing.");
            }

            var content = await db.Set<ContentObject>()
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == artifact.ContentObjectId, cancellationToken).ConfigureAwait(false);
            if (content is null)
            {
                throw new ErrorCodeException(ErrorCodes.ArtifactUnavailable, $"Audio artifact '{artifactId:D}' content is unavailable.");
            }

            storageKey = content.StorageKey;
        }

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
            using var local = new FileStream(destPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await download.CopyToAsync(local, cancellationToken).ConfigureAwait(false);
            await local.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
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

    private async Task EnsureRunActiveAsync(Guid tenantId, Guid runId, Guid projectId, CancellationToken cancellationToken)
    {
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            var run = await db.Set<ProcessingRun>()
                .AsNoTracking()
                .FirstOrDefaultAsync(r => r.Id == runId, cancellationToken).ConfigureAwait(false);
            if (run is null)
            {
                throw new NotFoundException($"Processing run '{runId}' was not found.");
            }

            if (run.TenantId != tenantId || run.ProjectId != projectId)
            {
                throw new ForbiddenException($"Processing run '{runId}' does not belong to the current tenant/project.");
            }

            if (run.Status is ProcessingRunStatus.Cancelling or ProcessingRunStatus.Cancelled)
            {
                throw new LeaseLostException($"Processing run '{run.Id}' is '{run.Status}'; aborting before commit.");
            }
        }
    }

    private async Task EnsureLeaseRunningAsync(StageExecution execution, CancellationToken cancellationToken)
    {
        using (TenantContext.BeginScope(execution.TenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            var loaded = await db.Set<StageExecution>()
                .AsNoTracking()
                .FirstOrDefaultAsync(e => e.Id == execution.Id, cancellationToken).ConfigureAwait(false);
            if (loaded is null
                || loaded.Status != StageStatus.Running
                || !string.Equals(loaded.LeaseOwner, execution.LeaseOwner, StringComparison.Ordinal)
                || !string.Equals(loaded.LeaseToken, execution.LeaseToken, StringComparison.Ordinal))
            {
                throw new LeaseLostException($"Lease lost for stage execution '{execution.Id}'.");
            }

            var run = await db.Set<ProcessingRun>()
                .AsNoTracking()
                .FirstOrDefaultAsync(r => r.Id == execution.ProcessingRunId, cancellationToken).ConfigureAwait(false);
            if (run is not null
                && run.Status is ProcessingRunStatus.Cancelling or ProcessingRunStatus.Cancelled)
            {
                throw new LeaseLostException($"Processing run '{run.Id}' is '{run.Status}'; aborting before commit.");
            }
        }
    }

    private static void DeleteDirQuietly(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch (Exception)
        {
            // Best effort; OS temp cleaners cover leftovers.
        }
    }
}
