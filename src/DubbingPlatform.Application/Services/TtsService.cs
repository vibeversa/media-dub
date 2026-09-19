using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
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
/// Outcome of one segment synthesis. <see cref="NeedsReview"/> is true only
/// for the terminal review path (exhausted budget or persistently undecodable
/// output; review item created, execution moved to
/// <c>ManualReviewRequired</c>); otherwise the persisted audio is completed.
/// <see cref="ProsodyRate"/> is the estimator pre-adjustment applied to the
/// SSML before the first paid call (1.0 when the estimator is disabled).
/// </summary>
public sealed record TtsSegmentResult(
    Guid SegmentId,
    Guid GeneratedAudioId,
    Guid ArtifactId,
    int DurationMs,
    int EstimatedMs,
    double ProsodyRate,
    string Provider,
    string Model,
    string VoiceId,
    bool IsPreview,
    bool NeedsReview,
    Guid? ReviewItemId);

/// <summary>
/// TTS meters. Counter names are frozen: renaming breaks dashboards.
/// </summary>
public static class TtsMeters
{
    public const string MeterName = "DubbingPlatform.Tts";

    public const string BudgetExceededMetricName = "tts.budget_exceeded_total";

    public const string ReviewMetricName = "tts.review_total";

    private static readonly Meter Meter = new(MeterName);

    public static readonly Counter<long> BudgetExceeded =
        Meter.CreateCounter<long>(BudgetExceededMetricName);

    public static readonly Counter<long> Reviews =
        Meter.CreateCounter<long>(ReviewMetricName);
}

/// <summary>
/// Segment-scoped TTS generation with estimator pre-adjustment, preview/final
/// separation, cost reservation, and bounded budgets. Flow per call: load the
/// selected translation (empty or missing fails fast with
/// <c>VALIDATION_FAILED</c>) plus the run voice assignment
/// (<c>PIPELINE_INVARIANT_VIOLATION</c> when the <c>VoiceAssignment</c>
/// prerequisite is missing), run the deterministic <see cref="DurationEstimator"/>
/// (skipped to rate 1.0 when <c>Tts:EstimatorEnabled=false</c>), build SSML
/// with the clamped prosody rate, check the capability cost gate
/// (<c>QUOTA_EXCEEDED</c> with no provider call when blocked), resolve the
/// provider (capability <c>Tts</c>, Mock adapter only), call once, materialize
/// the Mock WAV bytes, validate via <see cref="IFFprobeService"/> (readable
/// audio, duration &gt; 0, audio stream present; undecodable triggers one
/// fallback call then review), publish one <c>GeneratedAudioPreview</c> or
/// <c>GeneratedAudioFinal</c> artifact (parents are the translation artifact
/// when found) plus one immutable <c>GeneratedAudioArtifact</c> row
/// (<c>IsPreview</c> separates preview probes from finals; previews are never
/// selected as finals), record a <c>ProviderExecution</c> per call, and
/// complete the execution lease-fenced. Retryable rate-limit/timeout codes
/// propagate for delayed saga retry while attempts remain
/// (<c>Tts:MaxAttempts</c>); exhausted budgets route to review
/// (<c>TTS_QUALITY</c>, <c>Open</c>) instead of failing the run. Terminal
/// executions return their stored outcome (idempotent redelivery safe). Temp
/// files are always deleted; lease loss discards bytes (orphan blobs are
/// reconciled). Never logs text, SSML, audio, or secrets: only ids, counts,
/// durations, rates, and hashes.
/// </summary>
public sealed class TtsService
{
    /// <summary>Review reason for exhausted TTS budget or bad output.</summary>
    public const string ReviewReason = "TTS_QUALITY";

    /// <summary>Artifact/row schema version for every TTS metadata payload.</summary>
    public const string SchemaVersion = "1";

    /// <summary>Deterministic SSML template identity (no secrets in the id).</summary>
    public const string SsmlTemplateId = DurationEstimator.SsmlTemplateId;

    /// <summary>Deterministic SSML template version.</summary>
    public const string SsmlTemplateVersion = "1";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IStageExecutionContextFactory _contextFactory;
    private readonly ArtifactService _artifacts;
    private readonly IFFprobeService _ffprobe;
    private readonly ITtsProvider _tts;
    private readonly ProviderResolver _resolver;
    private readonly IDescriptorStore _descriptors;
    private readonly ProviderExecutionRecorder _recorder;
    private readonly IProviderCostGate _costGate;
    private readonly CostService? _costs;
    private readonly TtsOptions _options;
    private readonly ProviderOptions _providers;
    private readonly RetryOptions _retry;
    private readonly ILogger<TtsService> _logger;

