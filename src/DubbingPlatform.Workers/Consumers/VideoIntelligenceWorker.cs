using System.Diagnostics;
using System.Text;
using DubbingPlatform.Application.Abstractions.Providers;
using DubbingPlatform.Application.Abstractions.Providers.Dtos;
using DubbingPlatform.Application.Configuration;
using DubbingPlatform.Application.Enrichment;
using DubbingPlatform.Application.MultiTenancy;
using DubbingPlatform.Application.Options;
using DubbingPlatform.Application.Providers;
using DubbingPlatform.Application.Services;
using DubbingPlatform.Contracts.Messages;
using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Domain.Enums;
using DubbingPlatform.Infrastructure.Messaging;
using DubbingPlatform.Infrastructure.Observability;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DubbingPlatform.Workers.Consumers;

/// <summary>
/// Optional video-intelligence enrichment on <c>ai.gpu</c> (out-of-band, after
/// core render). Consumes <see cref="EnrichmentRequested"/> with
/// <c>Kind==VideoIntelligence</c>; other kinds are ignored pre-claim via
/// <c>ShouldProcess</c> (shared endpoint semantics, as in Task 012).
/// Dual-gated: global <c>Features:VideoIntelligenceEnabled</c> AND per-project
/// <c>settings.enrichment.videoIntelligence</c> (see <see cref="EnrichmentGate"/>).
/// Calls <see cref="IVideoIntelligenceProvider"/> (mock default: 1 face +
/// 1 active speaker, confidence 0.88 success), persists one
/// <c>ArtifactType.Enrichment</c> JSON artifact (schema v1, tolerant readers
/// ignore unknown types) plus a separate <c>OutputAsset</c>
/// (<c>Video/mp4</c> annotated-preview placeholder referencing the JSON
/// artifact), and records a <see cref="ProviderExecution"/> row.
/// Isolation: all errors are caught, logged (ids only, never face bytes),
/// counted on <c>enrichment.failed{kind=VideoIntelligence}</c>, and
/// acknowledged; never publishes <c>RunFailed</c>, never modifies the run
/// status or the core <c>OutputAsset</c>, so core download is unaffected.
/// Face data is tenant-scoped and follows intermediate 30d retention; no
/// cross-tenant access and no biometric export without consent.
/// </summary>
public sealed class VideoIntelligenceWorker : BaseConsumer<EnrichmentRequested>
{
    private readonly IStageExecutionContextFactory _contextFactory;
    private readonly FeatureOptions _features;
    private readonly IVideoIntelligenceProvider _provider;
    private readonly ProviderExecutionRecorder _recorder;
    private readonly ArtifactService _artifacts;
    private readonly ILogger<VideoIntelligenceWorker> _logger;

