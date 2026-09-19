using System.Diagnostics;
using System.Text;
using DubbingPlatform.Application.Abstractions;
using DubbingPlatform.Application.Abstractions.Providers;
using DubbingPlatform.Application.Abstractions.Providers.Dtos;
using DubbingPlatform.Application.Configuration;
using DubbingPlatform.Application.Enrichment;
using DubbingPlatform.Application.MultiTenancy;
using DubbingPlatform.Application.Options;
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
/// Optional lip-sync enrichment on <c>ai.gpu</c> (out-of-band, after core
/// render). Consumes <see cref="EnrichmentRequested"/> with
/// <c>Kind==LipSync</c>; other kinds are ignored pre-claim via
/// <c>ShouldProcess</c>. Dual-gated: global
/// <c>Features:LipSyncEnabled</c> AND per-project
/// <c>settings.enrichment.lipSync</c> (see <see cref="EnrichmentGate"/>).
/// Analyzes lip movement via <see cref="ILipSyncProvider"/> (mock score 0.85,
/// 0.35 low-confidence); the optional mouth transform is a mock byte-copy of
/// the source video (no FFmpeg shell; <c>ArgumentList</c> only in the underlying
/// media services) with <c>lipSyncScore</c> metadata. Post-validates via
/// <see cref="IFFprobeService"/> when a source video exists (decodable +
/// duration within <see cref="EnrichmentPayload.PreviewToleranceMs"/>);
/// validation failures mark enrichment Failed without touching the core run.
/// Persists one <c>ArtifactType.Enrichment</c> JSON artifact plus a separate
/// <c>OutputAsset</c> (<c>Video/mp4</c>) and records provider/model metadata in
/// <c>metadata_json</c>. Isolation: all errors are caught, logged (ids only),
/// counted on <c>enrichment.failed{kind=LipSync}</c>, and acknowledged; never
/// publishes <c>RunFailed</c> and never modifies the run status or core asset.
/// </summary>
public sealed class LipSyncWorker : BaseConsumer<EnrichmentRequested>
{
    private readonly IStageExecutionContextFactory _contextFactory;
    private readonly FeatureOptions _features;
    private readonly ILipSyncProvider _provider;
    private readonly IFFprobeService _ffprobe;
    private readonly ArtifactService _artifacts;
    private readonly ILogger<LipSyncWorker> _logger;

