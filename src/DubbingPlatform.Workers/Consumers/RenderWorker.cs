using System.Globalization;
using System.Text.Json;
using DubbingPlatform.Application.Errors;
using DubbingPlatform.Application.Exceptions;
using DubbingPlatform.Application.Enrichment;
using DubbingPlatform.Application.MultiTenancy;
using DubbingPlatform.Application.Options;
using DubbingPlatform.Application.Services;
using DubbingPlatform.Application.StateMachines;
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
/// Project-scoped (single unit) final rendering on <c>media.render</c> (the
/// workload queue for Render per <c>WorkQueueRouter</c>; the task text names
/// scope <c>Run</c>, but <c>StageGraph</c> scopes Render as <c>Project</c> with
/// <c>DispatchSingleWorkAsync</c> emitting <c>(Project, projectId D)</c> — code
/// truth wins, as in Tasks 022/025/030/031/032 — so this worker strictly
/// requires <c>ScopeType.Project</c>). Claims via
/// <see cref="BaseConsumer{TMessage}"/> (at-least-once, lease fenced), ignores
/// other stages sharing <c>media.render</c> before claiming, fails fast with
/// <c>QC_BLOCKED</c> (no FFmpeg call) when any open review exists for the run,
/// renders via <see cref="RenderService"/> to an isolated temp dir, uploads one
/// <c>RenderedOutput</c> artifact with parents <c>[mixed + source?]</c>,
/// registers the <c>OutputAsset</c> row, completes the execution, and publishes
/// <c>StageCompleted</c> plus <c>RunCompleted</c> (the saga's
/// <c>MaybeCompleteRun</c> is an idempotent safety net carrying the same
/// <c>OutputAssetId</c>). Duration violations fail with
/// <c>VALIDATION_FAILED</c> (no <c>OutputAsset</c>); missing media fails with
/// <c>ARTIFACT_UNAVAILABLE</c>; FFmpeg faults fail the run (manual retry owns
/// recovery per Task 035). The exact FFmpeg args plus the copy/re-encode
/// decision are recorded in the <c>RenderedOutput</c> artifact
/// <c>metadata_json</c> (<c>StageExecution</c> has no metadata column; artifact
/// metadata is the auditable record, as in Task 031). Barrier accounting stays
/// saga-owned. Never logs media bytes or secrets: only ids, codecs, counts,
/// durations, and decisions.
/// </summary>
public sealed class RenderWorker : BaseConsumer<StageWorkRequested>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IStageExecutionContextFactory _contextFactory;
    private readonly IOptions<RetryOptions> _retryOptions;
    private readonly RenderService _renderer;
    private readonly DubbingPlatform.Application.Abstractions.IFFprobeService _ffprobe;
    private readonly ArtifactService _artifacts;
    private readonly DubbingPlatform.Application.Abstractions.IArtifactStorage _storage;
    private readonly FeatureOptions _features;
    private readonly ILogger<RenderWorker> _logger;

    public RenderWorker(
        IStageExecutionContextFactory contextFactory,
        IOptions<RetryOptions> retryOptions,
        RenderService renderer,
        DubbingPlatform.Application.Abstractions.IFFprobeService ffprobe,
        ArtifactService artifacts,
        DubbingPlatform.Application.Abstractions.IArtifactStorage storage,
        ILogger<RenderWorker> logger,
        IDeferredSender? deferredSender = null,
        IOptions<FeatureOptions>? features = null)
        : base(contextFactory, retryOptions, deferredSender)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        ArgumentNullException.ThrowIfNull(retryOptions);
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentNullException.ThrowIfNull(ffprobe);
        ArgumentNullException.ThrowIfNull(artifacts);
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(logger);
        _contextFactory = contextFactory;
        _retryOptions = retryOptions;
        _renderer = renderer;
        _ffprobe = ffprobe;
        _artifacts = artifacts;
        _storage = storage;
        _features = features?.Value ?? new FeatureOptions();
        _logger = logger;
    }

    protected override bool ShouldProcess(StageWorkRequested message)
    {
        var stage = message.StageTypeRequired ?? message.StageType;
        return string.Equals(stage?.Trim(), nameof(StageType.Render), StringComparison.Ordinal);
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
            if (execution.StageType != StageType.Render)
            {
                throw new DomainException($"Stage execution '{execution.Id}' is '{execution.StageType}', not Render.");
            }

            if (execution.ScopeType != ScopeType.Project)
            {
                throw new DomainException($"Stage execution '{execution.Id}' is '{execution.ScopeType}', not Project, for Render.");
            }

            await EnsureLeaseRunningAsync(execution, cancellationToken).ConfigureAwait(false);

            var blocker = await FindOpenReviewAsync(execution, cancellationToken).ConfigureAwait(false);
            if (blocker is not null)
            {
                _logger.LogInformation(
                    "Render for run {RunId} blocked by open review {ReviewId}; routing to manual review without rendering.",
                    execution.ProcessingRunId, blocker.Id);
                await PublishBlockedReviewAsync(context, execution, blocker, cancellationToken).ConfigureAwait(false);
                return;
            }

            var rendered = await RenderRunAsync(execution, cancellationToken).ConfigureAwait(false);

            await EnsureLeaseRunningAsync(execution, cancellationToken).ConfigureAwait(false);

            var stages = new StageExecutionService(_contextFactory, _retryOptions);
            await stages.CompleteAsync(
                execution.TenantId, execution.Id,
                execution.LeaseOwner, execution.LeaseToken,
                [rendered.ArtifactId.ToString("N")],
                cancellationToken).ConfigureAwait(false);

            await MarkRunCompletedAsync(execution, cancellationToken).ConfigureAwait(false);
            PlatformMetrics.ProjectCompleted(execution.TenantId);
            PlatformMetrics.StageCompleted(execution.TenantId, "Render");

            await PublishCompletedAsync(context, execution, rendered, cancellationToken).ConfigureAwait(false);

            await MaybePublishEnrichmentAsync(context, execution, cancellationToken).ConfigureAwait(false);

            _logger.LogInformation(
                "Rendered run {RunId}: output {OutputId} ({Format}, {Duration}ms, copy={Copy}).",
                execution.ProcessingRunId, rendered.OutputAssetId,
                rendered.Result.OutputFormat,
                rendered.Result.DurationMs.ToString(CultureInfo.InvariantCulture),
                rendered.Result.CopyVideo);
        }
        catch (LeaseLostException)
        {
            throw;
        }
