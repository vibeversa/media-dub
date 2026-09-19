using System.Diagnostics;
using System.Text.Json;
using DubbingPlatform.Application.Abstractions.Providers;
using DubbingPlatform.Application.Abstractions.Providers.Dtos;
using DubbingPlatform.Application.Configuration;
using DubbingPlatform.Application.Errors;
using DubbingPlatform.Application.Exceptions;
using DubbingPlatform.Application.MultiTenancy;
using DubbingPlatform.Application.Options;
using DubbingPlatform.Application.Providers;
using DubbingPlatform.Application.Services;
using DubbingPlatform.Contracts.Messages;
using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Domain.Enums;
using DubbingPlatform.Domain.Exceptions;
using DubbingPlatform.Infrastructure.Messaging;
using DubbingPlatform.Infrastructure.Observability;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DubbingPlatform.Workers.Consumers;

/// <summary>
/// Run-scoped voice-activity detection on <c>media.preparation</c> (the
/// workload queue for Vad per <c>WorkQueueRouter</c>; the task text names
/// <c>ai.provider</c>, but routing is workload-class based and Vad is CPU
/// media preparation alongside analysis/preparation/separation, so this worker
/// shares <c>media.preparation</c> with a pre-claim stage filter like the
/// other media workers). Claims via <see cref="BaseConsumer{TMessage}"/>
/// (at-least-once, lease fenced), resolves the dialogue artifact (separation
/// output when present, else canonical), routes via <see cref="ProviderResolver"/>
/// (capability <c>Vad</c>), calls <c>DetectAsync</c> once (transient I/O
/// rethrows into transport retry via <see cref="StageWorkerFailure"/>;
/// permanent provider errors fail the stage with no fallback), records a
/// <c>ProviderExecution</c> row, persists one <c>VadRegions</c> JSON artifact
/// (schema <c>v1</c>) with <c>parents=[dialogue]</c>, completes the execution,
/// and publishes <c>StageCompleted</c> for the saga. Empty regions (silence)
/// still publish and complete; <c>SegmentBuild</c> maps them to zero segments.
/// </summary>
public sealed class VadWorker : BaseConsumer<StageWorkRequested>
{
    private readonly IStageExecutionContextFactory _contextFactory;
    private readonly IOptions<RetryOptions> _retryOptions;
    private readonly IVadProvider _vad;
    private readonly ProviderResolver _resolver;
    private readonly ProviderExecutionRecorder _recorder;
    private readonly ArtifactService _artifacts;
    private readonly ILogger<VadWorker> _logger;