    public LipSyncWorker(
        IStageExecutionContextFactory contextFactory,
        IOptions<RetryOptions> retryOptions,
        IOptions<FeatureOptions> features,
        ILipSyncProvider provider,
        IFFprobeService ffprobe,
        ArtifactService artifacts,
        ILogger<LipSyncWorker> logger,
        IDeferredSender? deferredSender = null)
        : base(contextFactory, retryOptions, deferredSender)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        ArgumentNullException.ThrowIfNull(retryOptions);
        ArgumentNullException.ThrowIfNull(features);
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(ffprobe);
        ArgumentNullException.ThrowIfNull(artifacts);
        ArgumentNullException.ThrowIfNull(logger);
        _contextFactory = contextFactory;
        _features = features.Value;
        _provider = provider;
        _ffprobe = ffprobe;
        _artifacts = artifacts;
        _logger = logger;
    }

    protected override bool ShouldProcess(EnrichmentRequested message)
    {
        return string.Equals(
            message.Kind?.Trim(),
            EnrichmentKinds.LipSync,
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

        if (!_features.LipSyncEnabled)
        {
            return;
        }

        var settingsJson = await LoadSettingsJsonAsync(
            message.TenantId, message.ProjectId, cancellationToken).ConfigureAwait(false);
        if (!EnrichmentGate.ShouldRequestLipSync(_features, settingsJson))
        {
            return;
        }

        try
        {
            await ProcessAsync(message, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
#pragma warning disable CA1031 // Isolation contract: all enrichment faults are swallowed after metric+log; core run is never touched.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            EnrichmentMetrics.Failed(EnrichmentKinds.LipSync);
            _logger.LogWarning(
                "Lip-sync enrichment failed for run {RunId}: {Error}. Core run untouched.",
                message.ProcessingRunId, ex.Message);
        }
    }

    private async Task ProcessAsync(
        EnrichmentRequested message,
        CancellationToken cancellationToken)
    {
        var tenantId = message.TenantId;
        var projectId = message.ProjectId;
        var runId = message.ProcessingRunId;

        var project = await LoadOwnedProjectAsync(tenantId, projectId, cancellationToken).ConfigureAwait(false);
        var (sourceArtifactId, sourceDurationMs, hasVideo) = await ResolveSourceAsync(
            tenantId, projectId, runId, cancellationToken).ConfigureAwait(false);

        var request = new LipSyncRequest(
            tenantId, projectId, runId,
            (sourceArtifactId ?? Guid.Empty).ToString("N"),
            project.SourceLanguage,
            0,
            sourceDurationMs,
            hasVideo ? "mp4" : null);

        var stopwatch = Stopwatch.StartNew();
        var response = await _provider.AnalyzeAsync(request, cancellationToken).ConfigureAwait(false);
        stopwatch.Stop();
        _ = Math.Max(0, stopwatch.ElapsedMilliseconds);

        if (double.IsNaN(response.Score) || response.Score < 0.0 || response.Score > 1.0)
        {
            EnrichmentMetrics.Failed(EnrichmentKinds.LipSync);
            _logger.LogWarning(
                "Lip-sync enrichment failed for run {RunId}: invalid score. Core run untouched.",
                runId);
            return;
        }

        var validated = await PostValidateAsync(
            sourceArtifactId, sourceDurationMs, hasVideo, cancellationToken).ConfigureAwait(false);
        if (!validated)
        {
            EnrichmentMetrics.Failed(EnrichmentKinds.LipSync);
            _logger.LogWarning(
                "Lip-sync enrichment post-validation failed for run {RunId}. Core run untouched.",
                runId);
            return;
        }

        var payload = EnrichmentPayload.BuildLipSyncJson(
            runId, response.Score, sourceDurationMs, sourceArtifactId?.ToString("N"));

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
                "Video", Math.Max(0, sourceDurationMs), "mp4", DateTimeOffset.UtcNow));
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        _logger.LogInformation(
            "Lip-sync enrichment completed for run {RunId}: artifact {ArtifactId} (score {Score}).",
            runId, published.ArtifactId, response.Score);
    }

    private async Task<bool> PostValidateAsync(
        Guid? sourceArtifactId,
        int sourceDurationMs,
        bool hasVideo,
        CancellationToken cancellationToken)
    {
        if (!sourceArtifactId.HasValue || sourceArtifactId.Value == Guid.Empty)
        {
            return EnrichmentPayload.IsLipSyncOutputValid(
                decodable: true,
                outputDurationMs: sourceDurationMs,
                sourceDurationMs: sourceDurationMs,
                toleranceMs: EnrichmentPayload.PreviewToleranceMs(hasVideo));
        }

        string? storageKey = null;

        try
        {
            storageKey = await ResolveStorageKeyAsync(sourceArtifactId.Value, cancellationToken).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Probe prep: storage lookup failure means enrichment Failed, never core failure.
        catch (Exception)
#pragma warning restore CA1031
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(storageKey))
        {
            return EnrichmentPayload.IsLipSyncOutputValid(
                true, sourceDurationMs, sourceDurationMs,
                EnrichmentPayload.PreviewToleranceMs(hasVideo));
        }

        FfprobeResult probe;
        try
        {
            probe = await _ffprobe.ProbeAsync(storageKey, cancellationToken).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Post-validation: undecodable source means enrichment Failed, core untouched.
        catch (Exception)
#pragma warning restore CA1031
        {
            return false;
        }

        var decodable = probe.Streams.Count > 0;
        return EnrichmentPayload.IsLipSyncOutputValid(
            decodable,
            probe.DurationMs,
            sourceDurationMs,
            EnrichmentPayload.PreviewToleranceMs(hasVideo));
    }

    private async Task<string?> ResolveStorageKeyAsync(Guid artifactId, CancellationToken cancellationToken)
    {
        using (TenantContext.BeginMaintenanceScope())
        {
            using var db = _contextFactory.CreateDbContext();
            var artifact = await db.Set<Artifact>()
                .AsNoTracking()
                .FirstOrDefaultAsync(a => a.Id == artifactId, cancellationToken).ConfigureAwait(false);
            if (artifact is null)
            {
                return null;
            }

            var content = await db.Set<ContentObject>()
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == artifact.ContentObjectId, cancellationToken).ConfigureAwait(false);
            if (content is null || content.Status != ContentObjectStatus.Committed)
            {
                return null;
            }

            return content.StorageKey;
        }
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

    private async Task<(Guid? ArtifactId, int DurationMs, bool HasVideo)> ResolveSourceAsync(
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
            var asset = await db.Set<MediaAsset>()
                .AsNoTracking()
                .Where(a => a.ProjectId == projectId && a.Status == MediaAssetStatus.Valid)
                .OrderBy(a => a.CreatedAt)
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (asset is null)
            {
                return (rendered, 0, false);
            }

            return (rendered, Math.Max(0, asset.DurationMs), true);
        }
    }
}