    public VideoIntelligenceWorker(
        IStageExecutionContextFactory contextFactory,
        IOptions<RetryOptions> retryOptions,
        IOptions<FeatureOptions> features,
        IVideoIntelligenceProvider provider,
        ProviderExecutionRecorder recorder,
        ArtifactService artifacts,
        ILogger<VideoIntelligenceWorker> logger,
        IDeferredSender? deferredSender = null)
        : base(contextFactory, retryOptions, deferredSender)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        ArgumentNullException.ThrowIfNull(retryOptions);
        ArgumentNullException.ThrowIfNull(features);
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(recorder);
        ArgumentNullException.ThrowIfNull(artifacts);
        ArgumentNullException.ThrowIfNull(logger);
        _contextFactory = contextFactory;
        _features = features.Value;
        _provider = provider;
        _recorder = recorder;
        _artifacts = artifacts;
        _logger = logger;
    }

    protected override bool ShouldProcess(EnrichmentRequested message)
    {
        return string.Equals(
            message.Kind?.Trim(),
            EnrichmentKinds.VideoIntelligence,
            StringComparison.Ordinal);
    }

    protected override async Task HandleAsync(
        ConsumeContext<EnrichmentRequested> context,
        StageExecution? execution,
        CancellationToken cancellationToken)
    {
        var message = context.Message;
        if (!ShouldProcess(message))
        {
            return;
        }

        if (!_features.VideoIntelligenceEnabled)
        {
            return;
        }

        var settingsJson = await LoadSettingsJsonAsync(
            message.TenantId, message.ProjectId, cancellationToken).ConfigureAwait(false);
        if (!EnrichmentGate.ShouldRequestVideoIntelligence(_features, settingsJson))
        {
            return;
        }

        try
        {
            await ProcessAsync(message, settingsJson, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
#pragma warning disable CA1031 // Isolation contract: all enrichment faults are swallowed after metric+log; core run is never touched.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            EnrichmentMetrics.Failed(EnrichmentKinds.VideoIntelligence);
            _logger.LogWarning(
                "Video-intelligence enrichment failed for run {RunId}: {Error}. Core run untouched.",
                message.ProcessingRunId, ex.Message);
        }
    }

    private async Task ProcessAsync(
        EnrichmentRequested message,
        string? settingsJson,
        CancellationToken cancellationToken)
    {
        var tenantId = message.TenantId;
        var projectId = message.ProjectId;
        var runId = message.ProcessingRunId;

        var project = await LoadOwnedProjectAsync(tenantId, projectId, cancellationToken).ConfigureAwait(false);
        var (sourceArtifactId, durationMs) = await ResolveSourceAsync(tenantId, projectId, runId, cancellationToken).ConfigureAwait(false);

        var request = new VideoIntelligenceRequest(
            tenantId, projectId, runId,
            (sourceArtifactId ?? Guid.Empty).ToString("N"),
            project.SourceLanguage,
            0,
            durationMs,
            "mp4");
        var requestHash = ConfigurationHashCalculator.Compute(request);

        var stopwatch = Stopwatch.StartNew();
        var response = await _provider.AnalyzeAsync(request, cancellationToken).ConfigureAwait(false);
        stopwatch.Stop();
        var latencyMs = Math.Max(0, stopwatch.ElapsedMilliseconds);

        var payload = EnrichmentPayload.BuildVideoIntelligenceJson(
            runId, response, sourceArtifactId?.ToString("N"));

        var parents = sourceArtifactId.HasValue && sourceArtifactId.Value != Guid.Empty
            ? new List<Guid> { sourceArtifactId.Value }
            : new List<Guid>();

        PublishResult published;
        using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(payload), writable: false))
        {
            published = await _artifacts.PublishAsync(
                tenantId, projectId, runId,
                StageType.Render, ArtifactType.Enrichment,
                stream, ".json", "application/json",
                "Mock", response.Model,
                null, null,
                parents,
                null,
                cancellationToken).ConfigureAwait(false);
        }

        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            await db.Database.ExecuteSqlRawAsync(
                "UPDATE artifacts SET metadata_json = {0} WHERE id = {1} AND tenant_id = {2}",
                payload, published.ArtifactId, tenantId).ConfigureAwait(false);
            db.Set<OutputAsset>().Add(new OutputAsset(
                Guid.NewGuid(), tenantId, projectId, runId, published.ArtifactId,
                "Video", Math.Max(0, durationMs), "mp4", DateTimeOffset.UtcNow));
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        await RecordExecutionAsync(
            message, requestHash, response, latencyMs, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Video-intelligence enrichment completed for run {RunId}: artifact {ArtifactId} ({Faces} faces, confidence {Confidence}).",
            runId, published.ArtifactId, response.Faces.Count, response.Confidence);
    }

    private async Task RecordExecutionAsync(
        EnrichmentRequested message,
        string requestHash,
        VideoIntelligenceResponse response,
        long latencyMs,
        CancellationToken cancellationToken)
    {
        var responseHash = ConfigurationHashCalculator.Compute(new
        {
            faces = response.Faces.Select(f => new { track = f.TrackId, startMs = f.StartMs, endMs = f.EndMs, confidence = f.Confidence }),
            speakers = response.ActiveSpeakers.Select(s => new { label = s.SpeakerLabel, startMs = s.StartMs, endMs = s.EndMs, confidence = s.Confidence }),
            confidence = response.Confidence,
            model = response.Model,
        });
        string? externalJobId = null;
        if (response.RawMetadata is not null
            && response.RawMetadata.TryGetValue("mock.job_id", out var jobId)
            && !string.IsNullOrWhiteSpace(jobId))
        {
            externalJobId = jobId.Trim();
        }

        var idempotencyKey = ProviderExecutionRecorder.BuildIdempotencyKey(
            message.ProcessingRunId, EnrichmentKinds.VideoIntelligence, "enrichment", message.Attempt);
        var row = new ProviderExecution(
            Guid.NewGuid(), message.TenantId, message.ProjectId, message.ProcessingRunId,
            null, ProviderType.Mock, ProviderCapability.VideoIntelligence, response.Model,
            response.ModelVersion, response.Deployment, null, null,
            Math.Max(0, message.Attempt), requestHash, responseHash, latencyMs,
            response.Usage?.TokensIn, response.Usage?.TokensOut, response.Usage?.AudioSeconds,
            response.Usage?.EstimatedCostUsd, response.Usage?.EstimatedCostUsd, null,
            OutcomeClass.Success, null, null, null, null, externalJobId, idempotencyKey,
            DateTimeOffset.UtcNow);
        await _recorder.RecordAsync(row, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string?> LoadSettingsJsonAsync(Guid tenantId, Guid projectId, CancellationToken cancellationToken)
    {
        using (TenantContext.BeginMaintenanceScope())
        {
            using var db = _contextFactory.CreateDbContext();
            var project = await db.Set<DubbingProject>()
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken).ConfigureAwait(false);
            if (project is null || project.TenantId != tenantId)
            {
                return null;
            }

            return project.SettingsJson;
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
                throw new Application.Exceptions.NotFoundException($"Project '{projectId}' was not found.");
            }

            isDeleted = await db.Set<DubbingProject>()
                .Where(p => p.Id == projectId)
                .Select(p => EF.Property<bool>(p, "IsDeleted"))
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        }

        if (project.TenantId != tenantId)
        {
            throw new Application.Exceptions.ForbiddenException($"Project '{projectId}' does not belong to the current tenant.");
        }

        if (isDeleted)
        {
            throw new Application.Exceptions.NotFoundException($"Project '{projectId}' was not found.");
        }

        return project;
    }

    private async Task<(Guid? ArtifactId, int DurationMs)> ResolveSourceAsync(
        Guid tenantId, Guid projectId, Guid runId, CancellationToken cancellationToken)
    {
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            var rendered = await db.Set<Artifact>()
                .AsNoTracking()
                .Where(a => a.ProcessingRunId == runId && a.Type == ArtifactType.RenderedOutput)
                .OrderByDescending(a => a.CreatedAt)
                .Select(a => (Guid?)a.Id)
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            var duration = await db.Set<MediaAsset>()
                .Where(a => a.ProjectId == projectId && a.Status == MediaAssetStatus.Valid)
                .OrderBy(a => a.CreatedAt)
                .Select(a => (int?)a.DurationMs)
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            return (rendered, Math.Max(0, duration ?? 0));
        }
    }
}