    public TtsService(
        IStageExecutionContextFactory contextFactory,
        ArtifactService artifacts,
        IFFprobeService ffprobe,
        ITtsProvider tts,
        ProviderResolver resolver,
        IDescriptorStore descriptors,
        ProviderExecutionRecorder recorder,
        IProviderCostGate costGate,
        IOptions<TtsOptions> ttsOptions,
        IOptions<ProviderOptions> providerOptions,
        IOptions<RetryOptions> retryOptions,
        ILogger<TtsService> logger,
        CostService? costs = null)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        ArgumentNullException.ThrowIfNull(artifacts);
        ArgumentNullException.ThrowIfNull(ffprobe);
        ArgumentNullException.ThrowIfNull(tts);
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(descriptors);
        ArgumentNullException.ThrowIfNull(recorder);
        ArgumentNullException.ThrowIfNull(costGate);
        ArgumentNullException.ThrowIfNull(ttsOptions);
        ArgumentNullException.ThrowIfNull(providerOptions);
        ArgumentNullException.ThrowIfNull(retryOptions);
        ArgumentNullException.ThrowIfNull(logger);
        _contextFactory = contextFactory;
        _artifacts = artifacts;
        _ffprobe = ffprobe;
        _tts = tts;
        _resolver = resolver;
        _descriptors = descriptors;
        _recorder = recorder;
        _costGate = costGate;
        _costs = costs;
        _options = ttsOptions.Value;
        _providers = providerOptions.Value;
        _retry = retryOptions.Value;
        _logger = logger;
    }

    /// <summary>
    /// Generates target speech for one segment end to end (estimator, cost
    /// gate, provider calls, validation, persistence, budget policy, stage
    /// commit). See class docs for the policy matrix. Terminal executions
    /// return their stored outcome (idempotent redelivery safe).
    /// </summary>
    public async Task<TtsSegmentResult> GenerateAsync(
        Guid tenantId,
        Guid projectId,
        Guid runId,
        Guid segmentId,
        bool isPreview,
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

        if (isPreview && !_options.PreviewEnabled)
        {
            throw new ErrorCodeException(ErrorCodes.ValidationFailed, "Preview synthesis is disabled (Tts:PreviewEnabled=false).");
        }

        var project = await LoadOwnedProjectAsync(tenantId, projectId, cancellationToken).ConfigureAwait(false);
        var execution = await LoadExecutionAsync(tenantId, executionId, projectId, runId, segmentId, owner, token, cancellationToken).ConfigureAwait(false);

        var idempotent = await TryReturnIdempotentAsync(tenantId, execution, segmentId, isPreview, cancellationToken).ConfigureAwait(false);
        if (idempotent is not null)
        {
            return idempotent;
        }

        await EnsureRunActiveAsync(tenantId, runId, projectId, cancellationToken).ConfigureAwait(false);

        var segment = await LoadSegmentAsync(tenantId, projectId, runId, segmentId, cancellationToken).ConfigureAwait(false);
        var translation = await LoadSelectedTranslationAsync(tenantId, projectId, runId, segmentId, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(translation.PrimaryText))
        {
            throw new ErrorCodeException(ErrorCodes.ValidationFailed, $"Segment '{segmentId:D}' has empty translation text; TTS cannot proceed.");
        }

        var voice = await LoadAssignedVoiceAsync(tenantId, projectId, runId, segment, cancellationToken).ConfigureAwait(false);

        var target = project.TargetLanguage?.Trim();
        if (string.IsNullOrWhiteSpace(target))
        {
            throw new ErrorCodeException(ErrorCodes.ValidationFailed, $"Project '{projectId:D}' has no target language for TTS.");
        }

        var sourceText = translation.PrimaryText.Trim();
        var estimatedMs = _options.EstimatorEnabled
            ? DurationEstimator.EstimateMs(sourceText, target)
            : segment.DurationMs;
        var prosodyRate = _options.EstimatorEnabled
            ? DurationEstimator.ComputeRate(estimatedMs, segment.DurationMs)
            : 1.0;
        var ssml = _options.EstimatorEnabled
            ? DurationEstimator.BuildSsml(sourceText, prosodyRate)
            : sourceText;
        var ssmlHash = DurationEstimator.ComputeHash(ssml);
        var requestHash = ConfigurationHashCalculator.Compute(new
        {
            segmentId = segmentId.ToString("N"),
            target,
            voiceId = voice.Voice.VoiceId,
            providerVoice = voice.Voice.Provider,
            rate = prosodyRate.ToString("F4", CultureInfo.InvariantCulture),
            ssmlHash,
            templateId = SsmlTemplateId,
            templateVersion = SsmlTemplateVersion,
            isPreview,
            durationMs = segment.DurationMs,
            estimatedMs,
        });

        if (!await _costGate.CanProceedAsync(tenantId, ProviderCapability.Tts, cancellationToken).ConfigureAwait(false))
        {
            throw new ErrorCodeException(ErrorCodes.QuotaExceeded, "Cost guard blocks TTS for the current tenant.");
        }

        // Task 036 per-segment preflight: atomic hold before the paid call.
        // Null when CostService is not wired (existing tests); the capability
        // gate above still applies. Reconciled to provider-reported actuals.
        CostReservation? costHold = null;
        var costEstimate = 0.0;
        if (_costs is not null)
        {
            costEstimate = CostService.Estimate(
                ProviderCapability.Tts,
                new CostUsageDims(0.0, sourceText.Length, 0));
            costHold = await _costs.ReserveAsync(
                tenantId, projectId, runId, segmentId,
                ProviderCapability.Tts, costEstimate, cancellationToken).ConfigureAwait(false);
        }

        var maxAttempts = Math.Clamp(_options.MaxAttempts, 1, 10);
        var inputBytes = (long)Encoding.UTF8.GetByteCount(ssml);

        ProviderType provider;
        string model;
        try
        {
            (provider, model) = await _resolver.ResolveAsync(
                ProviderCapability.Tts,
                tenantId,
                target,
                inputBytes,
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
#pragma warning disable CA1031 // Resolver failures are permanent provider-configuration failures with no audio to persist.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            throw new ErrorCodeException(ErrorCodes.ProviderConfigurationError, $"No TTS route is available: {Truncate(ClassifyMessage(ex))}.");
        }

        if (provider != ProviderType.Mock)
        {
            throw new ErrorCodeException(
                ErrorCodes.ProviderConfigurationError,
                string.Concat("No TTS adapter for provider '", provider.ToString(), "'."));
        }

        TtsCallResult? primary = null;
        try
        {
            primary = await InvokeOnceAsync(
                execution, provider, model, ssml, target, voice,
                segment, requestHash, ssmlHash, isPreview, isFallback: false,
                cancellationToken).ConfigureAwait(false);
        }
        catch (ErrorCodeException ex) when (IsRetryable(ex))
        {
            if (attempt + 1 < maxAttempts)
            {
                await ReleaseCostHoldAsync(costHold, tenantId, CancellationToken.None).ConfigureAwait(false);
                throw;
            }

            await ReconcileCostHoldAsync(costHold, tenantId, null, costEstimate).ConfigureAwait(false);
            costHold = null;
            TtsMeters.BudgetExceeded.Add(1);
            return await RouteToReviewAsync(
                tenantId, projectId, runId, segment, execution, owner, token,
                null, 0, estimatedMs, prosodyRate, provider.ToString(), model, voice.Voice.VoiceId,
                isPreview, budgetExceeded: true, cancellationToken).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Release is best effort; the provider error propagates for worker routing.
        catch (Exception)
        {
            await ReleaseCostHoldAsync(costHold, tenantId, CancellationToken.None).ConfigureAwait(false);
            costHold = null;
            throw;
        }
#pragma warning restore CA1031

        if (primary is not null)
        {
            await ReconcileCostHoldAsync(costHold, tenantId, primary.Response?.Usage?.EstimatedCostUsd, costEstimate).ConfigureAwait(false);
            costHold = null;
            var validated = await TryValidateAsync(primary.AudioBytes, cancellationToken).ConfigureAwait(false);
            if (validated is not null)
            {
                return await PersistAndCompleteAsync(
                    tenantId, projectId, runId, segment, translation, execution, owner, token,
                    primary, validated, estimatedMs, prosodyRate, ssmlHash,
                    isPreview, fallbackUsed: false, cancellationToken).ConfigureAwait(false);
            }

            await RecordInvalidAsync(execution, provider, model, primary, requestHash, ssmlHash, isPreview, cancellationToken).ConfigureAwait(false);
            var fallback = await TryFallbackOnceAsync(
                execution, target, ssml, voice, segment,
                requestHash, ssmlHash, inputBytes, provider, model,
                isPreview, cancellationToken).ConfigureAwait(false);
            if (fallback is not null)
            {
                var fallbackValidated = await TryValidateAsync(fallback.AudioBytes, cancellationToken).ConfigureAwait(false);
                if (fallbackValidated is not null)
                {
                    return await PersistAndCompleteAsync(
                        tenantId, projectId, runId, segment, translation, execution, owner, token,
                        fallback, fallbackValidated, estimatedMs, prosodyRate, ssmlHash,
                        isPreview, fallbackUsed: true, cancellationToken).ConfigureAwait(false);
                }

                await RecordInvalidAsync(execution, fallback.Provider, fallback.Model, fallback, requestHash, ssmlHash, isPreview, cancellationToken).ConfigureAwait(false);
            }

            TtsMeters.BudgetExceeded.Add(1);
            return await RouteToReviewAsync(
                tenantId, projectId, runId, segment, execution, owner, token,
                null, 0, estimatedMs, prosodyRate, provider.ToString(), model, voice.Voice.VoiceId,
                isPreview, budgetExceeded: true, cancellationToken).ConfigureAwait(false);
        }

        throw new ErrorCodeException(ErrorCodes.ProviderInvalidResponse, "TTS provider returned no audio.");
    }

    private sealed record TtsCallResult(
        ProviderType Provider,
        string Model,
        TtsResponse Response,
        byte[] AudioBytes,
        string ResponseHash,
        long LatencyMs,
        bool IsFallback,
        string RequestLanguage,
        string VoiceId,
        Guid VoiceProfileId);

    private sealed record LoadedTranslation(Guid VersionId, string PrimaryText, Guid? ArtifactId);

    private sealed record LoadedVoice(VoiceProfile Voice, Guid AssignmentId);

    private sealed record ValidatedAudio(int DurationMs);

    private async Task<TtsSegmentResult> PersistAndCompleteAsync(
        Guid tenantId,
        Guid projectId,
        Guid runId,
        SpeechSegment segment,
        LoadedTranslation translation,
        StageExecution execution,
        string owner,
        string token,
        TtsCallResult call,
        ValidatedAudio validated,
        int estimatedMs,
        double prosodyRate,
        string ssmlHash,
        bool isPreview,
        bool fallbackUsed,
        CancellationToken cancellationToken)
    {
        await EnsureLeaseRunningAsync(tenantId, execution.Id, owner, token, cancellationToken).ConfigureAwait(false);

        var translationArtifactId = translation.ArtifactId
            ?? await FindTranslationArtifactAsync(tenantId, runId, segment.Id, cancellationToken).ConfigureAwait(false);

        var artifactType = isPreview ? ArtifactType.GeneratedAudioPreview : ArtifactType.GeneratedAudioFinal;
        var payload = JsonSerializer.Serialize(new
        {
            schemaVersion = SchemaVersion,
            runId = runId.ToString("N"),
            segmentId = segment.Id.ToString("N"),
            sequence = segment.Sequence,
            targetLanguage = call.RequestLanguage,
            voiceId = call.VoiceId,
            provider = call.Provider.ToString(),
            model = call.Model,
            durationMs = validated.DurationMs,
            estimatedMs,
            prosodyRate,
            ssmlHash,
            ssmlTemplateId = SsmlTemplateId,
            ssmlTemplateVersion = SsmlTemplateVersion,
            isPreview,
            fallbackUsed,
            translationVersionId = translation.VersionId.ToString("N"),
            translationArtifactId = translationArtifactId?.ToString("N"),
            attempt = execution.Attempt,
        }, JsonOptions);

        var parents = new List<Guid>();
        if (translationArtifactId.HasValue && translationArtifactId.Value != Guid.Empty)
        {
            parents.Add(translationArtifactId.Value);
        }

        PublishResult published;
        using (var stream = new MemoryStream(call.AudioBytes, writable: false))
        {
            published = await _artifacts.PublishAsync(
                tenantId, projectId, runId,
                StageType.VoiceGeneration, artifactType,
                stream, ".wav", "audio/wav",
                call.Provider.ToString(), call.Model,
                execution.ConfigurationHash, execution.ExecutionSnapshotHash,
                parents,
                execution.Id,
                cancellationToken).ConfigureAwait(false);
        }

        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            await db.Database.ExecuteSqlRawAsync(
                "UPDATE artifacts SET metadata_json = {0} WHERE id = {1} AND tenant_id = {2}",
                payload, published.ArtifactId, tenantId).ConfigureAwait(false);
        }

        var generatedId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            var existingForAttempt = await db.Set<GeneratedAudioArtifact>()
                .FirstOrDefaultAsync(
                    g => g.RunId == runId && g.SegmentId == segment.Id && g.IsPreview == isPreview && g.Attempt == execution.Attempt,
                    cancellationToken).ConfigureAwait(false);
            if (existingForAttempt is not null)
            {
                await EnsureLeaseRunningAsync(tenantId, execution.Id, owner, token, cancellationToken).ConfigureAwait(false);
                var stagesReuse = new StageExecutionService(_contextFactory, Microsoft.Extensions.Options.Options.Create(_retry));
                await stagesReuse.CompleteAsync(
                    tenantId, execution.Id, owner, token,
                    [published.ArtifactId.ToString("N")],
                    cancellationToken).ConfigureAwait(false);
                return new TtsSegmentResult(
                    segment.Id, existingForAttempt.Id, published.ArtifactId,
                    existingForAttempt.DurationMs, estimatedMs, prosodyRate,
                    existingForAttempt.Provider, existingForAttempt.Model, call.VoiceId,
                    isPreview, false, null);
            }

            db.Set<GeneratedAudioArtifact>().Add(new GeneratedAudioArtifact(
                generatedId, tenantId, projectId, runId, segment.Id,
                call.Provider.ToString(), call.Model, call.VoiceProfileId,
                published.ContentObjectId, validated.DurationMs, isPreview,
                execution.Attempt, now));
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        await EnsureLeaseRunningAsync(tenantId, execution.Id, owner, token, cancellationToken).ConfigureAwait(false);
        var stages = new StageExecutionService(_contextFactory, Microsoft.Extensions.Options.Options.Create(_retry));
        await stages.CompleteAsync(
            tenantId, execution.Id, owner, token,
            [published.ArtifactId.ToString("N")],
            cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Synthesized segment {SegmentId} for run {RunId}: audio {AudioId} ({Duration}ms, preview={IsPreview}).",
            segment.Id, runId, generatedId, validated.DurationMs, isPreview);
        return new TtsSegmentResult(
            segment.Id, generatedId, published.ArtifactId,
            validated.DurationMs, estimatedMs, prosodyRate,
            call.Provider.ToString(), call.Model, call.VoiceId,
            isPreview, false, null);
    }

    private async Task<TtsSegmentResult> RouteToReviewAsync(
        Guid tenantId,
        Guid projectId,
        Guid runId,
        SpeechSegment segment,
        StageExecution execution,
        string owner,
        string token,
        Guid? generatedAudioId,
        int durationMs,
        int estimatedMs,
        double prosodyRate,
        string provider,
        string model,
        string voiceId,
        bool isPreview,
        bool budgetExceeded,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(new
        {
            reason = ReviewReason,
            segmentId = segment.Id.ToString("N"),
            sequence = segment.Sequence,
            generatedAudioId = generatedAudioId?.ToString("N"),
            durationMs,
            estimatedMs,
            prosodyRate,
            provider,
            model,
            voiceId,
            isPreview,
            budgetExceeded,
        }, JsonOptions);

        var reviewId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            db.Set<ReviewItem>().Add(new ReviewItem(
                reviewId, tenantId, projectId, runId,
                ScopeType.Segment, segment.Id.ToString("D"), segment.Id,
                ReviewStatus.Open, ReviewReason, payload, now, now, null));
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        await EnsureLeaseRunningAsync(tenantId, execution.Id, owner, token, cancellationToken).ConfigureAwait(false);
        var stages = new StageExecutionService(_contextFactory, Microsoft.Extensions.Options.Options.Create(_retry));
        await stages.MarkReviewRequiredAsync(tenantId, execution.Id, owner, token, cancellationToken).ConfigureAwait(false);
        TtsMeters.Reviews.Add(1);
        _logger.LogInformation(
            "TTS for segment {SegmentId} routed to review {ReviewId} (preview={IsPreview}).",
            segment.Id, reviewId, isPreview);
        return new TtsSegmentResult(
            segment.Id, generatedAudioId ?? Guid.Empty, Guid.Empty,
            durationMs, estimatedMs, prosodyRate,
            provider, model, voiceId,
            isPreview, true, reviewId);
    }

    private async Task<TtsCallResult> InvokeOnceAsync(
        StageExecution execution,
        ProviderType provider,
        string model,
        string ssml,
        string targetLanguage,
        LoadedVoice voice,
        SpeechSegment segment,
        string requestHash,
        string ssmlHash,
        bool isPreview,
        bool isFallback,
        CancellationToken cancellationToken)
    {
        var request = new TtsRequest(
            execution.TenantId, execution.ProjectId, execution.ProcessingRunId,
            ssml, targetLanguage, voice.Voice.VoiceId,
            segment.DurationMs,
            voice.Voice.Type == VoiceType.Cloned);

        var stopwatch = Stopwatch.StartNew();
        TtsResponse response;
        try
        {
            response = await _tts.SynthesizeAsync(request, cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            throw;
        }
        catch (LeaseLostException)
        {
            stopwatch.Stop();
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
                execution, provider, model, null, ssmlHash,
                voice.Voice.VoiceVersion, outcome, requestHash, null,
                stopwatch.ElapsedMilliseconds, isPreview, isFallback,
                CancellationToken.None).ConfigureAwait(false);

            if (ex is ErrorCodeException)
            {
                throw;
            }

            throw new ErrorCodeException(ErrorCodes.ProviderFailed, $"TTS provider failed for segment '{segment.Id:D}': {Truncate(ClassifyMessage(ex))}.", ex);
        }

        var validationError = ValidateResponse(response);
        if (validationError is not null)
        {
            var invalidHash = ConfigurationHashCalculator.Compute(new
            {
                contentId = response.ContentObjectId,
                duration = response.DurationMs,
                model = response.Model,
            });
            await RecordExecutionAsync(
                execution, provider, model, response, ssmlHash,
                voice.Voice.VoiceVersion, OutcomeClass.ProviderInvalidResponse,
                requestHash, invalidHash, stopwatch.ElapsedMilliseconds,
                isPreview, isFallback, CancellationToken.None).ConfigureAwait(false);
            throw new ErrorCodeException(ErrorCodes.ProviderInvalidResponse, validationError);
        }

        var audioBytes = ResolveAudioBytes(response, request);
        var hash = ConfigurationHashCalculator.Compute(new
        {
            contentId = response.ContentObjectId,
            duration = response.DurationMs,
            voice = response.VoiceId,
            model = response.Model,
            audioHash = Convert.ToHexString(SHA256.HashData(audioBytes)).ToLowerInvariant(),
        });
        await RecordExecutionAsync(
            execution, provider, model, response, ssmlHash,
            voice.Voice.VoiceVersion, OutcomeClass.Success,
            requestHash, hash, stopwatch.ElapsedMilliseconds,
            isPreview, isFallback, CancellationToken.None).ConfigureAwait(false);
        return new TtsCallResult(
            provider, model, response, audioBytes, hash,
            stopwatch.ElapsedMilliseconds, isFallback,
            targetLanguage, voice.Voice.VoiceId, voice.Voice.Id);
    }

    private async Task<TtsCallResult?> TryFallbackOnceAsync(
        StageExecution execution,
        string targetLanguage,
        string ssml,
        LoadedVoice voice,
        SpeechSegment segment,
        string requestHash,
        string ssmlHash,
        long inputBytes,
        ProviderType primary,
        string primaryModel,
        bool isPreview,
        CancellationToken cancellationToken)
    {
        ProviderCapabilityDescriptor? descriptor;
        try
        {
            descriptor = await FindFallbackDescriptorAsync(
                execution.TenantId, targetLanguage, inputBytes, segment.DurationMs, cancellationToken).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Descriptor lookup failure means no fallback; the budget policy decides review.
        catch (Exception)
#pragma warning restore CA1031
        {
            return null;
        }

        if (descriptor is null || descriptor.Provider != ProviderType.Mock)
        {
            return null;
        }

        var fallbackModel = ResolveFallbackModel(descriptor.Provider, primaryModel);
        try
        {
            var result = await InvokeOnceAsync(
                execution, descriptor.Provider, fallbackModel, ssml, targetLanguage,
                voice, segment, requestHash, ssmlHash,
                isPreview, isFallback: true, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation(
                "TTS fallback for segment {SegmentId} via {Provider} returned {Duration}ms.",
                segment.Id, descriptor.Provider.ToString(), result.Response.DurationMs);
            return result;
        }
#pragma warning disable CA1031 // Fallback failure keeps the primary outcome; the budget policy decides review.
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not LeaseLostException)
#pragma warning restore CA1031
        {
            _logger.LogWarning(
                "TTS fallback failed for segment {SegmentId}: {Error}.",
                segment.Id, ClassifyMessage(ex));
            return null;
        }
    }

    private async Task<ProviderCapabilityDescriptor?> FindFallbackDescriptorAsync(
        Guid tenantId,
        string language,
        long inputBytes,
        int durationMs,
        CancellationToken cancellationToken)
    {
        var candidates = await _descriptors.GetCandidatesAsync(ProviderCapability.Tts, tenantId, cancellationToken).ConfigureAwait(false);
        var routing = new ProviderRoutingRequest(language, inputBytes, durationMs, null, false, false, false);
        return candidates
            .Where(d => d is not null && d.Provider == ProviderType.Mock && _descriptors.IsCompatible(d, routing))
            .OrderBy(d => d.Id.ToString("N"), StringComparer.Ordinal)
            .FirstOrDefault();
    }

    private string ResolveFallbackModel(ProviderType provider, string primaryModel)
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

            if (string.Equals(option.Capability?.Trim(), nameof(ProviderCapability.Tts), StringComparison.OrdinalIgnoreCase)
                && !string.Equals(option.Model.Trim(), primaryModel?.Trim(), StringComparison.Ordinal))
            {
                return option.Model.Trim();
            }
        }

        if (!string.IsNullOrWhiteSpace(primaryModel))
        {
            return primaryModel;
        }

        return string.Concat(provider.ToString().ToLowerInvariant(), "-default");
    }

    private static byte[] ResolveAudioBytes(TtsResponse response, TtsRequest request)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(request);

        if (response.RawMetadata is not null
            && response.RawMetadata.TryGetValue("audioBase64", out var encoded)
            && !string.IsNullOrWhiteSpace(encoded))
        {
            try
            {
                return Convert.FromBase64String(encoded.Trim());
            }
            catch (FormatException ex)
            {
                throw new ErrorCodeException(ErrorCodes.ProviderInvalidResponse, "TTS provider returned malformed audio payload.", ex);
            }
        }

        var durationMs = response.DurationMs > 0 ? response.DurationMs : 1000;
        return GenerateSineWav(durationMs);
    }

    /// <summary>
    /// Deterministic 16kHz mono 16-bit PCM sine WAV (440Hz, 0.5 amplitude).
    /// Mirrors the mock provider waveform so Application stays
    /// dependency-free (no Infrastructure reference); byte-identical per
    /// duration. Pure.
    /// </summary>
    internal static byte[] GenerateSineWav(int durationMs)
    {
        const int sampleRateHz = 16000;
        const double frequencyHz = 440.0;
        var clamped = Math.Max(1, durationMs);
        var sampleCount = (int)((long)sampleRateHz * clamped / 1000);
        var dataBytes = sampleCount * 2;
        var result = new byte[44 + dataBytes];

        WriteAscii(result, 0, "RIFF");
        WriteInt32Le(result, 4, 36 + dataBytes);
        WriteAscii(result, 8, "WAVE");
        WriteAscii(result, 12, "fmt ");
        WriteInt32Le(result, 16, 16);
        WriteInt16Le(result, 20, 1);
        WriteInt16Le(result, 22, 1);
        WriteInt32Le(result, 24, sampleRateHz);
        WriteInt32Le(result, 28, sampleRateHz * 2);
        WriteInt16Le(result, 32, 2);
        WriteInt16Le(result, 34, 16);
        WriteAscii(result, 36, "data");
        WriteInt32Le(result, 40, dataBytes);

        for (var i = 0; i < sampleCount; i++)
        {
            var t = (double)i / sampleRateHz;
            var sample = (short)(Math.Sin(2.0 * Math.PI * frequencyHz * t) * 32767.0 * 0.5);
            result[44 + (i * 2)] = (byte)(sample & 0xFF);
            result[44 + (i * 2) + 1] = (byte)((sample >> 8) & 0xFF);
        }

        return result;
    }

    private static void WriteAscii(byte[] buffer, int offset, string value)
    {
        var bytes = Encoding.ASCII.GetBytes(value);
        Buffer.BlockCopy(bytes, 0, buffer, offset, bytes.Length);
    }

    private static void WriteInt32Le(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)(value & 0xFF);
        buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
        buffer[offset + 2] = (byte)((value >> 16) & 0xFF);
        buffer[offset + 3] = (byte)((value >> 24) & 0xFF);
    }

    private static void WriteInt16Le(byte[] buffer, int offset, short value)
    {
        buffer[offset] = (byte)(value & 0xFF);
        buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
    }

    private async Task<ValidatedAudio?> TryValidateAsync(byte[] audioBytes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(audioBytes);
        if (audioBytes.Length == 0)
        {
            return null;
        }

        var tempPath = Path.Combine(Path.GetTempPath(), string.Concat("dubbing-tts-", Guid.NewGuid().ToString("N"), ".wav"));
        try
        {
            await File.WriteAllBytesAsync(tempPath, audioBytes, cancellationToken).ConfigureAwait(false);
            FfprobeResult probe;
            try
            {
                probe = await _ffprobe.ProbeAsync(tempPath, cancellationToken).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // Undecodable bytes are a provider-invalid-response signal, not a crash: fallback/review decides.
            catch (Exception)
#pragma warning restore CA1031
            {
                return null;
            }

            if (probe.DurationMs <= 0)
            {
                return null;
            }

            if (probe.Streams is null
                || probe.Streams.Count == 0
                || !probe.Streams.Any(s => string.Equals(s.CodecType?.Trim(), "audio", StringComparison.OrdinalIgnoreCase)))
            {
                return null;
            }

            return new ValidatedAudio((int)Math.Min(probe.DurationMs, int.MaxValue));
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch (Exception)
            {
                // Best effort; OS temp cleaners cover leftovers.
            }
        }
    }

    private async Task RecordInvalidAsync(
        StageExecution execution,
        ProviderType provider,
        string model,
        TtsCallResult call,
        string requestHash,
        string ssmlHash,
        bool isPreview,
        CancellationToken cancellationToken)
    {
        await RecordExecutionAsync(
            execution, provider, model, call.Response, ssmlHash,
            null, OutcomeClass.ProviderInvalidResponse,
            requestHash, call.ResponseHash, call.LatencyMs,
            isPreview, call.IsFallback, cancellationToken).ConfigureAwait(false);
    }

    private async Task RecordExecutionAsync(
        StageExecution execution,
        ProviderType provider,
        string model,
        TtsResponse? response,
        string ssmlHash,
        string? voiceVersion,
        OutcomeClass outcome,
        string requestHash,
        string? responseHash,
        long latencyMs,
        bool isPreview,
        bool isFallback,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var scope = string.Concat(
            execution.ScopeType.ToString(), ":",
            execution.ScopeId, ":",
            provider.ToString(), ":",
            model,
            isFallback ? ":fallback" : string.Empty,
            isPreview ? ":preview" : string.Empty);
        var idempotencyKey = ProviderExecutionRecorder.BuildIdempotencyKey(
            execution.ProcessingRunId, nameof(StageType.VoiceGeneration), scope, execution.Attempt);
        string? externalJobId = null;
        if (response?.RawMetadata is not null
            && response.RawMetadata.TryGetValue("mock.job_id", out var jobId)
            && !string.IsNullOrWhiteSpace(jobId))
        {
            externalJobId = jobId.Trim();
        }

        var row = new ProviderExecution(
            Guid.NewGuid(), execution.TenantId, execution.ProjectId, execution.ProcessingRunId,
            execution.Id, provider, ProviderCapability.Tts, model,
            response?.ModelVersion, response?.Deployment, null, null,
            execution.Attempt, requestHash, responseHash, Math.Max(0, latencyMs),
            response?.Usage?.TokensIn, response?.Usage?.TokensOut, response?.Usage?.AudioSeconds,
            response?.Usage?.EstimatedCostUsd, response?.Usage?.EstimatedCostUsd, null,
            outcome, isFallback ? "tts-fallback" : null,
            SsmlTemplateId, ssmlHash, voiceVersion, externalJobId, idempotencyKey, now);
        await _recorder.RecordAsync(row, cancellationToken).ConfigureAwait(false);
    }

    private async Task<TtsSegmentResult?> TryReturnIdempotentAsync(
        Guid tenantId,
        StageExecution execution,
        Guid segmentId,
        bool isPreview,
        CancellationToken cancellationToken)
    {
        if (execution.Status is not (StageStatus.Completed or StageStatus.ManualReviewRequired))
        {
            return null;
        }

        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            var rows = await db.Set<GeneratedAudioArtifact>()
                .AsNoTracking()
                .Where(g => g.SegmentId == segmentId && g.RunId == execution.ProcessingRunId && g.IsPreview == isPreview)
                .OrderByDescending(g => g.CreatedAt)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            var existing = rows.FirstOrDefault();
            if (existing is null)
            {
                return null;
            }

            var artifactId = Guid.Empty;
            if (!string.IsNullOrWhiteSpace(execution.OutputArtifactIdsJson))
            {
                try
                {
                    var ids = JsonSerializer.Deserialize<string[]>(execution.OutputArtifactIdsJson, JsonOptions);
                    if (ids is not null && ids.Length > 0 && Guid.TryParseExact(ids[0].Trim(), "N", out var parsed))
                    {
                        artifactId = parsed;
                    }
                }
                catch (JsonException)
                {
                    artifactId = Guid.Empty;
                }
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

            return new TtsSegmentResult(
                segmentId, existing.Id, artifactId,
                existing.DurationMs, 0, 1.0,
                existing.Provider, existing.Model, string.Empty,
                isPreview, execution.Status == StageStatus.ManualReviewRequired, reviewId);
        }
    }

    private async Task<Guid?> FindTranslationArtifactAsync(
        Guid tenantId,
        Guid runId,
        Guid segmentId,
        CancellationToken cancellationToken)
    {
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            var rows = await db.Set<Artifact>()
                .AsNoTracking()
                .Where(a => a.ProcessingRunId == runId && a.Type == ArtifactType.Translation)
                .OrderByDescending(a => a.CreatedAt)
                .Select(a => new { a.Id, a.MetadataJson })
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            var needle = segmentId.ToString("N");
            foreach (var row in rows)
            {
                if (!string.IsNullOrWhiteSpace(row.MetadataJson)
                    && row.MetadataJson.Contains(needle, StringComparison.Ordinal))
                {
                    return row.Id;
                }
            }

            return null;
        }
    }

    private async Task<LoadedTranslation> LoadSelectedTranslationAsync(
        Guid tenantId, Guid projectId, Guid runId, Guid segmentId, CancellationToken cancellationToken)
    {
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            var versions = await db.Set<TranslationVersion>()
                .AsNoTracking()
                .Where(v => v.SegmentId == segmentId)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            var winner = versions.FirstOrDefault(v => v.IsSelected)
                ?? versions.OrderByDescending(v => v.CreatedAt).FirstOrDefault();
            if (winner is null)
            {
                throw new ErrorCodeException(ErrorCodes.ValidationFailed, $"Segment '{segmentId:D}' has no selected translation; TTS cannot proceed.");
            }

            if (winner.TenantId != tenantId || winner.ProjectId != projectId || winner.RunId != runId)
            {
                throw new ForbiddenException($"Translation for segment '{segmentId:D}' does not belong to the current tenant/project/run.");
            }

            Guid? artifactId = await FindTranslationArtifactAsync(tenantId, runId, segmentId, cancellationToken).ConfigureAwait(false);
            return new LoadedTranslation(winner.Id, winner.PrimaryText, artifactId);
        }
    }

    private async Task<LoadedVoice> LoadAssignedVoiceAsync(
        Guid tenantId, Guid projectId, Guid runId, SpeechSegment segment, CancellationToken cancellationToken)
    {
        if (segment.SpeakerId is null || segment.SpeakerId.Value == Guid.Empty)
        {
            throw new ErrorCodeException(
                ErrorCodes.PipelineInvariantViolation,
                $"Segment '{segment.Id:D}' has no mapped speaker; VoiceAssignment must complete before VoiceGeneration.");
        }

        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            var assignment = await db.Set<SpeakerVoiceAssignment>()
                .AsNoTracking()
                .FirstOrDefaultAsync(a => a.RunId == runId && a.SpeakerId == segment.SpeakerId.Value, cancellationToken).ConfigureAwait(false);
            if (assignment is null)
            {
                throw new ErrorCodeException(
                    ErrorCodes.PipelineInvariantViolation,
                    $"Segment '{segment.Id:D}' has no voice assignment; VoiceAssignment must complete before VoiceGeneration.");
            }

            if (assignment.TenantId != tenantId || assignment.ProjectId != projectId)
            {
                throw new ForbiddenException($"Voice assignment for segment '{segment.Id:D}' does not belong to the current tenant/project.");
            }

            var profile = await db.Set<VoiceProfile>()
                .AsNoTracking()
                .FirstOrDefaultAsync(v => v.Id == assignment.VoiceProfileId, cancellationToken).ConfigureAwait(false);
            if (profile is null)
            {
                throw new ErrorCodeException(
                    ErrorCodes.PipelineInvariantViolation,
                    $"Voice profile '{assignment.VoiceProfileId:D}' was not found for segment '{segment.Id:D}'.");
            }

            if (profile.TenantId != tenantId)
            {
                throw new ForbiddenException($"Voice profile '{profile.Id:D}' does not belong to the current tenant.");
            }

            return new LoadedVoice(profile, assignment.Id);
        }
    }

    private static string? ValidateResponse(TtsResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);

        if (string.IsNullOrWhiteSpace(response.ContentObjectId))
        {
            return "TTS provider returned no content id.";
        }

        if (string.IsNullOrWhiteSpace(response.VoiceId))
        {
            return "TTS provider returned no voice id.";
        }

        if (string.IsNullOrWhiteSpace(response.Model))
        {
            return "TTS provider returned no model.";
        }

        if (response.DurationMs < 0)
        {
            return "TTS provider returned an invalid duration.";
        }

        if (double.IsNaN(response.Confidence))
        {
            return "TTS provider returned an invalid confidence.";
        }

        return null;
    }

    private static bool IsTransport(Exception exception)
    {
        return exception is HttpRequestException or TimeoutException or SocketException or IOException;
    }

    private static bool IsRetryable(ErrorCodeException exception)
    {
        return string.Equals(exception.ErrorCode, ErrorCodes.RateLimited, StringComparison.Ordinal)
            || string.Equals(exception.ErrorCode, ErrorCodes.ProviderRateLimited, StringComparison.Ordinal)
            || string.Equals(exception.ErrorCode, ErrorCodes.ProviderTimeout, StringComparison.Ordinal)
            || string.Equals(exception.ErrorCode, ErrorCodes.ProviderQuotaExhausted, StringComparison.Ordinal)
            || string.Equals(exception.ErrorCode, ErrorCodes.ProviderFailed, StringComparison.Ordinal);
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

    private async Task ReconcileCostHoldAsync(
        CostReservation? hold,
        Guid tenantId,
        double? providerReported,
        double estimate)
    {
        if (hold is null || _costs is null)
        {
            return;
        }

        var actual = providerReported.HasValue && !double.IsNaN(providerReported.Value) && providerReported.Value >= 0.0
            ? providerReported.Value
            : Math.Max(0.0, estimate);
        try
        {
            await _costs.ReconcileAsync(tenantId, hold.Id, actual, providerReported, CancellationToken.None).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Reconcile is best effort after success; the TTS result stands.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogWarning(ex, "Cost reconcile failed for reservation {ReservationId}.", hold.Id);
        }
    }

    private async Task ReleaseCostHoldAsync(
        CostReservation? hold,
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        if (hold is null || _costs is null)
        {
            return;
        }

        try
        {
            await _costs.ReleaseAsync(tenantId, hold.Id, cancellationToken).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Release is best effort on failure paths; the original error propagates.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogWarning(ex, "Cost release failed for reservation {ReservationId}.", hold.Id);
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

            if (execution.StageType != StageType.VoiceGeneration)
            {
                throw new DomainException($"Stage execution '{executionId}' is '{execution.StageType}', not VoiceGeneration.");
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
            return "TTS failed.";
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
