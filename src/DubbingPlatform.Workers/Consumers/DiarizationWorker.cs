using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json;
using DubbingPlatform.Application.Abstractions;
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
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DubbingPlatform.Workers.Consumers;

/// <summary>
/// Run-scoped (project scope, single unit) speaker diarization on
/// <c>ai.provider</c> (the workload queue for Diarization per
/// <c>WorkQueueRouter</c>). Claims via <see cref="BaseConsumer{TMessage}"/>
/// (at-least-once, lease fenced), ignores other stages sharing the queue
/// before claiming, loads the run segments (empty segments complete with an
/// empty map and no provider call), resolves the dialogue artifact (separation
/// output when present, else canonical) for the provider call plus the
/// segments artifact for lineage (<c>parents=[segments]</c>), routes via
/// <see cref="ProviderResolver"/> (capability <c>Diarization</c>), calls
/// <c>DiarizeAsync</c> with in-process transient retry inside the
/// <c>Retry</c> stage budget (exhausted transients rethrow into transport
/// retry via <see cref="StageWorkerFailure"/>), aligns provider time windows
/// to segments by maximum overlap, persists one <c>DiarizationMap</c> JSON
/// artifact (schema <c>v1</c>), maps labels via <see cref="DiarizationService"/>
/// (speakers never deleted; conflicting retry labels reassign while old rows
/// remain), records a <c>ProviderExecution</c> row, completes the execution,
/// and publishes <c>StageCompleted</c>. Permanent provider failures fall back
/// to a single speaker with a <c>DIARIZATION_FALLBACK</c> warning when
/// <c>Media:Diarization:FallbackToSingleSpeaker</c> is true (default),
/// otherwise fail with <c>PROVIDER_FAILED</c>. Non-Mock resolver results have
/// no injected adapter and follow the same permanent-failure path.
/// </summary>
public sealed class DiarizationWorker : BaseConsumer<StageWorkRequested>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IStageExecutionContextFactory _contextFactory;
    private readonly IOptions<RetryOptions> _retryOptions;
    private readonly IDiarizationProvider _diarization;
    private readonly ProviderResolver _resolver;
    private readonly ProviderExecutionRecorder _recorder;
    private readonly ArtifactService _artifacts;
    private readonly DiarizationService _mapping;
    private readonly DiarizationOptions _diarizationOptions;
    private readonly ILogger<DiarizationWorker> _logger;

    public DiarizationWorker(
        IStageExecutionContextFactory contextFactory,
        IOptions<RetryOptions> retryOptions,
        IDiarizationProvider diarization,
        ProviderResolver resolver,
        ProviderExecutionRecorder recorder,
        ArtifactService artifacts,
        DiarizationService mapping,
        IOptions<DiarizationOptions> diarizationOptions,
        ILogger<DiarizationWorker> logger,
        IDeferredSender? deferredSender = null)
        : base(contextFactory, retryOptions, deferredSender)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        ArgumentNullException.ThrowIfNull(retryOptions);
        ArgumentNullException.ThrowIfNull(diarization);
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(recorder);
        ArgumentNullException.ThrowIfNull(artifacts);
        ArgumentNullException.ThrowIfNull(mapping);
        ArgumentNullException.ThrowIfNull(diarizationOptions);
        ArgumentNullException.ThrowIfNull(logger);
        _contextFactory = contextFactory;
        _retryOptions = retryOptions;
        _diarization = diarization;
        _resolver = resolver;
        _recorder = recorder;
        _artifacts = artifacts;
        _mapping = mapping;
        _diarizationOptions = diarizationOptions.Value;
        _logger = logger;
    }

    protected override bool ShouldProcess(StageWorkRequested message)
    {
        var stage = message.StageTypeRequired ?? message.StageType;
        return string.Equals(stage?.Trim(), nameof(StageType.Diarization), StringComparison.Ordinal);
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
            if (execution.StageType != StageType.Diarization)
            {
                throw new DomainException($"Stage execution '{execution.Id}' is '{execution.StageType}', not Diarization.");
            }

            var project = await LoadOwnedProjectAsync(
                execution.TenantId, execution.ProjectId, cancellationToken).ConfigureAwait(false);
            var segmentsArtifactId = await ResolveSegmentsArtifactIdAsync(execution, cancellationToken).ConfigureAwait(false);
            var segments = await LoadSegmentsAsync(execution, cancellationToken).ConfigureAwait(false);

            if (segments.Count == 0)
            {
                await CompleteEmptyAsync(execution, context, segmentsArtifactId, cancellationToken).ConfigureAwait(false);
                return;
            }

            var dialogue = await ResolveDialogueAsync(
                execution.TenantId, execution.ProjectId, execution.ProcessingRunId,
                cancellationToken).ConfigureAwait(false);
            var durationMs = await LoadDurationMsAsync(execution, segments, cancellationToken).ConfigureAwait(false);

            ProviderType provider;
            string model;
            try
            {
                (provider, model) = await _resolver.ResolveAsync(
                    ProviderCapability.Diarization,
                    execution.TenantId,
                    project.SourceLanguage,
                    dialogue.Content.SizeBytes,
                    durationMs,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (LeaseLostException)
            {
                throw;
            }
#pragma warning disable CA1031 // Fallback contract: resolver failures fall back to single speaker unless fallback is disabled.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                await HandlePermanentProviderFailureAsync(
                    execution, context, segments, segmentsArtifactId,
                    providerName: null, model: null,
                    requestHash: null, responseHash: null, latencyMs: 0, usage: null,
                    failure: ex, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (provider != ProviderType.Mock)
            {
                var unsupported = new ErrorCodeException(
                    ErrorCodes.ProviderConfigurationError,
                    string.Concat("No diarization adapter for provider '", provider.ToString(), "'."));
                await HandlePermanentProviderFailureAsync(
                    execution, context, segments, segmentsArtifactId,
                    provider.ToString(), model,
                    requestHash: null, responseHash: null, latencyMs: 0, usage: null,
                    unsupported, cancellationToken).ConfigureAwait(false);
                return;
            }

            var request = new DiarizationRequest(
                execution.TenantId, execution.ProjectId, execution.ProcessingRunId,
                dialogue.Artifact.Id.ToString("N"),
                project.SourceLanguage,
                dialogue.Content.SizeBytes,
                durationMs,
                "flac");
            var requestHash = ConfigurationHashCalculator.Compute(request);

            var maxAttempts = MaxAttempts();
            DiarizationResponse? response = null;
            long latencyMs = 0;
            Exception? lastTransient = null;
            for (var attempt = 0; attempt < maxAttempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await EnsureLeaseRunningAsync(execution, cancellationToken).ConfigureAwait(false);

                var stopwatch = Stopwatch.StartNew();
                try
                {
                    response = await _diarization.DiarizeAsync(request, cancellationToken).ConfigureAwait(false);
                    stopwatch.Stop();
                    latencyMs = stopwatch.ElapsedMilliseconds;
                    lastTransient = null;
                    break;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (LeaseLostException)
                {
                    throw;
                }
#pragma warning disable CA1031 // Retry-budget contract: transient provider failures retry, permanent fall back (or fail when fallback is disabled).
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    stopwatch.Stop();
                    latencyMs = stopwatch.ElapsedMilliseconds;
                    if (IsTransientForRetry(ex) && attempt + 1 < maxAttempts)
                    {
                        lastTransient = ex;
                        continue;
                    }

                    if (IsTransientForRetry(ex))
                    {
                        throw;
                    }

                    await HandlePermanentProviderFailureAsync(
                        execution, context, segments, segmentsArtifactId,
                        provider.ToString(), model,
                        requestHash, responseHash: null, latencyMs, usage: null,
                        ex, cancellationToken).ConfigureAwait(false);
                    return;
                }
            }

            if (response is null)
            {
                if (lastTransient is not null)
                {
                    throw lastTransient;
                }

                var empty = new ErrorCodeException(
                    ErrorCodes.ProviderInvalidResponse,
                    "Diarization provider returned no result.");
                await HandlePermanentProviderFailureAsync(
                    execution, context, segments, segmentsArtifactId,
                    provider.ToString(), model,
                    requestHash, responseHash: null, latencyMs, usage: null,
                    empty, cancellationToken).ConfigureAwait(false);
                return;
            }

            try
            {
                ValidateResponse(response, durationMs);
            }
            catch (Exception ex)
            {
                await HandlePermanentProviderFailureAsync(
                    execution, context, segments, segmentsArtifactId,
                    provider.ToString(), model,
                    requestHash,
                    ConfigurationHashCalculator.Compute(new { labels = response.SpeakerLabels, model = response.Model }),
                    latencyMs, response.Usage,
                    ex, cancellationToken).ConfigureAwait(false);
                return;
            }

            var responseHash = ConfigurationHashCalculator.Compute(new
            {
                segments = response.Segments.Select(s => new
                {
                    label = s.SpeakerLabel,
                    startMs = s.StartMs,
                    endMs = s.EndMs,
                    confidence = s.Confidence,
                }),
                confidence = response.Confidence,
                model = response.Model,
            });

            await RecordExecutionAsync(
                execution, provider, model, response,
                OutcomeClass.Success, fallbackReason: null,
                requestHash, responseHash, latencyMs, cancellationToken).ConfigureAwait(false);

            var labels = AlignLabels(segments, response.Segments);

            await EnsureLeaseRunningAsync(execution, cancellationToken).ConfigureAwait(false);
            var mapped = await _mapping.MapAsync(
                execution.TenantId, execution.ProjectId, execution.ProcessingRunId,
                segments, labels, provider.ToString(), cancellationToken).ConfigureAwait(false);

            await EnsureLeaseRunningAsync(execution, cancellationToken).ConfigureAwait(false);
            var published = await PublishMapAsync(
                execution, segmentsArtifactId, dialogue.Artifact.Id,
                project.SourceLanguage, durationMs, segments,
                mapped, provider.ToString(), model,
                isFallback: false, fallbackReason: null, cancellationToken).ConfigureAwait(false);

            var stages = new StageExecutionService(_contextFactory, _retryOptions);
            await stages.CompleteAsync(
                execution.TenantId, execution.Id,
                execution.LeaseOwner, execution.LeaseToken,
                [published.ArtifactId.ToString("N")],
                cancellationToken).ConfigureAwait(false);

            await PublishCompletedAsync(context, execution, published.ArtifactId, cancellationToken).ConfigureAwait(false);
        }
        catch (LeaseLostException)
        {
            throw;
        }
#pragma warning disable CA1031 // Worker poison contract: permanent failures are recorded via TryFailAsync; transient rethrows.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            // Single-speaker fallbacks already completed inside
            // HandlePermanentProviderFailureAsync and return normally, so any
            // exception reaching here is a real stage failure (or a transient
            // that must retry). Never log voice biometrics: only ids and counts.
            _logger.LogWarning(
                "Diarization failed for run {RunId}: {Error}.",
                execution.ProcessingRunId, ex.Message);
            if (!await StageWorkerFailure.TryFailAsync(
                _contextFactory, _retryOptions, context, execution, ex,
                nameof(StageType.Diarization), cancellationToken).ConfigureAwait(false))
            {
                throw;
            }
        }
    }

    private async Task HandlePermanentProviderFailureAsync(
        StageExecution execution,
        ConsumeContext<StageWorkRequested> context,
        IReadOnlyList<SpeechSegment> segments,
        Guid segmentsArtifactId,
        string? providerName,
        string? model,
        string? requestHash,
        string? responseHash,
        long latencyMs,
        ProviderUsage? usage,
        Exception failure,
        CancellationToken cancellationToken)
    {
        if (failure is OperationCanceledException or LeaseLostException)
        {
            throw failure;
        }

        var reason = string.Concat(
            "Diarization failed (",
            Truncate(ClassifyMessage(failure)),
            "); single speaker selected.");

        if (!_diarizationOptions.FallbackToSingleSpeaker)
        {
            throw new ErrorCodeException(ErrorCodes.ProviderFailed, reason);
        }

        if (requestHash is not null)
        {
            await RecordExecutionAsync(
                execution,
                TryParseProvider(providerName),
                string.IsNullOrWhiteSpace(model) ? "mock-default" : model.Trim(),
                response: null,
                MapOutcome(failure), reason,
                requestHash, responseHash, latencyMs, cancellationToken).ConfigureAwait(false);
        }

        await EnsureLeaseRunningAsync(execution, cancellationToken).ConfigureAwait(false);
        var mapped = await _mapping.MapSingleSpeakerFallbackAsync(
            execution.TenantId, execution.ProjectId, execution.ProcessingRunId,
            segments, reason, cancellationToken).ConfigureAwait(false);

        await EnsureLeaseRunningAsync(execution, cancellationToken).ConfigureAwait(false);
        var published = await PublishMapAsync(
            execution, segmentsArtifactId, dialogueArtifactId: null,
            language: null, durationMs: null, segments,
            mapped, providerName, model,
            isFallback: true, fallbackReason: reason, cancellationToken).ConfigureAwait(false);

        var stages = new StageExecutionService(_contextFactory, _retryOptions);
        await stages.CompleteAsync(
            execution.TenantId, execution.Id,
            execution.LeaseOwner, execution.LeaseToken,
            [published.ArtifactId.ToString("N")],
            cancellationToken).ConfigureAwait(false);
        await stages.NoteFallbackAsync(
            execution.TenantId, execution.Id,
            execution.LeaseOwner, execution.LeaseToken,
            DiarizationService.FallbackCode, reason, cancellationToken).ConfigureAwait(false);
        await CreateQualityWarningAsync(execution, published.ArtifactId, reason, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Diarization fallback for run {RunId}: {Reason}.",
            execution.ProcessingRunId, reason);

        await PublishCompletedAsync(context, execution, published.ArtifactId, cancellationToken).ConfigureAwait(false);
    }

    private static List<DiarLabel> AlignLabels(
        IReadOnlyList<SpeechSegment> segments,
        IReadOnlyList<DiarizationSegment> providerSegments)
    {
        var labels = new List<DiarLabel>(segments.Count);
        for (var i = 0; i < segments.Count; i++)
        {
            var segment = segments[i];
            var bestIndex = -1;
            long bestOverlap = 0;
            for (var j = 0; j < providerSegments.Count; j++)
            {
                var provider = providerSegments[j];
                var overlap = Math.Min(segment.EndMs, provider.EndMs) - Math.Max(segment.StartMs, provider.StartMs);
                if (overlap > bestOverlap)
                {
                    bestOverlap = overlap;
                    bestIndex = j;
                }
            }

            if (bestIndex < 0)
            {
                continue;
            }

            var best = providerSegments[bestIndex];
            labels.Add(new DiarLabel(segment.Id, best.SpeakerLabel.Trim(), best.Confidence));
        }

        return labels;
    }

    private static void ValidateResponse(DiarizationResponse response, int durationMs)
    {
        ArgumentNullException.ThrowIfNull(response);

        if (response.Segments is null || response.SpeakerLabels is null)
        {
            throw new ErrorCodeException(
                ErrorCodes.ProviderInvalidResponse,
                "Diarization provider returned no segments.");
        }

        if (double.IsNaN(response.Confidence))
        {
            throw new ErrorCodeException(
                ErrorCodes.ProviderInvalidResponse,
                "Diarization provider returned an invalid confidence.");
        }

        foreach (var segment in response.Segments)
        {
            if (string.IsNullOrWhiteSpace(segment.SpeakerLabel))
            {
                throw new ErrorCodeException(
                    ErrorCodes.ProviderInvalidResponse,
                    "Diarization provider returned an empty speaker label.");
            }

            if (segment.StartMs < 0 || segment.EndMs <= segment.StartMs || segment.EndMs > durationMs)
            {
                throw new ErrorCodeException(
                    ErrorCodes.ProviderInvalidResponse,
                    string.Concat(
                        "Diarization segment [",
                        segment.StartMs.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        ",",
                        segment.EndMs.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        "] is outside the media timeline."));
            }

            if (double.IsNaN(segment.Confidence))
            {
                throw new ErrorCodeException(
                    ErrorCodes.ProviderInvalidResponse,
                    "Diarization provider returned an invalid segment confidence.");
            }
        }
    }

    private async Task<PublishResult> PublishMapAsync(
        StageExecution execution,
        Guid segmentsArtifactId,
        Guid? dialogueArtifactId,
        string? language,
        int? durationMs,
        IReadOnlyList<SpeechSegment> segments,
        DiarizationMapResult mapped,
        string? providerName,
        string? model,
        bool isFallback,
        string? fallbackReason,
        CancellationToken cancellationToken)
    {
        var byId = segments.ToDictionary(s => s.Id);
        var mapJson = JsonSerializer.Serialize(new
        {
            schemaVersion = "1",
            segmentsArtifactId = segmentsArtifactId == Guid.Empty ? null : segmentsArtifactId.ToString("N"),
            dialogueArtifactId = dialogueArtifactId?.ToString("N"),
            language,
            durationMs,
            fallback = isFallback,
            fallbackReason,
            provider = providerName,
            model,
            speakers = mapped.Speakers.Select(s => new
            {
                speakerId = s.SpeakerId.ToString("N"),
                speakerKey = s.SpeakerKey,
                displayName = s.DisplayName,
                providerLabel = s.ProviderLabel,
                confidence = s.Confidence,
                firstAppearanceMs = s.FirstAppearanceMs,
                lastAppearanceMs = s.LastAppearanceMs,
                mappingMethod = s.MappingMethod,
                mappingVersion = s.MappingVersion,
            }),
            mappings = mapped.SegmentToSpeaker.Select(pair => new
            {
                segmentId = pair.Key.ToString("N"),
                sequence = byId.TryGetValue(pair.Key, out var seg) ? seg.Sequence : 0,
                startMs = byId.TryGetValue(pair.Key, out var start) ? start.StartMs : 0,
                endMs = byId.TryGetValue(pair.Key, out var end) ? end.EndMs : 0,
                speakerId = pair.Value.ToString("N"),
                label = mapped.Speakers.FirstOrDefault(s => s.SpeakerId == pair.Value)?.ProviderLabel,
            }),
        });

        var parents = segmentsArtifactId == Guid.Empty
            ? []
            : new[] { segmentsArtifactId };

        PublishResult published;
        using (var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(mapJson), writable: false))
        {
            published = await _artifacts.PublishAsync(
                execution.TenantId, execution.ProjectId, execution.ProcessingRunId,
                StageType.Diarization, ArtifactType.DiarizationMap,
                stream, ".json", "application/json",
                providerName, model,
                execution.ConfigurationHash, execution.ExecutionSnapshotHash,
                parents,
                execution.Id,
                cancellationToken).ConfigureAwait(false);
        }

        using (TenantContext.BeginScope(execution.TenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            await db.Database.ExecuteSqlRawAsync(
                "UPDATE artifacts SET metadata_json = {0} WHERE id = {1} AND tenant_id = {2}",
                mapJson, published.ArtifactId, execution.TenantId).ConfigureAwait(false);
        }

        return published;
    }

    private async Task CompleteEmptyAsync(
        StageExecution execution,
        ConsumeContext<StageWorkRequested> context,
        Guid segmentsArtifactId,
        CancellationToken cancellationToken)
    {
        await EnsureLeaseRunningAsync(execution, cancellationToken).ConfigureAwait(false);
        var empty = new DiarizationMapResult([], new Dictionary<Guid, Guid>(), false, null);
        var published = await PublishMapAsync(
            execution, segmentsArtifactId, dialogueArtifactId: null,
            language: null, durationMs: null, segments: [],
            empty, providerName: null, model: null,
            isFallback: false, fallbackReason: null, cancellationToken).ConfigureAwait(false);

        var stages = new StageExecutionService(_contextFactory, _retryOptions);
        await stages.CompleteAsync(
            execution.TenantId, execution.Id,
            execution.LeaseOwner, execution.LeaseToken,
            [published.ArtifactId.ToString("N")],
            cancellationToken).ConfigureAwait(false);

        await PublishCompletedAsync(context, execution, published.ArtifactId, cancellationToken).ConfigureAwait(false);
    }

    private async Task RecordExecutionAsync(
        StageExecution execution,
        ProviderType provider,
        string model,
        DiarizationResponse? response,
        OutcomeClass outcome,
        string? fallbackReason,
        string requestHash,
        string? responseHash,
        long latencyMs,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var scope = string.Concat(execution.ScopeType.ToString(), ":", execution.ScopeId);
        var idempotencyKey = ProviderExecutionRecorder.BuildIdempotencyKey(
            execution.ProcessingRunId, nameof(StageType.Diarization), scope, execution.Attempt);
        string? externalJobId = null;
        if (response?.RawMetadata is not null
            && response.RawMetadata.TryGetValue("mock.job_id", out var jobId)
            && !string.IsNullOrWhiteSpace(jobId))
        {
            externalJobId = jobId.Trim();
        }

        var row = new ProviderExecution(
            Guid.NewGuid(), execution.TenantId, execution.ProjectId, execution.ProcessingRunId,
            execution.Id, provider, ProviderCapability.Diarization, model,
            response?.ModelVersion, response?.Deployment, null, null,
            execution.Attempt, requestHash, responseHash, Math.Max(0, latencyMs),
            response?.Usage?.TokensIn, response?.Usage?.TokensOut, response?.Usage?.AudioSeconds,
            response?.Usage?.EstimatedCostUsd, response?.Usage?.EstimatedCostUsd, null,
            outcome,
            string.IsNullOrWhiteSpace(fallbackReason) ? null : Truncate(fallbackReason),
            null, null, null, externalJobId, idempotencyKey, now);
        await _recorder.RecordAsync(row, cancellationToken).ConfigureAwait(false);
    }

    private async Task CreateQualityWarningAsync(
        StageExecution execution,
        Guid artifactId,
        string reason,
        CancellationToken cancellationToken)
    {
        var details = JsonSerializer.Serialize(new
        {
            code = DiarizationService.FallbackCode,
            reason,
            artifactId = artifactId.ToString("N"),
        });

        var row = new QualityResult(
            Guid.NewGuid(), execution.TenantId, execution.ProjectId, execution.ProcessingRunId,
            execution.ScopeType, execution.ScopeId, execution.SegmentId,
            QualityStatus.PassWithWarnings, DiarizationService.FallbackCode, "Warning",
            Truncate(reason), details, artifactId, DateTimeOffset.UtcNow);

        using (TenantContext.BeginScope(execution.TenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            db.Set<QualityResult>().Add(row);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task PublishCompletedAsync(
        ConsumeContext<StageWorkRequested> context,
        StageExecution execution,
        Guid artifactId,
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
            nameof(StageType.Diarization),
            [artifactId.ToString("N")]), cancellationToken).ConfigureAwait(false);
    }

    private sealed record DialogueBundle(Artifact Artifact, ContentObject Content);

    private async Task<Guid> ResolveSegmentsArtifactIdAsync(
        StageExecution execution,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(execution.InputHash)
            && Guid.TryParseExact(execution.InputHash.Trim(), "N", out var inputId)
            && inputId != Guid.Empty)
        {
            return inputId;
        }

        using (TenantContext.BeginScope(execution.TenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            return await db.Set<Artifact>()
                .Where(a => a.ProcessingRunId == execution.ProcessingRunId && a.Type == ArtifactType.Segments)
                .OrderByDescending(a => a.CreatedAt)
                .Select(a => a.Id)
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<IReadOnlyList<SpeechSegment>> LoadSegmentsAsync(
        StageExecution execution,
        CancellationToken cancellationToken)
    {
        using (TenantContext.BeginScope(execution.TenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            return await db.Set<SpeechSegment>()
                .AsNoTracking()
                .Where(s => s.RunId == execution.ProcessingRunId)
                .OrderBy(s => s.Sequence)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<DialogueBundle> ResolveDialogueAsync(
        Guid tenantId,
        Guid projectId,
        Guid runId,
        CancellationToken cancellationToken)
    {
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
                    var ids = JsonSerializer.Deserialize<string[]>(separationOutput, JsonOptions);
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

    private async Task<int> LoadDurationMsAsync(
        StageExecution execution,
        IReadOnlyList<SpeechSegment> segments,
        CancellationToken cancellationToken)
    {
        using (TenantContext.BeginScope(execution.TenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            var duration = await db.Set<MediaAsset>()
                .Where(a => a.ProjectId == execution.ProjectId && a.Status == MediaAssetStatus.Valid)
                .OrderBy(a => a.CreatedAt)
                .Select(a => (int?)a.DurationMs)
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (duration.HasValue && duration.Value > 0)
            {
                return duration.Value;
            }
        }

        return segments.Count == 0 ? 0 : segments.Max(s => s.EndMs);
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

    private int MaxAttempts()
    {
        var retry = _retryOptions.Value;
        if (retry.PerStageMaxAttempts.TryGetValue(nameof(StageType.Diarization), out var perStage))
        {
            return Math.Clamp(perStage, 1, 10);
        }

        return Math.Clamp(retry.LogicalStageMaxAttempts, 1, 10);
    }

    private static bool IsTransientForRetry(Exception exception)
    {
        if (exception is HttpRequestException or TimeoutException or SocketException or IOException)
        {
            return true;
        }

        if (exception is ErrorCodeException coded)
        {
            return string.Equals(coded.ErrorCode, ErrorCodes.ProviderRateLimited, StringComparison.Ordinal)
                || string.Equals(coded.ErrorCode, ErrorCodes.ProviderTimeout, StringComparison.Ordinal)
                || string.Equals(coded.ErrorCode, ErrorCodes.ProviderQuotaExhausted, StringComparison.Ordinal)
                || string.Equals(coded.ErrorCode, ErrorCodes.ProviderFailed, StringComparison.Ordinal);
        }

        return false;
    }

    private static OutcomeClass MapOutcome(Exception exception)
    {
        if (exception is ErrorCodeException coded)
        {
            return coded.ErrorCode switch
            {
                ErrorCodes.ProviderRateLimited => OutcomeClass.ProviderRateLimited,
                ErrorCodes.ProviderTimeout => OutcomeClass.ProviderTimeout,
                ErrorCodes.ProviderInvalidResponse => OutcomeClass.ProviderInvalidResponse,
                ErrorCodes.ProviderConfigurationError => OutcomeClass.UnsupportedCapability,
                ErrorCodes.PolicyDenied => OutcomeClass.PolicyRejected,
                ErrorCodes.ProviderQuotaExhausted => OutcomeClass.ProviderUnavailable,
                ErrorCodes.ProviderFailed => OutcomeClass.ProviderTransientFailure,
                _ => OutcomeClass.ProviderPermanentFailure,
            };
        }

        if (exception is DomainException)
        {
            return OutcomeClass.ProviderPermanentFailure;
        }

        return OutcomeClass.ProviderTransientFailure;
    }

    private static ProviderType TryParseProvider(string? providerName)
    {
        if (Enum.TryParse<ProviderType>(providerName ?? string.Empty, ignoreCase: true, out var provider))
        {
            return provider;
        }

        return ProviderType.Mock;
    }

    private static string ClassifyMessage(Exception exception)
    {
        if (string.IsNullOrWhiteSpace(exception.Message))
        {
            return "provider error";
        }

        return exception.Message.Trim();
    }

    private static string Truncate(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return "Diarization fallback.";
        }

        var trimmed = message.Trim();
        return trimmed.Length <= 500 ? trimmed : trimmed.Substring(0, 500);
    }
}