#pragma warning disable CA1031 // Worker poison contract: permanent failures fail the run + publish RunFailed; transient rethrows.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogWarning(
                "Render failed for run {RunId}: {Error}.",
                execution.ProcessingRunId, ex.Message);
            if (!await FailRunAsync(context, execution, ex, cancellationToken).ConfigureAwait(false))
            {
                throw;
            }
        }
    }

    private sealed record RenderedOutput(Guid ArtifactId, Guid OutputAssetId, RenderResult Result);

    private async Task<RenderedOutput> RenderRunAsync(StageExecution execution, CancellationToken cancellationToken)
    {
        var tenantId = execution.TenantId;
        var projectId = execution.ProjectId;
        var runId = execution.ProcessingRunId;

        var project = await LoadOwnedProjectAsync(tenantId, projectId, cancellationToken).ConfigureAwait(false);
        await EnsureRunActiveAsync(tenantId, runId, projectId, cancellationToken).ConfigureAwait(false);

        var (mixedArtifactId, mixedStorageKey) = await LoadMixedAsync(tenantId, runId, cancellationToken).ConfigureAwait(false);
        var (sourceArtifactId, sourceStorageKey) = await LoadSourceAsync(tenantId, projectId, cancellationToken).ConfigureAwait(false);

        string workDir;
        try
        {
            workDir = ProcessRunner.CreateTempWorkingDir();
        }
        catch (IOException ex)
        {
            throw new ErrorCodeException(ErrorCodes.ResourceExhausted, "Render cannot create temp dir; disk may be full.", ex);
        }

        try
        {
            var mixedFile = Path.Combine(workDir, string.Concat("mixed-", mixedArtifactId.ToString("N"), ".wav"));
            await DownloadAsync(mixedStorageKey, mixedFile, cancellationToken).ConfigureAwait(false);

            string? sourceFile = null;
            string? sourceVideoPath = null;
            double? fps = null;
            var hasVideo = false;
            if (sourceStorageKey is not null)
            {
                sourceFile = Path.Combine(workDir, "source.bin");
                await DownloadAsync(sourceStorageKey, sourceFile, cancellationToken).ConfigureAwait(false);

                var probe = await ProbeSourceAsync(sourceFile, cancellationToken).ConfigureAwait(false);
                hasVideo = probe.HasVideo;
                fps = probe.Fps;
                if (hasVideo)
                {
                    sourceVideoPath = sourceFile;
                }
            }

            var outputFormat = RenderService.ParseOutputFormat(project.SettingsJson, hasVideo);
            var outputFile = Path.Combine(workDir, string.Concat("rendered", RenderService.ExtensionFor(outputFormat)));

            await EnsureLeaseRunningAsync(execution, cancellationToken).ConfigureAwait(false);

            var result = await _renderer.RenderAsync(
                sourceVideoPath, mixedFile, outputFile, outputFormat, fps, cancellationToken).ConfigureAwait(false);

            await EnsureLeaseRunningAsync(execution, cancellationToken).ConfigureAwait(false);

            var parents = new List<Guid> { mixedArtifactId };
            if (sourceArtifactId.HasValue)
            {
                parents.Add(sourceArtifactId.Value);
            }

            PublishResult published;
            using (var stream = new FileStream(outputFile, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                published = await _artifacts.PublishAsync(
                    tenantId, projectId, runId,
                    StageType.Render, ArtifactType.RenderedOutput,
                    stream, RenderService.ExtensionFor(outputFormat), RenderService.ContentTypeFor(outputFormat),
                    null, null, null, null,
                    parents.Distinct().ToList(),
                    execution.Id,
                    cancellationToken).ConfigureAwait(false);
            }

            var metadataJson = JsonSerializer.Serialize(new
            {
                schemaVersion = "1",
                runId = runId.ToString("N"),
                outputFormat = result.OutputFormat,
                container = result.Container,
                durationMs = result.DurationMs,
                sourceDurationMs = result.SourceDurationMs,
                toleranceMs = result.ToleranceMs,
                videoIncluded = result.VideoIncluded,
                copyVideo = result.CopyVideo,
                reencodeReason = result.ReencodeReason,
                fps = result.Fps,
                ffmpegArgs = result.FfmpegArgs,
                mixedArtifactId = mixedArtifactId.ToString("N"),
                sourceArtifactId = sourceArtifactId?.ToString("N"),
            }, JsonOptions);

            var mediaKind = result.VideoIncluded ? "Video" : "Audio";
            var outputAssetId = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;
            using (TenantContext.BeginScope(tenantId))
            {
                using var db = _contextFactory.CreateDbContext();
                await db.Database.ExecuteSqlRawAsync(
                    "UPDATE artifacts SET metadata_json = {0} WHERE id = {1} AND tenant_id = {2}",
                    metadataJson, published.ArtifactId, tenantId).ConfigureAwait(false);
                db.Set<OutputAsset>().Add(new OutputAsset(
                    outputAssetId, tenantId, projectId, runId, published.ArtifactId,
                    mediaKind, result.DurationMs, result.Container, now));
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }

            return new RenderedOutput(published.ArtifactId, outputAssetId, result);
        }
        finally
        {
            DeleteDirQuietly(workDir);
        }
    }

    private async Task<ReviewItem?> FindOpenReviewAsync(StageExecution execution, CancellationToken cancellationToken)
    {
        using (TenantContext.BeginScope(execution.TenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            return await db.Set<ReviewItem>()
                .AsNoTracking()
                .Where(r => r.ProcessingRunId == execution.ProcessingRunId && r.Status == ReviewStatus.Open)
                .OrderBy(r => r.CreatedAt)
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task PublishBlockedReviewAsync(
        ConsumeContext<StageWorkRequested> context,
        StageExecution execution,
        ReviewItem blocker,
        CancellationToken cancellationToken)
    {
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
            nameof(StageType.Render),
            blocker.Id.ToString("N")), cancellationToken).ConfigureAwait(false);
    }

    private async Task PublishCompletedAsync(
        ConsumeContext<StageWorkRequested> context,
        StageExecution execution,
        RenderedOutput rendered,
        CancellationToken cancellationToken)
    {
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
            nameof(StageType.Render),
            [rendered.ArtifactId.ToString("N")]), cancellationToken).ConfigureAwait(false);

        await context.Publish(new RunCompleted(
            Guid.NewGuid(), correlationId,
            execution.TenantId, execution.ProjectId, execution.ProcessingRunId,
            null, null, null, null, null,
            MessageVersionPolicy.CurrentVersion, DateTimeOffset.UtcNow, execution.Attempt,
            null, null, null,
            rendered.OutputAssetId.ToString("D")), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Optional enrichment fan-out (out-of-band, after core render). When both
    /// feature flags are false the core completes identically to Task 33 (no
    /// jobs, no artifacts). When a flag is true AND the project opted in via
    /// <c>settings.enrichment</c>, publishes one <see cref="EnrichmentRequested"/>
    /// per requested kind to the <c>ai.gpu</c> video workload queue
    /// (<see cref="QueueNames.AiGpu"/>; MassTransit topology routes the message
    /// type to the subscribed enrichment workers). Best effort and isolated:
    /// publish failures are logged and swallowed so core completion is never
    /// affected. Flag values are read at render-completion time, so mid-run
    /// toggles take effect only for new runs.
    /// </summary>
    private async Task MaybePublishEnrichmentAsync(
        ConsumeContext<StageWorkRequested> context,
        StageExecution execution,
        CancellationToken cancellationToken)
    {
        if (!_features.VideoIntelligenceEnabled && !_features.LipSyncEnabled)
        {
            return;
        }

        try
        {
            string? settingsJson;
            using (TenantContext.BeginMaintenanceScope())
            {
                using var db = _contextFactory.CreateDbContext();
                var project = await db.Set<DubbingProject>()
                    .AsNoTracking()
                    .FirstOrDefaultAsync(p => p.Id == execution.ProjectId, cancellationToken).ConfigureAwait(false);
                if (project is null || project.TenantId != execution.TenantId)
                {
                    return;
                }

                settingsJson = project.SettingsJson;
            }

            var kinds = EnrichmentGate.RequestedKinds(_features, settingsJson);
            if (kinds.Count == 0)
            {
                return;
            }

            var inbound = context.Message;
            var correlationId = string.IsNullOrWhiteSpace(inbound.CorrelationId)
                ? context.CorrelationId?.ToString() ?? Guid.NewGuid().ToString("N")
                : inbound.CorrelationId;
            var requests = EnrichmentGate.BuildRequests(
                execution.TenantId, execution.ProjectId, execution.ProcessingRunId,
                correlationId, kinds, execution.Attempt);
            foreach (var request in requests)
            {
                await context.Publish(request, cancellationToken).ConfigureAwait(false);
            }

            _logger.LogInformation(
                "Enrichment requested for run {RunId}: {Kinds}.",
                execution.ProcessingRunId, string.Join(",", kinds));
        }
#pragma warning disable CA1031 // Enrichment fan-out is best effort; publish failures must never fail core render.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogWarning(
                "Enrichment fan-out failed for run {RunId}: {Error}. Core run completed.",
                execution.ProcessingRunId, ex.Message);
        }
    }

    private async Task<bool> FailRunAsync(
        ConsumeContext<StageWorkRequested> context,
        StageExecution execution,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var handled = await StageWorkerFailure.TryFailAsync(
            _contextFactory, _retryOptions, context, execution, exception,
            nameof(StageType.Render), cancellationToken).ConfigureAwait(false);
        if (!handled)
        {
            return false;
        }

        var code = exception is ErrorCodeException coded ? coded.ErrorCode : ErrorCodes.InternalError;
        var message = Truncate(exception.Message);

        await MarkRunFailedAsync(execution, code, cancellationToken).ConfigureAwait(false);
        PlatformMetrics.ProjectFailed(execution.TenantId);
        PlatformMetrics.StageFailed(execution.TenantId, "Render");

        var inbound = context.Message;
        var correlationId = string.IsNullOrWhiteSpace(inbound.CorrelationId)
            ? context.CorrelationId?.ToString() ?? Guid.NewGuid().ToString("N")
            : inbound.CorrelationId;
        await context.Publish(new RunFailed(
            Guid.NewGuid(), correlationId,
            execution.TenantId, execution.ProjectId, execution.ProcessingRunId,
            null, null, null, null, null,
            MessageVersionPolicy.CurrentVersion, DateTimeOffset.UtcNow, execution.Attempt,
            null, null, null, code, message), cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task MarkRunCompletedAsync(StageExecution execution, CancellationToken cancellationToken)
    {
        using (TenantContext.BeginScope(execution.TenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            var run = await db.Set<ProcessingRun>()
                .FirstOrDefaultAsync(r => r.Id == execution.ProcessingRunId, cancellationToken).ConfigureAwait(false);
            if (run is null)
            {
                throw new NotFoundException($"Processing run '{execution.ProcessingRunId}' was not found.");
            }

            RunStateMachine.EnsureCanTransition(run.Status, ProcessingRunStatus.Completed);

            var project = await db.Set<DubbingProject>()
                .FirstOrDefaultAsync(p => p.Id == execution.ProjectId, cancellationToken).ConfigureAwait(false);
            if (project is not null)
            {
                ProjectStateMachine.EnsureCanTransition(project.Status, ProjectStatus.Completed);
            }

            var now = DateTimeOffset.UtcNow;
            await db.Database.ExecuteSqlRawAsync(
                "UPDATE processing_runs SET status = {0}, updated_at = {1}, completed_at = {1} " +
                "WHERE id = {2} AND tenant_id = {3} AND status <> 'Completed' AND status <> 'Cancelled'",
                ProcessingRunStatus.Completed.ToString(), now,
                execution.ProcessingRunId, execution.TenantId).ConfigureAwait(false);
            await db.Database.ExecuteSqlRawAsync(
                "UPDATE dubbing_projects SET status = {0}, updated_at = {1} WHERE id = {2} AND tenant_id = {3}",
                ProjectStatus.Completed.ToString(), now,
                execution.ProjectId, execution.TenantId).ConfigureAwait(false);
        }
    }

    private async Task MarkRunFailedAsync(StageExecution execution, string code, CancellationToken cancellationToken)
    {
        using (TenantContext.BeginScope(execution.TenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            var run = await db.Set<ProcessingRun>()
                .FirstOrDefaultAsync(r => r.Id == execution.ProcessingRunId, cancellationToken).ConfigureAwait(false);
            if (run is not null)
            {
                try
                {
                    RunStateMachine.EnsureCanTransition(run.Status, ProcessingRunStatus.Failed);
                }
                catch (DomainException)
                {
                    return;
                }
            }

            var now = DateTimeOffset.UtcNow;
            await db.Database.ExecuteSqlRawAsync(
                "UPDATE processing_runs SET status = {0}, updated_at = {1}, completed_at = {1} " +
                "WHERE id = {2} AND tenant_id = {3} AND status <> 'Completed' AND status <> 'Cancelled'",
                ProcessingRunStatus.Failed.ToString(), now,
                execution.ProcessingRunId, execution.TenantId).ConfigureAwait(false);

            var project = await db.Set<DubbingProject>()
                .FirstOrDefaultAsync(p => p.Id == execution.ProjectId, cancellationToken).ConfigureAwait(false);
            if (project is not null)
            {
                try
                {
                    ProjectStateMachine.EnsureCanTransition(project.Status, ProjectStatus.Failed);
                    await db.Database.ExecuteSqlRawAsync(
                        "UPDATE dubbing_projects SET status = {0}, updated_at = {1} WHERE id = {2} AND tenant_id = {3}",
                        ProjectStatus.Failed.ToString(), now,
                        execution.ProjectId, execution.TenantId).ConfigureAwait(false);
                }
                catch (DomainException)
                {
                    // Project already terminal (e.g. ManualReviewRequired); run failure stands alone.
                }
            }

            _logger.LogInformation(
                "Render for run {RunId} failed ({Code}); run marked Failed with no output registered on tolerance violations.",
                execution.ProcessingRunId, code);
        }
    }

    private async Task<(Guid ArtifactId, string StorageKey)> LoadMixedAsync(
        Guid tenantId,
        Guid runId,
        CancellationToken cancellationToken)
    {
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            var row = await db.Set<Artifact>()
                .AsNoTracking()
                .Where(a => a.ProcessingRunId == runId && a.Type == ArtifactType.MixedAudio)
                .OrderByDescending(a => a.CreatedAt)
                .Select(a => new { a.Id, a.ContentObjectId })
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (row is null)
            {
                throw new ErrorCodeException(ErrorCodes.ArtifactUnavailable, "Mixed audio for rendering is unavailable.");
            }

            var content = await db.Set<ContentObject>()
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == row.ContentObjectId, cancellationToken).ConfigureAwait(false);
            if (content is null || content.Status != ContentObjectStatus.Committed)
            {
                throw new ErrorCodeException(ErrorCodes.ArtifactUnavailable, "Mixed audio content is unavailable.");
            }

            return (row.Id, content.StorageKey);
        }
    }

    private async Task<(Guid? ArtifactId, string? StorageKey)> LoadSourceAsync(
        Guid tenantId,
        Guid projectId,
        CancellationToken cancellationToken)
    {
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            var asset = await db.Set<MediaAsset>()
                .AsNoTracking()
                .Where(a => a.ProjectId == projectId && a.Status == MediaAssetStatus.Valid)
                .OrderBy(a => a.CreatedAt)
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (asset is null)
            {
                return (null, null);
            }

            var content = await db.Set<ContentObject>()
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == asset.ContentObjectId, cancellationToken).ConfigureAwait(false);
            if (content is null || content.Status != ContentObjectStatus.Committed)
            {
                return (null, null);
            }

            var artifactId = await db.Set<Artifact>()
                .AsNoTracking()
                .Where(a => a.ContentObjectId == content.Id)
                .OrderByDescending(a => a.CreatedAt)
                .Select(a => (Guid?)a.Id)
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            return (artifactId, content.StorageKey);
        }
    }

    private sealed record SourceProbe(bool HasVideo, double? Fps);

    private async Task<SourceProbe> ProbeSourceAsync(string sourceFile, CancellationToken cancellationToken)
    {
        DubbingPlatform.Application.Abstractions.FfprobeResult probe;
        try
        {
            probe = await _ffprobe.ProbeAsync(sourceFile, cancellationToken).ConfigureAwait(false);
        }
        catch (ErrorCodeException ex) when (string.Equals(ex.ErrorCode, ErrorCodes.MediaCorrupt, StringComparison.Ordinal))
        {
            throw new ErrorCodeException(ErrorCodes.MediaCorrupt, "Source media is undecodable; render is blocked.", ex);
        }

        var video = probe.Streams.FirstOrDefault(s => string.Equals(s.CodecType, "video", StringComparison.OrdinalIgnoreCase));
        return new SourceProbe(video is not null, video?.Fps);
    }

    private async Task DownloadAsync(string storageKey, string destPath, CancellationToken cancellationToken)
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
#pragma warning disable CA1031 // Storage mapping: any transport failure surfaces as STORAGE_UNAVAILABLE for worker policy.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            throw new ErrorCodeException(ErrorCodes.StorageUnavailable, "Storage is temporarily unavailable; retry the operation.", ex);
        }

        try
        {
            using (download)
            {
                using var local = new FileStream(destPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
                await download.CopyToAsync(local, cancellationToken).ConfigureAwait(false);
                await local.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (IOException ex)
        {
            throw new ErrorCodeException(ErrorCodes.ResourceExhausted, "Render staging hit disk limits.", ex);
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

    private static string Truncate(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return "Render failed.";
        }

        var trimmed = message.Trim();
        return trimmed.Length <= 500 ? trimmed : trimmed.Substring(0, 500);
    }
}