    public VadWorker(
        IStageExecutionContextFactory contextFactory,
        IOptions<RetryOptions> retryOptions,
        IVadProvider vad,
        ProviderResolver resolver,
        ProviderExecutionRecorder recorder,
        ArtifactService artifacts,
        ILogger<VadWorker> logger,
        IDeferredSender? deferredSender = null)
        : base(contextFactory, retryOptions, deferredSender)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        ArgumentNullException.ThrowIfNull(retryOptions);
        ArgumentNullException.ThrowIfNull(vad);
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(recorder);
        ArgumentNullException.ThrowIfNull(artifacts);
        ArgumentNullException.ThrowIfNull(logger);
        _contextFactory = contextFactory;
        _retryOptions = retryOptions;
        _vad = vad;
        _resolver = resolver;
        _recorder = recorder;
        _artifacts = artifacts;
        _logger = logger;
    }

    protected override bool ShouldProcess(StageWorkRequested message)
    {
        var stage = message.StageTypeRequired ?? message.StageType;
        return string.Equals(stage?.Trim(), nameof(StageType.Vad), StringComparison.Ordinal);
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

        if (execution.Status is StageStatus.Completed or StageStatus.Skipped)
        {
            return;
        }

        try
        {
            if (execution.StageType != StageType.Vad)
            {
                throw new DomainException($"Stage execution '{execution.Id}' is '{execution.StageType}', not Vad.");
            }

            var project = await LoadOwnedProjectAsync(
                execution.TenantId, execution.ProjectId, cancellationToken).ConfigureAwait(false);
            var dialogue = await ResolveDialogueAsync(
                execution.TenantId, execution.ProjectId, execution.ProcessingRunId,
                execution.InputHash, cancellationToken).ConfigureAwait(false);
            var durationMs = await LoadDurationMsAsync(
                execution.TenantId, execution.ProjectId, cancellationToken).ConfigureAwait(false);

            var (provider, model) = await _resolver.ResolveAsync(
                ProviderCapability.Vad,
                execution.TenantId,
                project.SourceLanguage,
                dialogue.Content.SizeBytes,
                durationMs,
                cancellationToken).ConfigureAwait(false);

            if (provider != ProviderType.Mock)
            {
                throw new ErrorCodeException(
                    ErrorCodes.ProviderConfigurationError,
                    string.Concat("No VAD adapter for provider '", provider.ToString(), "'."));
            }

            var request = new VadRequest(
                execution.TenantId, execution.ProjectId, execution.ProcessingRunId,
                dialogue.Artifact.Id.ToString("N"),
                project.SourceLanguage,
                dialogue.Content.SizeBytes,
                durationMs,
                "flac");
            var requestHash = ConfigurationHashCalculator.Compute(request);

            var stopwatch = Stopwatch.StartNew();
            var response = await _vad.DetectAsync(request, cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();
            var latencyMs = stopwatch.ElapsedMilliseconds;

            ValidateResponse(response, durationMs);

            var responseHash = ConfigurationHashCalculator.Compute(new
            {
                regions = response.Regions.Select(r => new { startMs = r.StartMs, endMs = r.EndMs, confidence = r.Confidence }),
                confidence = response.Confidence,
                model = response.Model,
            });

            await RecordExecutionAsync(
                execution, provider, model, response,
                requestHash, responseHash, latencyMs, cancellationToken).ConfigureAwait(false);

            await EnsureLeaseRunningAsync(execution, cancellationToken).ConfigureAwait(false);

            var vadJson = JsonSerializer.Serialize(new
            {
                schemaVersion = "1",
                dialogueArtifactId = dialogue.Artifact.Id.ToString("N"),
                language = project.SourceLanguage,
                durationMs,
                regions = response.Regions.Select(r => new
                {
                    startMs = r.StartMs,
                    endMs = r.EndMs,
                    confidence = r.Confidence,
                }),
                confidence = response.Confidence,
                provider = provider.ToString(),
                model,
            });

            PublishResult published;
            using (var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(vadJson), writable: false))
            {
                published = await _artifacts.PublishAsync(
                    execution.TenantId, execution.ProjectId, execution.ProcessingRunId,
                    StageType.Vad, ArtifactType.VadRegions,
                    stream, ".json", "application/json",
                    provider.ToString(), model,
                    execution.ConfigurationHash, execution.ExecutionSnapshotHash,
                    [dialogue.Artifact.Id],
                    execution.Id,
                    cancellationToken).ConfigureAwait(false);
            }

            using (TenantContext.BeginScope(execution.TenantId))
            {
                using var db = _contextFactory.CreateDbContext();
                await db.Database.ExecuteSqlRawAsync(
                    "UPDATE artifacts SET metadata_json = {0} WHERE id = {1} AND tenant_id = {2}",
                    vadJson, published.ArtifactId, execution.TenantId).ConfigureAwait(false);
            }

            var stages = new StageExecutionService(_contextFactory, _retryOptions);
            await stages.CompleteAsync(
                execution.TenantId, execution.Id,
                execution.LeaseOwner, execution.LeaseToken,
                [published.ArtifactId.ToString("N")],
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
                nameof(StageType.Vad),
                [published.ArtifactId.ToString("N")]), cancellationToken).ConfigureAwait(false);
        }
        catch (LeaseLostException)
        {
            throw;
        }
