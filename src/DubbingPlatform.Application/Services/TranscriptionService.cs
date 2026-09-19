using System.Diagnostics;
using System.Globalization;
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
using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Domain.Enums;
using DubbingPlatform.Domain.Exceptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DubbingPlatform.Application.Services;

/// <summary>
/// Outcome of one segment transcription call. <see cref="NeedsReview"/> is
/// true only for the persistent-low-confidence terminal path (review item
/// created, winning version flagged, execution moved to
/// <c>ManualReviewRequired</c>); otherwise the winner is completed.
/// </summary>
public sealed record TranscriptionSegmentResult(
    Guid SegmentId,
    Guid SelectedVersionId,
    Guid WordsArtifactId,
    double Confidence,
    string Provider,
    string Model,
    bool NeedsReview,
    Guid? ReviewItemId);

/// <summary>
/// Low confidence with attempts remaining. The worker converts this into a
/// lease-fenced <c>Failed</c> execution plus a retryable <c>StageFailed</c> so
/// the saga dispatches the next attempt (rate-limit codes are deferred by the
/// saga); it never flows through the permanent-failure path.
/// </summary>
public sealed class TranscriptionRetryableException : AppException
{
    public TranscriptionRetryableException(Guid segmentId, double confidence, double threshold)
        : base(ErrorCodes.ProviderFailed, BuildMessage(segmentId, confidence, threshold))
    {
        SegmentId = segmentId;
        Confidence = confidence;
        Threshold = threshold;
    }

    public Guid SegmentId { get; }

    public double Confidence { get; }

    public double Threshold { get; }

    public override int StatusCode => ErrorCodes.StatusFor(ErrorCode);

    private static string BuildMessage(Guid segmentId, double confidence, double threshold)
    {
        return string.Concat(
            "Transcript confidence ",
            confidence.ToString("F2", CultureInfo.InvariantCulture),
            " is below threshold ",
            threshold.ToString("F2", CultureInfo.InvariantCulture),
            " for segment '",
            segmentId.ToString("D"),
            "'; retrying within stage budget.");
    }
}

/// <summary>
/// One persisted version ranked by <see cref="TranscriptionService.PickBest"/>.
/// </summary>
public sealed record TranscriptCandidate(
    Guid VersionId,
    string Provider,
    double Confidence,
    int TextLength,
    int WordCount,
    DateTimeOffset CreatedAt);

/// <summary>
/// Segment-scoped transcription with versioned transcripts, deterministic
/// best-version selection, one compatible fallback attempt, and review routing.
/// Flow per call: resolve the provider (capability <c>Transcription</c>),
/// check the capability cost gate, slice the segment audio to 16kHz mono wav
/// via <see cref="IFFmpegService"/> (temp files always deleted), invoke the
/// provider, record a <c>ProviderExecution</c> for every call, persist one
/// <c>Transcript</c> words artifact (schema <c>v1</c>) plus one
/// <c>TranscriptVersion</c> per attempt/provider (alternatives accumulate,
/// never deleted), then apply the confidence policy: at or above
/// <c>Transcription:ConfidenceThreshold</c> selects the deterministic winner
/// and completes; below threshold tries one fallback provider when a second
/// compatible invocable descriptor exists; still below with attempts remaining
/// throws <see cref="TranscriptionRetryableException"/>; otherwise creates a
/// <c>ReviewItem</c> (<c>LOW_CONFIDENCE</c>, <c>Open</c>), flags the winner
/// <c>NeedsReview</c>, and moves the execution to
/// <c>ManualReviewRequired</c> (the project never fails for one low-confidence
/// segment). Empty slices throw <c>VALIDATION_FAILED</c> (fail fast, no
/// retry); provider rate-limit codes propagate for delayed saga retry;
/// provider invalid responses fall back once, never transport-retry.
/// </summary>
public sealed class TranscriptionService
{
    /// <summary>Review reason for persistent low confidence.</summary>
    public const string ReviewReason = "LOW_CONFIDENCE";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IStageExecutionContextFactory _contextFactory;
    private readonly ArtifactService _artifacts;
    private readonly IArtifactStorage _storage;
    private readonly IFFmpegService _ffmpeg;
    private readonly ITranscriptionProvider _transcription;
    private readonly ProviderResolver _resolver;
    private readonly IDescriptorStore _descriptors;
    private readonly ProviderExecutionRecorder _recorder;
    private readonly IProviderCostGate _costGate;
    private readonly TranscriptionOptions _options;
    private readonly ProviderOptions _providers;
    private readonly RetryOptions _retry;
    private readonly ILogger<TranscriptionService> _logger;