#pragma warning disable CA1031 // Worker poison contract: permanent failures are recorded via TryFailAsync; transient rethrows.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogWarning(
                "VAD failed for run {RunId}: {Error}.",
                execution.ProcessingRunId, ex.Message);
            if (!await StageWorkerFailure.TryFailAsync(
                _contextFactory, _retryOptions, context, execution, ex,
                nameof(StageType.Vad), cancellationToken).ConfigureAwait(false))
            {
                throw;
            }
        }
    }

    private sealed record DialogueBundle(Artifact Artifact, ContentObject Content);

    private static void ValidateResponse(VadResponse response, int durationMs)
    {
        ArgumentNullException.ThrowIfNull(response);

        if (response.Regions is null)
        {
            throw new ErrorCodeException(
                ErrorCodes.ProviderInvalidResponse,
                "VAD provider returned no regions.");
        }

        if (double.IsNaN(response.Confidence))
        {
            throw new ErrorCodeException(
                ErrorCodes.ProviderInvalidResponse,
                "VAD provider returned an invalid confidence.");
        }

        foreach (var region in response.Regions)
        {
            if (region.StartMs < 0 || region.EndMs <= region.StartMs || region.EndMs > durationMs)
            {
                throw new ErrorCodeException(
                    ErrorCodes.ProviderInvalidResponse,
                    string.Concat(
                        "VAD region [",
                        region.StartMs.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        ",",
                        region.EndMs.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        "] is outside the media timeline."));
            }

            if (double.IsNaN(region.Confidence))
            {
                throw new ErrorCodeException(
                    ErrorCodes.ProviderInvalidResponse,
                    "VAD provider returned an invalid region confidence.");
            }
        }
    }

    private async Task RecordExecutionAsync(
        StageExecution execution,
        ProviderType provider,
        string model,
        VadResponse response,
        string requestHash,
        string responseHash,
        long latencyMs,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var scope = string.Concat(execution.ScopeType.ToString(), ":", execution.ScopeId);
        var idempotencyKey = ProviderExecutionRecorder.BuildIdempotencyKey(
            execution.ProcessingRunId, nameof(StageType.Vad), scope, execution.Attempt);
        string? externalJobId = null;
        if (response.RawMetadata is not null
            && response.RawMetadata.TryGetValue("mock.job_id", out var jobId)
            && !string.IsNullOrWhiteSpace(jobId))
        {
            externalJobId = jobId.Trim();
        }

        var row = new ProviderExecution(
            Guid.NewGuid(), execution.TenantId, execution.ProjectId, execution.ProcessingRunId,
            execution.Id, provider, ProviderCapability.Vad, model,
            response.ModelVersion, response.Deployment, null, null,
            execution.Attempt, requestHash, responseHash, Math.Max(0, latencyMs),
            response.Usage?.TokensIn, response.Usage?.TokensOut, response.Usage?.AudioSeconds,
            response.Usage?.EstimatedCostUsd, response.Usage?.EstimatedCostUsd, null,
            OutcomeClass.Success, null,
            null, null, null, externalJobId, idempotencyKey, now);
        await _recorder.RecordAsync(row, cancellationToken).ConfigureAwait(false);
        PlatformMetrics.ProviderCall(execution.TenantId, provider.ToString(), model);
        PlatformMetrics.ObserveProviderLatency(Math.Max(0, latencyMs), provider.ToString());
        if (response.Usage?.EstimatedCostUsd.HasValue == true)
        {
            PlatformMetrics.ProviderCostObserved(execution.TenantId, response.Usage.EstimatedCostUsd.Value, provider.ToString());
        }
    }

    private async Task<DialogueBundle> ResolveDialogueAsync(
        Guid tenantId,
        Guid projectId,
        Guid runId,
        string? inputHash,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(inputHash)
            && Guid.TryParseExact(inputHash.Trim(), "N", out var inputId)
            && inputId != Guid.Empty)
        {
            return await LoadDialogueByIdAsync(tenantId, runId, inputId, cancellationToken).ConfigureAwait(false);
        }

        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();

            var separationOutput = await db.Set<StageExecution>()
                .AsNoTracking()
                .Where(e => e.ProcessingRunId == runId && e.StageType == StageType.SourceSeparation)
                .OrderByDescending(e => e.CompletedAt)
                .Select(e => e.OutputArtifactIdsJson)
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(separationOutput))
            {
                try
                {
                    var ids = JsonSerializer.Deserialize<string[]>(separationOutput);
                    if (ids is not null && ids.Length > 0
                        && Guid.TryParseExact(ids[0].Trim(), "N", out var selected)
                        && selected != Guid.Empty)
                    {
                        var artifact = await db.Set<Artifact>()
                            .AsNoTracking()
                            .FirstOrDefaultAsync(a => a.Id == selected, cancellationToken).ConfigureAwait(false);
                        if (artifact is not null && artifact.TenantId == tenantId && artifact.ProcessingRunId == runId)
                        {
                            var content = await db.Set<ContentObject>()
                                .AsNoTracking()
                                .FirstOrDefaultAsync(c => c.Id == artifact.ContentObjectId, cancellationToken).ConfigureAwait(false);
                            if (content is not null && content.Status == ContentObjectStatus.Committed)
                            {
                                return new DialogueBundle(artifact, content);
                            }
                        }
                    }
                }
                catch (JsonException)
                {
                    // Fall through to artifact scan below.
                }
            }

            var fallback = await db.Set<Artifact>()
                .AsNoTracking()
                .Where(a => a.ProcessingRunId == runId
                    && (a.Type == ArtifactType.DialogueStem || a.Type == ArtifactType.CanonicalAudio))
                .OrderByDescending(a => a.Type == ArtifactType.DialogueStem)
                .ThenByDescending(a => a.CreatedAt)
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (fallback is null)
            {
                throw new ErrorCodeException(ErrorCodes.ArtifactUnavailable, "Dialogue audio artifact was not found.");
            }

            var fallbackContent = await db.Set<ContentObject>()
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == fallback.ContentObjectId, cancellationToken).ConfigureAwait(false);
            if (fallbackContent is null || fallbackContent.Status != ContentObjectStatus.Committed)
            {
                throw new ErrorCodeException(ErrorCodes.ArtifactUnavailable, "Dialogue audio content is unavailable.");
            }

            return new DialogueBundle(fallback, fallbackContent);
        }
    }

    private async Task<DialogueBundle> LoadDialogueByIdAsync(
        Guid tenantId,
        Guid runId,
        Guid artifactId,
        CancellationToken cancellationToken)
    {
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            var artifact = await db.Set<Artifact>()
                .AsNoTracking()
                .FirstOrDefaultAsync(a => a.Id == artifactId, cancellationToken).ConfigureAwait(false);
            if (artifact is null || artifact.TenantId != tenantId || artifact.ProcessingRunId != runId)
            {
                throw new ErrorCodeException(ErrorCodes.ArtifactUnavailable, "Dialogue audio artifact was not found.");
            }

            var content = await db.Set<ContentObject>()
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == artifact.ContentObjectId, cancellationToken).ConfigureAwait(false);
            if (content is null || content.Status != ContentObjectStatus.Committed)
            {
                throw new ErrorCodeException(ErrorCodes.ArtifactUnavailable, "Dialogue audio content is unavailable.");
            }

            return new DialogueBundle(artifact, content);
        }
    }

    private async Task<int> LoadDurationMsAsync(Guid tenantId, Guid projectId, CancellationToken cancellationToken)
    {
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            var duration = await db.Set<MediaAsset>()
                .Where(a => a.ProjectId == projectId && a.Status == MediaAssetStatus.Valid)
                .OrderBy(a => a.CreatedAt)
                .Select(a => (int?)a.DurationMs)
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            return Math.Max(0, duration ?? 0);
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

    private async Task EnsureLeaseRunningAsync(StageExecution execution, CancellationToken cancellationToken)
    {
        using (TenantContext.BeginScope(execution.TenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            var current = await db.Set<StageExecution>()
                .AsNoTracking()
                .FirstOrDefaultAsync(e => e.Id == execution.Id, cancellationToken).ConfigureAwait(false);
            if (current is null
                || current.Status != StageStatus.Running
                || !string.Equals(current.LeaseOwner, execution.LeaseOwner, StringComparison.Ordinal)
                || !string.Equals(current.LeaseToken, execution.LeaseToken, StringComparison.Ordinal))
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
}