    public TranscriptionService(
        IStageExecutionContextFactory contextFactory,
        ArtifactService artifacts,
        IArtifactStorage storage,
        IFFmpegService ffmpeg,
        ITranscriptionProvider transcription,
        ProviderResolver resolver,
        IDescriptorStore descriptors,
        ProviderExecutionRecorder recorder,
        IProviderCostGate costGate,
        IOptions<TranscriptionOptions> transcriptionOptions,
        IOptions<ProviderOptions> providerOptions,
        IOptions<RetryOptions> retryOptions,
        ILogger<TranscriptionService> logger)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        ArgumentNullException.ThrowIfNull(artifacts);
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(ffmpeg);
        ArgumentNullException.ThrowIfNull(transcription);
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(descriptors);
        ArgumentNullException.ThrowIfNull(recorder);
        ArgumentNullException.ThrowIfNull(costGate);
        ArgumentNullException.ThrowIfNull(transcriptionOptions);
        ArgumentNullException.ThrowIfNull(providerOptions);
        ArgumentNullException.ThrowIfNull(retryOptions);
        ArgumentNullException.ThrowIfNull(logger);
        _contextFactory = contextFactory;
        _artifacts = artifacts;
        _storage = storage;
        _ffmpeg = ffmpeg;
        _transcription = transcription;
        _resolver = resolver;
        _descriptors = descriptors;
        _recorder = recorder;
        _costGate = costGate;
        _options = transcriptionOptions.Value;
        _providers = providerOptions.Value;
        _retry = retryOptions.Value;
        _logger = logger;
    }

    /// <summary>
    /// Builds the provider-priority index for selection from
    /// <c>Providers:RoutePriority[Transcription]</c> (case-insensitive).
    /// Unknown providers sort last; ties break by name for determinism.
    /// Mirrors <see cref="ProviderResolver"/> ordering without hardcoding
    /// precedence.
    /// </summary>
    public static Dictionary<string, int> BuildPriorityIndex(ProviderOptions providers, string capability)
    {
        ArgumentNullException.ThrowIfNull(providers);
        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (providers.RoutePriority is null)
        {
            return index;
        }

        foreach (var pair in providers.RoutePriority)
        {
            if (!string.Equals(pair.Key?.Trim(), capability?.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var position = 0;
            foreach (var name in pair.Value ?? [])
            {
                if (!string.IsNullOrWhiteSpace(name) && !index.ContainsKey(name.Trim()))
                {
                    index[name.Trim()] = position++;
                }
            }

            return index;
        }

        return index;
    }

    /// <summary>
    /// Deterministic winner: confidence desc, provider-priority asc, text
    /// present, word count desc, earliest created, id order. Pure (no I/O) so
    /// unit tests cover it hermetically.
    /// </summary>
    public static TranscriptCandidate? PickBest(
        IReadOnlyList<TranscriptCandidate> candidates,
        IReadOnlyDictionary<string, int> priorityIndex)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(priorityIndex);

        TranscriptCandidate? best = null;
        foreach (var candidate in candidates)
        {
            ArgumentNullException.ThrowIfNull(candidate);
            if (best is null || Compare(candidate, best, priorityIndex) < 0)
            {
                best = candidate;
            }
        }

        return best;
    }

    private static int Compare(
        TranscriptCandidate left,
        TranscriptCandidate right,
        IReadOnlyDictionary<string, int> priorityIndex)
    {
        var confidence = right.Confidence.CompareTo(left.Confidence);
        if (confidence != 0)
        {
            return confidence;
        }

        var priority = Rank(left.Provider, priorityIndex).CompareTo(Rank(right.Provider, priorityIndex));
        if (priority != 0)
        {
            return priority;
        }

        var nameOrder = string.Compare(left.Provider, right.Provider, StringComparison.Ordinal);
        if (nameOrder != 0)
        {
            return nameOrder;
        }

        var completeness = (right.TextLength > 0).CompareTo(left.TextLength > 0);
        if (completeness != 0)
        {
            return completeness;
        }

        var words = right.WordCount.CompareTo(left.WordCount);
        if (words != 0)
        {
            return words;
        }

        var created = left.CreatedAt.CompareTo(right.CreatedAt);
        if (created != 0)
        {
            return created;
        }

        return string.Compare(left.VersionId.ToString("N"), right.VersionId.ToString("N"), StringComparison.Ordinal);
    }

    private static int Rank(string provider, IReadOnlyDictionary<string, int> priorityIndex)
    {
        if (!string.IsNullOrWhiteSpace(provider) && priorityIndex.TryGetValue(provider.Trim(), out var position))
        {
            return position;
        }

        return int.MaxValue;
    }

    /// <summary>
    /// Transcribes one segment end to end (provider calls, persistence,
    /// selection, confidence policy, stage commit). See class docs for the
    /// policy matrix. Terminal executions return their stored outcome
    /// (idempotent redelivery safe).
    /// </summary>
    public async Task<TranscriptionSegmentResult> TranscribeSegmentAsync(
        Guid tenantId,
        Guid projectId,
        Guid runId,
        Guid segmentId,
        int attempt,
        Guid executionId,
        string owner,
        string token,
        CancellationToken cancellationToken = default)
    {
        RequireTenant(tenantId);
        RequireId(projectId, nameof(projectId));
        RequireId(runId, nameof(runId));
        RequireId(segmentId, nameof(segmentId));
        RequireId(executionId, nameof(executionId));
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        if (attempt < 0)
        {
            throw new DomainException("Attempt must be >= 0.");
        }

        var project = await LoadOwnedProjectAsync(tenantId, projectId, cancellationToken).ConfigureAwait(false);
        var execution = await LoadExecutionAsync(tenantId, executionId, projectId, runId, segmentId, owner, token, cancellationToken).ConfigureAwait(false);

        var idempotent = await TryReturnIdempotentAsync(tenantId, execution, segmentId, cancellationToken).ConfigureAwait(false);
        if (idempotent is not null)
        {
            return idempotent;
        }

        await EnsureRunActiveAsync(tenantId, runId, projectId, cancellationToken).ConfigureAwait(false);

        var segment = await LoadSegmentAsync(tenantId, projectId, runId, segmentId, cancellationToken).ConfigureAwait(false);
        if (segment.DurationMs <= 0)
        {
            throw new ErrorCodeException(ErrorCodes.ValidationFailed, $"Segment '{segmentId:D}' has an empty audio slice; transcription cannot proceed.");
        }

        if (!await _costGate.CanProceedAsync(tenantId, ProviderCapability.Transcription, cancellationToken).ConfigureAwait(false))
        {
            throw new ErrorCodeException(ErrorCodes.QuotaExceeded, "Cost guard blocks transcription for the current tenant.");
        }

        var threshold = Clamp01(_options.ConfidenceThreshold);
        var maxAttempts = MaxAttempts();
        var estimatedBytes = checked((long)segment.DurationMs * 32L);

        ProviderType provider;
        string model;
        try
        {
            (provider, model) = await _resolver.ResolveAsync(
                ProviderCapability.Transcription,
                tenantId,
                project.SourceLanguage,
                estimatedBytes,
                segment.DurationMs,
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
#pragma warning disable CA1031 // Resolver failures are permanent provider-configuration failures with no transcript to persist.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            throw new ErrorCodeException(ErrorCodes.ProviderConfigurationError, $"No transcription route is available: {Truncate(ClassifyMessage(ex))}.");
        }

        if (provider != ProviderType.Mock)
        {
            throw new ErrorCodeException(
                ErrorCodes.ProviderConfigurationError,
                string.Concat("No transcription adapter for provider '", provider.ToString(), "'."));
        }

        var dialogue = await ResolveDialogueAsync(tenantId, projectId, runId, cancellationToken).ConfigureAwait(false);
        var segmentsArtifactId = await ResolveSegmentsArtifactIdAsync(tenantId, runId, cancellationToken).ConfigureAwait(false);

        var workDir = CreateTempWorkingDir();
        try
        {
            var sourcePath = Path.Combine(workDir, "source.flac");
            var slicePath = Path.Combine(workDir, "slice.wav");
            await DownloadToFileAsync(dialogue.Content.StorageKey, sourcePath, cancellationToken).ConfigureAwait(false);
            await _ffmpeg.ExtractSegmentSliceAsync(sourcePath, slicePath, segment.StartMs, segment.DurationMs, cancellationToken).ConfigureAwait(false);
            var sliceBytes = new FileInfo(slicePath).Length;

            var request = new TranscriptionRequest(
                tenantId, projectId, runId,
                segmentId.ToString("N"),
                project.SourceLanguage,
                sliceBytes,
                segment.DurationMs,
                "wav",
                true,
                false);
            var requestHash = ConfigurationHashCalculator.Compute(request);

            TranscriptionCallResult? primary = null;
            ErrorCodeException? primaryInvalid = null;
            try
            {
                primary = await InvokeAsync(execution, provider, model, request, requestHash, cancellationToken).ConfigureAwait(false);
            }
            catch (ErrorCodeException ex) when (IsInvalidResponse(ex))
            {
                primaryInvalid = ex;
            }

            TranscriptionCallResult? fallback = null;
            if (primary is null || primary.Response.Confidence < threshold)
            {
                fallback = await TryFallbackOnceAsync(
                    execution, project.SourceLanguage, provider, request, requestHash,
                    sliceBytes, segment.DurationMs, cancellationToken).ConfigureAwait(false);
            }

            if (primary is null && fallback is null)
            {
                throw primaryInvalid ?? new ErrorCodeException(ErrorCodes.ProviderInvalidResponse, "Transcription provider returned an invalid response.");
            }

            await EnsureLeaseRunningAsync(tenantId, executionId, owner, token, cancellationToken).ConfigureAwait(false);

            if (primary is not null)
            {
                await PersistVersionAsync(
                    tenantId, projectId, runId, segment, segmentsArtifactId,
                    execution, primary.Provider, primary.Model, project.SourceLanguage,
                    primary.Response, cancellationToken).ConfigureAwait(false);
            }

            if (fallback is not null)
            {
                await PersistVersionAsync(
                    tenantId, projectId, runId, segment, segmentsArtifactId,
                    execution, fallback.Provider, fallback.Model, project.SourceLanguage,
                    fallback.Response, cancellationToken).ConfigureAwait(false);
            }

            var (winnerVersionId, winnerArtifactId, winnerConfidence, winnerProvider, winnerModel) =
                await SelectBestStoredAsync(tenantId, segmentId, cancellationToken).ConfigureAwait(false);

            if (winnerConfidence >= threshold)
            {
                await EnsureLeaseRunningAsync(tenantId, executionId, owner, token, cancellationToken).ConfigureAwait(false);
                var stages = new StageExecutionService(_contextFactory, Microsoft.Extensions.Options.Options.Create(_retry));
                await stages.CompleteAsync(
                    tenantId, executionId, owner, token,
                    [winnerArtifactId.ToString("N")],
                    cancellationToken).ConfigureAwait(false);

                _logger.LogInformation(
                    "Transcribed segment {SegmentId} for run {RunId}: version {VersionId} selected ({Confidence}).",
                    segmentId, runId, winnerVersionId, winnerConfidence);
                return new TranscriptionSegmentResult(
                    segmentId, winnerVersionId, winnerArtifactId,
                    winnerConfidence, winnerProvider, winnerModel,
                    false, null);
            }

            if (attempt + 1 < maxAttempts)
            {
                throw new TranscriptionRetryableException(segmentId, winnerConfidence, threshold);
            }

            var review = await CreateReviewAsync(
                tenantId, projectId, runId, segment, winnerVersionId, winnerConfidence,
                threshold, cancellationToken).ConfigureAwait(false);

            await EnsureLeaseRunningAsync(tenantId, executionId, owner, token, cancellationToken).ConfigureAwait(false);
            var reviewStages = new StageExecutionService(_contextFactory, Microsoft.Extensions.Options.Options.Create(_retry));
            await reviewStages.MarkReviewRequiredAsync(tenantId, executionId, owner, token, cancellationToken).ConfigureAwait(false);

            _logger.LogInformation(
                "Transcription for segment {SegmentId} routed to review {ReviewId} (confidence {Confidence}).",
                segmentId, review, winnerConfidence);
            return new TranscriptionSegmentResult(
                segmentId, winnerVersionId, winnerArtifactId,
                winnerConfidence, winnerProvider, winnerModel,
                true, review);
        }
        finally
        {
            DeleteWorkDirQuietly(workDir);
        }
    }

    /// <summary>
    /// Deterministic best-version selection for one segment across all stored
    /// alternatives. Sets <c>IsSelected</c> on the winner only, in one
    /// transaction. Returns null when no versions exist.
    /// </summary>
    public async Task<TranscriptVersion?> SelectBestAsync(
        Guid tenantId,
        Guid segmentId,
        CancellationToken cancellationToken = default)
    {
        RequireTenant(tenantId);
        RequireId(segmentId, nameof(segmentId));

        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var versions = await db.Set<TranscriptVersion>()
                    .Where(v => v.SegmentId == segmentId)
                    .OrderBy(v => v.CreatedAt)
                    .ToListAsync(cancellationToken).ConfigureAwait(false);
                if (versions.Count == 0)
                {
                    return null;
                }

                var wordCounts = await LoadWordCountsAsync(db, versions, cancellationToken).ConfigureAwait(false);
                var priority = BuildPriorityIndex(_providers, nameof(ProviderCapability.Transcription));
                var candidates = versions.Select(v => new TranscriptCandidate(
                    v.Id, v.Provider, v.Confidence, v.Text.Length,
                    wordCounts.TryGetValue(v.Id, out var count) ? count : 0,
                    v.CreatedAt)).ToList();
                var best = PickBest(candidates, priority);
                if (best is null)
                {
                    return null;
                }

                foreach (var version in versions)
                {
                    version.SetSelected(version.Id == best.VersionId);
                }

                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return versions.First(v => v.Id == best.VersionId);
            }
            catch
            {
                try
                {
                    await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Rollback best effort; original exception propagates.
                }

                throw;
            }
        }
    }

    private sealed record TranscriptionCallResult(
        ProviderType Provider,
        string Model,
        TranscriptionResponse Response,
        string ResponseHash,
        long LatencyMs);

    private sealed record DialogueBundle(Artifact Artifact, ContentObject Content);

    private async Task<TranscriptionCallResult> InvokeAsync(
        StageExecution execution,
        ProviderType provider,
        string model,
        TranscriptionRequest request,
        string requestHash,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        TranscriptionResponse response;
        try
        {
            response = await _transcription.TranscribeAsync(request, cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (LeaseLostException)
        {
            throw;
        }
#pragma warning disable CA1031 // Provider-failure taxonomy: transport errors propagate for transport retry, coded errors are recorded then rethrown for worker routing.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            stopwatch.Stop();
            if (IsTransport(ex))
            {
                throw;
            }

            var outcome = MapOutcome(ex);
            await RecordExecutionAsync(
                execution, provider, model, null,
                outcome, requestHash, null, stopwatch.ElapsedMilliseconds,
                cancellationToken).ConfigureAwait(false);

            if (IsInvalidResponse(ex))
            {
                throw new ErrorCodeException(ErrorCodes.ProviderInvalidResponse, $"Transcription provider returned an invalid response: {Truncate(ClassifyMessage(ex))}.");
            }

            throw;
        }

        string? validationError = ValidateResponse(response);
        if (validationError is not null)
        {
            var responseHash = ConfigurationHashCalculator.Compute(new
            {
                text = response.Text,
                confidence = response.Confidence,
                model = response.Model,
            });
            await RecordExecutionAsync(
                execution, provider, model, response,
                OutcomeClass.ProviderInvalidResponse, requestHash, responseHash, stopwatch.ElapsedMilliseconds,
                cancellationToken).ConfigureAwait(false);
            throw new ErrorCodeException(ErrorCodes.ProviderInvalidResponse, validationError);
        }

        var hash = ConfigurationHashCalculator.Compute(new
        {
            text = response.Text,
            confidence = response.Confidence,
            words = response.Words.Select(w => new { word = w.Word, startMs = w.StartMs, endMs = w.EndMs, confidence = w.Confidence }),
            model = response.Model,
        });
        await RecordExecutionAsync(
            execution, provider, model, response,
            OutcomeClass.Success, requestHash, hash, stopwatch.ElapsedMilliseconds,
            cancellationToken).ConfigureAwait(false);
        return new TranscriptionCallResult(provider, model, response, hash, stopwatch.ElapsedMilliseconds);
    }

    private async Task<TranscriptionCallResult?> TryFallbackOnceAsync(
        StageExecution execution,
        string language,
        ProviderType primary,
        TranscriptionRequest request,
        string requestHash,
        long inputBytes,
        int durationMs,
        CancellationToken cancellationToken)
    {
        ProviderCapabilityDescriptor? descriptor;
        try
        {
            descriptor = await FindFallbackDescriptorAsync(execution.TenantId, primary, language, inputBytes, durationMs, cancellationToken).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Descriptor lookup failure means no fallback; the primary low-confidence path decides retry vs review.
        catch (Exception)
#pragma warning restore CA1031
        {
            return null;
        }

        if (descriptor is null || descriptor.Provider != ProviderType.Mock)
        {
            return null;
        }

        try
        {
            var fallbackModel = ResolveFallbackModel(descriptor.Provider);
            var result = await InvokeAsync(execution, descriptor.Provider, fallbackModel, request, requestHash, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation(
                "Transcription fallback for segment {SegmentId} via {Provider} returned confidence {Confidence}.",
                request.ArtifactId, descriptor.Provider.ToString(), result.Response.Confidence);
            return result;
        }
#pragma warning disable CA1031 // Fallback failure keeps the primary version; the confidence policy decides retry vs review.
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not LeaseLostException)
#pragma warning restore CA1031
        {
            _logger.LogWarning(
                "Transcription fallback failed for segment {SegmentId}: {Error}.",
                request.ArtifactId, ClassifyMessage(ex));
            return null;
        }
    }

    private async Task<ProviderCapabilityDescriptor?> FindFallbackDescriptorAsync(
        Guid tenantId,
        ProviderType primary,
        string language,
        long inputBytes,
        int durationMs,
        CancellationToken cancellationToken)
    {
        var candidates = await _descriptors.GetCandidatesAsync(ProviderCapability.Transcription, tenantId, cancellationToken).ConfigureAwait(false);
        var routing = new ProviderRoutingRequest(language, inputBytes, durationMs, "wav", true, false, false);
        var priority = BuildPriorityIndex(_providers, nameof(ProviderCapability.Transcription));
        return candidates
            .Where(d => d is not null && d.Provider != primary && _descriptors.IsCompatible(d, routing))
            .OrderBy(d => Rank(d.Provider.ToString(), priority))
            .ThenBy(d => d.Provider.ToString(), StringComparer.Ordinal)
            .FirstOrDefault();
    }

    private string ResolveFallbackModel(ProviderType provider)
    {
        foreach (var option in _providers.Descriptors ?? [])
        {
            if (option is null || string.IsNullOrWhiteSpace(option.Model))
            {
                continue;
            }

            if (!string.Equals(option.Provider?.Trim(), provider.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (string.Equals(option.Capability?.Trim(), nameof(ProviderCapability.Transcription), StringComparison.OrdinalIgnoreCase))
            {
                return option.Model;
            }
        }

        return string.Concat(provider.ToString().ToLowerInvariant(), "-default");
    }

    private async Task PersistVersionAsync(
        Guid tenantId,
        Guid projectId,
        Guid runId,
        SpeechSegment segment,
        Guid segmentsArtifactId,
        StageExecution execution,
        ProviderType provider,
        string model,
        string language,
        TranscriptionResponse response,
        CancellationToken cancellationToken)
    {
        var confidence = Clamp01(response.Confidence);
        var wordsJson = JsonSerializer.Serialize(new
        {
            schemaVersion = "1",
            segmentId = segment.Id.ToString("N"),
            sequence = segment.Sequence,
            startMs = segment.StartMs,
            endMs = segment.EndMs,
            language,
            provider = provider.ToString(),
            model,
            confidence,
            text = response.Text,
            words = response.Words.Select(w => new
            {
                word = w.Word,
                startMs = w.StartMs,
                endMs = w.EndMs,
                confidence = Clamp01(w.Confidence),
            }),
        }, JsonOptions);

        PublishResult published;
        using (var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(wordsJson), writable: false))
        {
            published = await _artifacts.PublishAsync(
                tenantId, projectId, runId,
                StageType.Transcription, ArtifactType.Transcript,
                stream, ".json", "application/json",
                provider.ToString(), model,
                execution.ConfigurationHash, execution.ExecutionSnapshotHash,
                segmentsArtifactId == Guid.Empty ? [] : [segmentsArtifactId],
                execution.Id,
                cancellationToken).ConfigureAwait(false);
        }

        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            await db.Database.ExecuteSqlRawAsync(
                "UPDATE artifacts SET metadata_json = {0} WHERE id = {1} AND tenant_id = {2}",
                wordsJson, published.ArtifactId, tenantId).ConfigureAwait(false);
        }

        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            db.Set<TranscriptVersion>().Add(new TranscriptVersion(
                Guid.NewGuid(), tenantId, projectId, runId, segment.Id,
                provider.ToString(), model, language, response.Text, confidence,
                published.ArtifactId, false, false, DateTimeOffset.UtcNow));
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<(Guid VersionId, Guid WordsArtifactId, double Confidence, string Provider, string Model)> SelectBestStoredAsync(
        Guid tenantId,
        Guid segmentId,
        CancellationToken cancellationToken)
    {
        var winner = await SelectBestAsync(tenantId, segmentId, cancellationToken).ConfigureAwait(false);
        if (winner is null || winner.WordTimestampsArtifactId is null)
        {
            throw new ErrorCodeException(ErrorCodes.PipelineInvariantViolation, $"Transcript selection for segment '{segmentId:D}' produced no winner.");
        }

        return (winner.Id, winner.WordTimestampsArtifactId.Value, winner.Confidence, winner.Provider, winner.Model);
    }

    private async Task<Guid> CreateReviewAsync(
        Guid tenantId,
        Guid projectId,
        Guid runId,
        SpeechSegment segment,
        Guid versionId,
        double confidence,
        double threshold,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(new
        {
            reason = ReviewReason,
            segmentId = segment.Id.ToString("N"),
            sequence = segment.Sequence,
            versionId = versionId.ToString("N"),
            confidence,
            threshold,
        }, JsonOptions);

        var reviewId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            var winner = await db.Set<TranscriptVersion>()
                .FirstOrDefaultAsync(v => v.Id == versionId, cancellationToken).ConfigureAwait(false);
            winner?.SetNeedsReview(true);

            db.Set<ReviewItem>().Add(new ReviewItem(
                reviewId, tenantId, projectId, runId,
                ScopeType.Segment, segment.Id.ToString("D"), segment.Id,
                ReviewStatus.Open, ReviewReason, payload, now, now, null));
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        return reviewId;
    }

    private async Task<TranscriptionSegmentResult?> TryReturnIdempotentAsync(
        Guid tenantId,
        StageExecution execution,
        Guid segmentId,
        CancellationToken cancellationToken)
    {
        if (execution.Status is not (StageStatus.Completed or StageStatus.ManualReviewRequired))
        {
            return null;
        }

        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            var versions = await db.Set<TranscriptVersion>()
                .AsNoTracking()
                .Where(v => v.SegmentId == segmentId)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            var winner = versions.FirstOrDefault(v => v.IsSelected)
                ?? versions.OrderByDescending(v => v.Confidence).FirstOrDefault();
            if (winner is null || winner.WordTimestampsArtifactId is null)
            {
                return null;
            }

            Guid? reviewId = null;
            if (execution.Status == StageStatus.ManualReviewRequired)
            {
                reviewId = await db.Set<ReviewItem>()
                    .AsNoTracking()
                    .Where(r => r.ProcessingRunId == execution.ProcessingRunId && r.SegmentId == segmentId && r.Reason == ReviewReason)
                    .OrderByDescending(r => r.CreatedAt)
                    .Select(r => (Guid?)r.Id)
                    .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            }

            return new TranscriptionSegmentResult(
                segmentId, winner.Id, winner.WordTimestampsArtifactId.Value,
                winner.Confidence, winner.Provider, winner.Model,
                execution.Status == StageStatus.ManualReviewRequired, reviewId);
        }
    }

    private async Task<Dictionary<Guid, int>> LoadWordCountsAsync(
        DbContext db,
        IReadOnlyList<TranscriptVersion> versions,
        CancellationToken cancellationToken)
    {
        var ids = versions
            .Select(v => v.WordTimestampsArtifactId)
            .Where(id => id.HasValue && id.Value != Guid.Empty)
            .Select(id => id!.Value)
            .Distinct()
            .ToList();
        var result = new Dictionary<Guid, int>();
        if (ids.Count == 0)
        {
            return result;
        }

        var metadata = await db.Set<Artifact>()
            .AsNoTracking()
            .Where(a => ids.Contains(a.Id))
            .Select(a => new { a.Id, a.MetadataJson })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var byArtifact = metadata.ToDictionary(m => m.Id, m => m.MetadataJson);
        foreach (var version in versions)
        {
            if (version.WordTimestampsArtifactId is null
                || !byArtifact.TryGetValue(version.WordTimestampsArtifactId.Value, out var json)
                || string.IsNullOrWhiteSpace(json))
            {
                result[version.Id] = 0;
                continue;
            }

            result[version.Id] = CountWords(json);
        }

        return result;
    }

    private static int CountWords(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.TryGetProperty("words", out var words)
                && words.ValueKind == JsonValueKind.Array)
            {
                return words.GetArrayLength();
            }

            return 0;
        }
        catch (JsonException)
        {
            return 0;
        }
    }

    private async Task RecordExecutionAsync(
        StageExecution execution,
        ProviderType provider,
        string model,
        TranscriptionResponse? response,
        OutcomeClass outcome,
        string requestHash,
        string? responseHash,
        long latencyMs,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var scope = string.Concat(execution.ScopeType.ToString(), ":", execution.ScopeId, ":", provider.ToString());
        var idempotencyKey = ProviderExecutionRecorder.BuildIdempotencyKey(
            execution.ProcessingRunId, nameof(StageType.Transcription), scope, execution.Attempt);
        string? externalJobId = null;
        if (response?.RawMetadata is not null
            && response.RawMetadata.TryGetValue("mock.job_id", out var jobId)
            && !string.IsNullOrWhiteSpace(jobId))
        {
            externalJobId = jobId.Trim();
        }

        var row = new ProviderExecution(
            Guid.NewGuid(), execution.TenantId, execution.ProjectId, execution.ProcessingRunId,
            execution.Id, provider, ProviderCapability.Transcription, model,
            response?.ModelVersion, response?.Deployment, null, null,
            execution.Attempt, requestHash, responseHash, Math.Max(0, latencyMs),
            response?.Usage?.TokensIn, response?.Usage?.TokensOut, response?.Usage?.AudioSeconds,
            response?.Usage?.EstimatedCostUsd, response?.Usage?.EstimatedCostUsd, null,
            outcome, null,
            null, null, null, externalJobId, idempotencyKey, now);
        await _recorder.RecordAsync(row, cancellationToken).ConfigureAwait(false);
    }

    private static string? ValidateResponse(TranscriptionResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);

        if (string.IsNullOrWhiteSpace(response.Text))
        {
            return "Transcription provider returned empty text.";
        }

        if (response.Words is null)
        {
            return "Transcription provider returned no word timestamps.";
        }

        if (double.IsNaN(response.Confidence))
        {
            return "Transcription provider returned an invalid confidence.";
        }

        return null;
    }

    private static bool IsTransport(Exception exception)
    {
        return exception is HttpRequestException or TimeoutException or SocketException or IOException;
    }

    private static bool IsInvalidResponse(Exception exception)
    {
        return exception is ErrorCodeException coded
            && string.Equals(coded.ErrorCode, ErrorCodes.ProviderInvalidResponse, StringComparison.Ordinal);
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

    private int MaxAttempts()
    {
        if (_retry.PerStageMaxAttempts.TryGetValue(nameof(StageType.Transcription), out var perStage))
        {
            return Math.Clamp(perStage, 1, 10);
        }

        return Math.Clamp(_options.MaxAttempts, 1, 10);
    }

    private static double Clamp01(double value)
    {
        if (double.IsNaN(value))
        {
            return 0;
        }

        return Math.Clamp(value, 0.0, 1.0);
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

    private async Task<StageExecution> LoadExecutionAsync(
        Guid tenantId,
        Guid executionId,
        Guid projectId,
        Guid runId,
        Guid segmentId,
        string owner,
        string token,
        CancellationToken cancellationToken)
    {
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            var execution = await db.Set<StageExecution>()
                .AsNoTracking()
                .FirstOrDefaultAsync(e => e.Id == executionId, cancellationToken).ConfigureAwait(false);
            if (execution is null)
            {
                throw new NotFoundException($"Stage execution '{executionId}' was not found.");
            }

            if (execution.TenantId != tenantId
                || execution.ProjectId != projectId
                || execution.ProcessingRunId != runId)
            {
                throw new ForbiddenException($"Stage execution '{executionId}' does not belong to the current tenant/project/run.");
            }

            if (execution.StageType != StageType.Transcription)
            {
                throw new DomainException($"Stage execution '{executionId}' is '{execution.StageType}', not Transcription.");
            }

            if (execution.ScopeType != ScopeType.Segment
                || execution.SegmentId is null
                || execution.SegmentId.Value == Guid.Empty
                || execution.SegmentId.Value != segmentId
                || !string.Equals(execution.ScopeId, segmentId.ToString("D"), StringComparison.OrdinalIgnoreCase))
            {
                throw new DomainException($"Stage execution '{executionId}' does not target segment '{segmentId:D}'.");
            }

            if (execution.Status is StageStatus.Completed or StageStatus.ManualReviewRequired)
            {
                return execution;
            }

            if (execution.Status != StageStatus.Running
                || !string.Equals(execution.LeaseOwner, owner, StringComparison.Ordinal)
                || !string.Equals(execution.LeaseToken, token, StringComparison.Ordinal))
            {
                throw new LeaseLostException($"Lease lost for stage execution '{executionId}'.");
            }

            return execution;
        }
    }

    private async Task<SpeechSegment> LoadSegmentAsync(
        Guid tenantId,
        Guid projectId,
        Guid runId,
        Guid segmentId,
        CancellationToken cancellationToken)
    {
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            var segment = await db.Set<SpeechSegment>()
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.Id == segmentId, cancellationToken).ConfigureAwait(false);
            if (segment is null)
            {
                throw new NotFoundException($"Speech segment '{segmentId:D}' was not found.");
            }

            if (segment.TenantId != tenantId || segment.ProjectId != projectId || segment.RunId != runId)
            {
                throw new ForbiddenException($"Speech segment '{segmentId:D}' does not belong to the current tenant/project/run.");
            }

            return segment;
        }
    }

    private async Task EnsureLeaseRunningAsync(
        Guid tenantId,
        Guid executionId,
        string owner,
        string token,
        CancellationToken cancellationToken)
    {
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            var execution = await db.Set<StageExecution>()
                .AsNoTracking()
                .FirstOrDefaultAsync(e => e.Id == executionId, cancellationToken).ConfigureAwait(false);
            if (execution is null
                || execution.Status != StageStatus.Running
                || !string.Equals(execution.LeaseOwner, owner, StringComparison.Ordinal)
                || !string.Equals(execution.LeaseToken, token, StringComparison.Ordinal))
            {
                throw new LeaseLostException($"Lease lost for stage execution '{executionId}'.");
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
                throw new LeaseLostException($"Processing run '{runId}' is '{run.Status}'; aborting before commit.");
            }
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

    private async Task<Guid> ResolveSegmentsArtifactIdAsync(
        Guid tenantId,
        Guid runId,
        CancellationToken cancellationToken)
    {
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            return await db.Set<Artifact>()
                .Where(a => a.ProcessingRunId == runId && a.Type == ArtifactType.Segments)
                .OrderByDescending(a => a.CreatedAt)
                .Select(a => a.Id)
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
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
            throw new ErrorCodeException(ErrorCodes.StorageUnavailable, "Source download failed; retry the operation.", ex);
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
            catch (Exception ex) when (ex is DomainException || ex is AppException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new ErrorCodeException(ErrorCodes.StorageUnavailable, "Source download failed; retry the operation.", ex);
            }
        }
    }

    private static string CreateTempWorkingDir()
    {
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
            return "Transcription failed.";
        }

        var trimmed = message.Trim();
        return trimmed.Length <= 500 ? trimmed : trimmed.Substring(0, 500);
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
