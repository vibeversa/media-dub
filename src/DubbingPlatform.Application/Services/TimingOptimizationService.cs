using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Text.Json;
using DubbingPlatform.Application.Abstractions;
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
/// Outcome of one segment timing optimization. <see cref="NeedsReview"/> is true
/// only for the exhausted terminal path (review item created, execution moved to
/// <c>ManualReviewRequired</c>); otherwise the chosen audio is completed.
/// <see cref="Rate"/> is the applied prosody rate (1.0 when untouched) and
/// <see cref="StretchFactor"/> the applied FFmpeg tempo (1.0 when untouched);
/// both never exceed their caps (rate ±15%, stretch 1.15x symmetric).
/// </summary>
public sealed record TimingOptimizationResult(
    Guid SegmentId,
    Guid SyncResultId,
    double SyncScore01,
    SyncStatus Status,
    int TargetMs,
    int ActualMs,
    double Rate,
    double StretchFactor,
    Guid? ChosenTranslationVersionId,
    Guid ChosenAudioArtifactId,
    IReadOnlyList<Guid> PreviewArtifactIds,
    bool NeedsReview,
    Guid? ReviewItemId);

/// <summary>
/// One bounded optimization attempt. <c>Kind</c> is one of
/// <c>initial</c>, <c>alternate</c>, <c>prosody</c>, <c>rewrite</c>,
/// <c>stretch</c>. <see cref="SyncScore"/> is 0..100 (persistence normalizes to
/// 0..1). Pure planning record; the service re-measures stretch outputs via
/// FFprobe before persisting.
/// </summary>
public sealed record TimingAttempt(
    int Ordinal,
    string Kind,
    int DurationMs,
    double Rate,
    double StretchFactor,
    double SyncScore,
    SyncStatus Status);

/// <summary>
/// Bounded deterministic plan. <see cref="ChosenIndex"/> is the best attempt
/// (minimum |duration-target|, then maximum score, then earliest ordinal).
/// <see cref="StretchSkippedForCap"/> is true when a required stretch exceeded
/// 1.15x and was skipped without violation (review instead).
/// </summary>
public sealed record TimingPlan(
    IReadOnlyList<TimingAttempt> Attempts,
    int ChosenIndex,
    int CandidateCount,
    int PreviewCount,
    int RewriteCount,
    SyncStatus FinalStatus,
    double FinalScore,
    bool StretchSkippedForCap);

/// <summary>
/// Timing meters. Counter names are frozen: renaming breaks dashboards.
/// </summary>
public static class TimingMeters
{
    public const string MeterName = "DubbingPlatform.Timing";

    public const string BudgetExceededMetricName = "timing.budget_exceeded_total";

    public const string ReviewMetricName = "timing.review_total";

    private static readonly Meter Meter = new(MeterName);

    public static readonly Counter<long> BudgetExceeded =
        Meter.CreateCounter<long>(BudgetExceededMetricName);

    public static readonly Counter<long> Reviews =
        Meter.CreateCounter<long>(ReviewMetricName);
}

/// <summary>
/// Segment-scoped timing optimization with bounded deterministic search,
/// explicit classification, and hard-cap enforcement. Dimensions: onset is
/// fixed at the segment start (<c>onsetErr=0</c>, placement never shifts —
/// onset/lead-lag are validated by timeline assembly in Task 30, not moved
/// here); voiced-duration is the FFprobe-measured generated duration;
/// window is the segment <c>DurationMs</c>; internal silence is 0 in this task
/// (no silence detector yet; the penalty term is wired at 0 so the formula is
/// complete without inventing data); rate is capped at ±15% and stretch at
/// 1.15x symmetric (<c>[1/1.15, 1.15]</c> so both speed-up and slow-down fit).
/// Loop order per call: alternate translation estimates (candidate budget) →
/// prosody rate fit (preview budget) → rewrite reuses of later alternatives
/// (rewrite budget, no provider call: deterministic reuse of stored
/// alternatives keeps the loop bounded and cost-free; an LLM rewrite provider
/// call is out of scope) → FFmpeg <c>atempo</c> stretch previews (preview
/// budget, preview artifacts only). After each try the duration is re-scored;
/// the loop stops when <c>|durationErr|</c> is within tolerance. Hard caps are
/// never exceeded even when still out-of-tolerance (goes to review instead;
/// cap skips are logged as invariant errors). Zero-duration generated audio
/// fails fast with <c>PROVIDER_INVALID_RESPONSE</c> (no stretch). Review path
/// preserves the current translation/audio selection (the reviewer decides);
/// the acceptable path updates <c>TranslationVersion.IsSelected</c> when the
/// winner text changed. Every stretch preview records a
/// <c>ProviderExecution</c> (Mock/Tts, model <c>ffmpeg-atempo-1</c>: no Timing
/// capability exists, so Tts carries the audio audit) and persists the exact
/// FFmpeg args in artifact metadata. Never logs text, SSML, audio, or secrets:
/// only ids, durations, rates, factors, scores, and hashes.
/// </summary>
public sealed class TimingOptimizationService
{
    /// <summary>Review reason for exhausted out-of-tolerance timing.</summary>
    public const string ReviewReason = "TIMING_SYNC";

    /// <summary>Artifact/row schema version for every timing payload.</summary>
    public const string SchemaVersion = "1";

    /// <summary>Stretch preview model identity (local FFmpeg, no provider).</summary>
    public const string StretchModel = "ffmpeg-atempo-1";

    /// <summary>Stretch audit template identity (no secrets in the id).</summary>
    public const string StretchTemplateId = "timing-stretch-v1";

    /// <summary>Stretch audit template version.</summary>
    public const string StretchTemplateVersion = "1";

    /// <summary>Attempt kind: first measurement.</summary>
    public const string KindInitial = "initial";

    /// <summary>Attempt kind: stored alternative translation estimate.</summary>
    public const string KindAlternate = "alternate";

    /// <summary>Attempt kind: prosody rate fit (no new audio).</summary>
    public const string KindProsody = "prosody";

    /// <summary>Attempt kind: rewrite reuse of a later alternative.</summary>
    public const string KindRewrite = "rewrite";

    /// <summary>Attempt kind: FFmpeg atempo stretch preview.</summary>
    public const string KindStretch = "stretch";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IStageExecutionContextFactory _contextFactory;
    private readonly ArtifactService _artifacts;
    private readonly IArtifactStorage _storage;
    private readonly IFFprobeService _ffprobe;
    private readonly IFFmpegService _ffmpeg;
    private readonly ProviderExecutionRecorder _recorder;
    private readonly TimingOptions _timing;
    private readonly MediaOptions _media;
    private readonly RetryOptions _retry;
    private readonly ILogger<TimingOptimizationService> _logger;

    public TimingOptimizationService(
        IStageExecutionContextFactory contextFactory,
        ArtifactService artifacts,
        IArtifactStorage storage,
        IFFprobeService ffprobe,
        IFFmpegService ffmpeg,
        ProviderExecutionRecorder recorder,
        IOptions<TimingOptions> timingOptions,
        IOptions<MediaOptions> mediaOptions,
        IOptions<RetryOptions> retryOptions,
        ILogger<TimingOptimizationService> logger)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        ArgumentNullException.ThrowIfNull(artifacts);
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(ffprobe);
        ArgumentNullException.ThrowIfNull(ffmpeg);
        ArgumentNullException.ThrowIfNull(recorder);
        ArgumentNullException.ThrowIfNull(timingOptions);
        ArgumentNullException.ThrowIfNull(mediaOptions);
        ArgumentNullException.ThrowIfNull(retryOptions);
        ArgumentNullException.ThrowIfNull(logger);
        _contextFactory = contextFactory;
        _artifacts = artifacts;
        _storage = storage;
        _ffprobe = ffprobe;
        _ffmpeg = ffmpeg;
        _recorder = recorder;
        _timing = timingOptions.Value;
        _media = mediaOptions.Value;
        _retry = retryOptions.Value;
        _logger = logger;
    }

    /// <summary>
    /// Deterministic sync score 0..100 (documented formula):
    /// <c>100 - (w1*|durationErr|/10 + w2*|onsetErr|/10 + w3*|rateDelta|*100 +
    /// w4*stretchExcess*50 + w5*silenceMs/100)</c> with
    /// <c>w1=1, w2=1, w3=0.5, w4=2, w5=0.2</c>, clamped to 0..100. Pure.
    /// <paramref name="rateDelta"/> is <c>|rate-1|</c>,
    /// <paramref name="stretchExcess"/> is <c>|factor-1|</c> (0 when untouched),
    /// <paramref name="silenceMs"/> is internal silence (0 in this task).
    /// NaN inputs are treated as worst-case (score 0 via clamp).
    /// </summary>
    public static double ComputeSyncScore(
        double durationErrMs,
        double onsetErrMs,
        double rateDelta,
        double stretchExcess,
        double silenceMs)
    {
        if (double.IsNaN(durationErrMs) || double.IsNaN(onsetErrMs)
            || double.IsNaN(rateDelta) || double.IsNaN(stretchExcess) || double.IsNaN(silenceMs))
        {
            return 0.0;
        }

        var penalty =
            (1.0 * Math.Abs(durationErrMs) / 10.0)
            + (1.0 * Math.Abs(onsetErrMs) / 10.0)
            + (0.5 * Math.Abs(rateDelta) * 100.0)
            + (2.0 * Math.Abs(stretchExcess) * 50.0)
            + (0.2 * Math.Abs(silenceMs) / 100.0);
        if (double.IsNaN(penalty) || double.IsInfinity(penalty))
        {
            return 0.0;
        }

        return Math.Clamp(100.0 - penalty, 0.0, 100.0);
    }

    /// <summary>
    /// Explicit classification. Pure: <c>|err| ≤ preferred</c> →
    /// <c>SyncAcceptable</c>; <c>≤ max</c> → <c>SyncAcceptableWithWarning</c>;
    /// else <c>attemptsRemain</c> → <c>SyncRetryable</c> else
    /// <c>ManualReviewRequired</c>.
    /// </summary>
    public static SyncStatus Classify(int absDurationErrMs, int preferredMs, int maxMs, bool attemptsRemain)
    {
        var abs = Math.Abs(absDurationErrMs);
        if (abs <= Math.Max(0, preferredMs))
        {
            return SyncStatus.SyncAcceptable;
        }

        if (abs <= Math.Max(Math.Max(0, preferredMs), Math.Max(0, maxMs)))
        {
            return SyncStatus.SyncAcceptableWithWarning;
        }

        return attemptsRemain ? SyncStatus.SyncRetryable : SyncStatus.ManualReviewRequired;
    }

    /// <summary>
    /// Clamps a prosody rate to <c>[1-maxPercent/100, 1+maxPercent/100]</c>.
    /// Pure. Non-finite rates yield 1.0 (no adjustment).
    /// </summary>
    public static double ClampRate(double rate, double maxPercent)
    {
        if (double.IsNaN(rate) || double.IsInfinity(rate))
        {
            return 1.0;
        }

        var bound = Math.Clamp(maxPercent, 0.0, 100.0) / 100.0;
        return Math.Clamp(rate, 1.0 - bound, 1.0 + bound);
    }

    /// <summary>
    /// Clamps a stretch factor to the symmetric <c>[1/maxFactor, maxFactor]</c>
    /// window so both speed-up and slow-down fit. Pure. Non-finite factors
    /// yield 1.0 (no stretch). <paramref name="maxFactor"/> is clamped to
    /// <c>[1.0, 2.0]</c> (matches <c>Timing:MaxStretchFactor</c> validation).
    /// </summary>
    public static double ClampStretch(double factor, double maxFactor)
    {
        if (double.IsNaN(factor) || double.IsInfinity(factor))
        {
            return 1.0;
        }

        var max = Math.Clamp(maxFactor, 1.0, 2.0);
        return Math.Clamp(factor, 1.0 / max, max);
    }

    /// <summary>
    /// Prosody rate fitting <c>actual/target</c> within the percent cap
    /// (rate &gt; 1 speeds up a too-long rendering, &lt; 1 slows a too-short
    /// one). Pure. Non-positive inputs yield 1.0.
    /// </summary>
    public static double RateFor(int actualMs, int targetMs, double maxPercent)
    {
        if (actualMs <= 0 || targetMs <= 0)
        {
            return 1.0;
        }

        return ClampRate((double)actualMs / targetMs, maxPercent);
    }

    /// <summary>
    /// Stretch factor fitting <c>actual/target</c> within the symmetric factor
    /// cap (FFmpeg <c>atempo</c> &gt; 1 shortens, &lt; 1 lengthens). Pure.
    /// Non-positive inputs yield 1.0.
    /// </summary>
    public static double StretchFor(int actualMs, int targetMs, double maxFactor)
    {
        if (actualMs <= 0 || targetMs <= 0)
        {
            return 1.0;
        }

        return ClampStretch((double)actualMs / targetMs, maxFactor);
    }

    /// <summary>
    /// Applies a rate/factor to a duration: <c>round(actual/factor)</c>, minimum
    /// 1ms. Pure. Non-positive actual yields 1; non-finite/non-positive factors
    /// yield the input clamped to ≥ 1.
    /// </summary>
    public static int AdjustedDuration(int actualMs, double factor)
    {
        if (actualMs <= 0)
        {
            return 1;
        }

        if (double.IsNaN(factor) || double.IsInfinity(factor) || factor <= 0.0)
        {
            return Math.Max(1, actualMs);
        }

        return Math.Max(1, (int)Math.Round(actualMs / factor, MidpointRounding.AwayFromZero));
    }

    /// <summary>
    /// Builds the FFmpeg time-stretch args (single <c>atempo</c> filter; the
    /// 1.15x cap always fits one filter stage, whose range is 0.5..100).
    /// Output is 16kHz mono 16-bit wav like generated audio. Pure; the caller
    /// passes the list via <c>ArgumentList</c> (never shell). Throws
    /// <see cref="DomainException"/> when <paramref name="tempo"/> is outside
    /// <c>[0.5, 100]</c> so cap violations fail fast instead of reaching FFmpeg.
    /// </summary>
    public static IReadOnlyList<string> BuildStretchArgs(string sourcePath, string destPath, double tempo, int cpuThreads)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destPath);
        ArgumentOutOfRangeException.ThrowIfLessThan(cpuThreads, 1);
        if (double.IsNaN(tempo) || double.IsInfinity(tempo) || tempo < 0.5 || tempo > 100.0)
        {
            throw new DomainException("Stretch tempo must be in 0.5..100 for a single atempo filter.");
        }

        return [
            "-y",
            "-threads", cpuThreads.ToString(CultureInfo.InvariantCulture),
            "-i", sourcePath,
            "-filter:a", string.Concat("atempo=", tempo.ToString("F3", CultureInfo.InvariantCulture)),
            "-vn", "-ar", "16000", "-ac", "1", "-c:a", "pcm_s16le",
            destPath];
    }

    /// <summary>
    /// Bounded deterministic plan over mock durations (hermetic; no I/O).
    /// Order: initial → alternates (candidate budget) → prosody fit (preview
    /// budget) → rewrite reuses (rewrite budget) → iterative atempo stretch
    /// (preview budget, each step re-fits the current best so output converges;
    /// a required factor outside the cap skips stretch without violation).
    /// Never exceeds <c>MaxCandidates</c>/<c>MaxTtsPreviewAttempts</c>/
    /// <c>MaxRewrites</c>; stops early when within <c>MaxToleranceMs</c>.
    /// Non-positive mock durations are skipped (never selected). Throws
    /// <see cref="ErrorCodeException"/> (<c>PROVIDER_INVALID_RESPONSE</c>) for
    /// non-positive <paramref name="initialMs"/> (zero-duration generated audio
    /// is undecodable: no stretch attempted).
    /// </summary>
    public static TimingPlan Plan(
        int targetMs,
        int initialMs,
        IReadOnlyList<int> alternateMs,
        IReadOnlyList<int> rewriteMs,
        TimingOptions options)
    {
        ArgumentNullException.ThrowIfNull(alternateMs);
        ArgumentNullException.ThrowIfNull(rewriteMs);
        ArgumentNullException.ThrowIfNull(options);
        if (targetMs <= 0)
        {
            throw new DomainException("Target window must be > 0.");
        }

        if (initialMs <= 0)
        {
            throw new ErrorCodeException(ErrorCodes.ProviderInvalidResponse, "Generated audio has zero duration; timing cannot proceed.");
        }

        var maxCandidates = Math.Clamp(options.MaxCandidates, 1, 10);
        var maxPreviews = Math.Clamp(options.MaxTtsPreviewAttempts, 1, 10);
        var maxRewrites = Math.Clamp(options.MaxRewrites, 0, 10);
        var preferred = Math.Max(0, options.PreferredToleranceMs);
        var max = Math.Max(preferred, Math.Max(0, options.MaxToleranceMs));
        var maxRatePercent = Math.Clamp(double.IsNaN(options.MaxRateChangePercent) ? 15.0 : options.MaxRateChangePercent, 0.0, 100.0);
        var maxStretch = Math.Clamp(double.IsNaN(options.MaxStretchFactor) ? 1.15 : options.MaxStretchFactor, 1.0, 2.0);

        var attempts = new List<TimingAttempt>();
        var candidateCount = 0;
        var previewCount = 0;
        var rewriteCount = 0;
        var stretchSkipped = false;

        bool BudgetsRemain()
        {
            return candidateCount < maxCandidates || previewCount < maxPreviews || rewriteCount < maxRewrites;
        }

        void Add(string kind, int durationMs, double rate, double stretch)
        {
            var err = durationMs - targetMs;
            var score = ComputeSyncScore(err, 0, Math.Abs(rate - 1.0), Math.Abs(stretch - 1.0), 0);
            var status = Classify(Math.Abs(err), preferred, max, BudgetsRemain());
            attempts.Add(new TimingAttempt(attempts.Count, kind, durationMs, rate, stretch, score, status));
        }

        bool WithinMax(int durationMs)
        {
            return Math.Abs(durationMs - targetMs) <= max;
        }

        int BestIndex()
        {
            var best = 0;
            for (var i = 1; i < attempts.Count; i++)
            {
                var errBest = Math.Abs(attempts[best].DurationMs - targetMs);
                var errCur = Math.Abs(attempts[i].DurationMs - targetMs);
                if (errCur < errBest
                    || (errCur == errBest && attempts[i].SyncScore > attempts[best].SyncScore))
                {
                    best = i;
                }
            }

            return best;
        }

        Add(KindInitial, initialMs, 1.0, 1.0);
        candidateCount++;

        if (!WithinMax(attempts[BestIndex()].DurationMs))
        {
            foreach (var alt in alternateMs)
            {
                if (candidateCount >= maxCandidates || WithinMax(attempts[BestIndex()].DurationMs))
                {
                    break;
                }

                if (alt <= 0)
                {
                    continue;
                }

                Add(KindAlternate, alt, 1.0, 1.0);
                candidateCount++;
            }
        }

        if (!WithinMax(attempts[BestIndex()].DurationMs) && previewCount < maxPreviews)
        {
            var best = attempts[BestIndex()].DurationMs;
            var rate = RateFor(best, targetMs, maxRatePercent);
            var adjusted = AdjustedDuration(best, rate);
            if (adjusted != best)
            {
                Add(KindProsody, adjusted, rate, 1.0);
                previewCount++;
            }
        }

        if (!WithinMax(attempts[BestIndex()].DurationMs))
        {
            foreach (var rw in rewriteMs)
            {
                if (rewriteCount >= maxRewrites || WithinMax(attempts[BestIndex()].DurationMs))
                {
                    break;
                }

                if (rw <= 0)
                {
                    continue;
                }

                Add(KindRewrite, rw, 1.0, 1.0);
                rewriteCount++;
            }
        }

        while (!WithinMax(attempts[BestIndex()].DurationMs) && previewCount < maxPreviews)
        {
            var best = attempts[BestIndex()].DurationMs;
            var required = (double)best / targetMs;
            if (required < (1.0 / maxStretch) - 1e-9 || required > maxStretch + 1e-9)
            {
                stretchSkipped = true;
                break;
            }

            var factor = ClampStretch(required, maxStretch);
            var stretched = AdjustedDuration(best, factor);
            if (stretched == best)
            {
                break;
            }

            Add(KindStretch, stretched, 1.0, factor);
            previewCount++;
        }

        var chosen = BestIndex();
        var final = attempts[chosen];
        var finalStatus = Classify(Math.Abs(final.DurationMs - targetMs), preferred, max, false);
        attempts[chosen] = final with { Status = finalStatus };

        return new TimingPlan(
            attempts, chosen, candidateCount, previewCount, rewriteCount,
            finalStatus, attempts[chosen].SyncScore, stretchSkipped);
    }

    /// <summary>
    /// Builds the persistent <see cref="SyncResult"/> (score normalized from
    /// 0..100 to the entity 0..1 range). Pure.
    /// </summary>
    public static SyncResult BuildSyncResult(
        Guid tenantId,
        Guid projectId,
        Guid runId,
        Guid segmentId,
        int targetMs,
        int actualMs,
        double rate,
        double stretchFactor,
        double score100,
        SyncStatus status)
    {
        if (tenantId == Guid.Empty || projectId == Guid.Empty || runId == Guid.Empty || segmentId == Guid.Empty)
        {
            throw new DomainException("Timing ids must not be empty.");
        }

        var normalized = Math.Clamp(score100 / 100.0, 0.0, 1.0);
        if (double.IsNaN(normalized))
        {
            normalized = 0.0;
        }

        return new SyncResult(
            Guid.NewGuid(), tenantId, projectId, runId, segmentId,
            normalized, status, Math.Max(0, targetMs), Math.Max(0, actualMs),
            rate, stretchFactor <= 0.0 ? 1.0 : stretchFactor, DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Builds the deterministic review payload (ids, durations, scores only;
    /// never text or audio). Pure.
    /// </summary>
    public static string BuildReviewPayload(
        Guid segmentId,
        int sequence,
        int targetMs,
        int actualMs,
        double score100,
        SyncStatus status,
        int attemptCount)
    {
        return JsonSerializer.Serialize(new
        {
            reason = ReviewReason,
            segmentId = segmentId.ToString("N"),
            sequence,
            targetMs,
            actualMs,
            durationErrMs = actualMs - targetMs,
            syncScore = Math.Clamp(score100, 0.0, 100.0),
            status = status.ToString(),
            attemptCount,
        }, JsonOptions);
    }

    /// <summary>
    /// Optimizes one segment end to end (measure, bounded plan, optional
    /// stretch preview, persistence, stage commit). Terminal executions return
    /// their stored outcome (idempotent redelivery safe). See class docs for
    /// the policy matrix.
    /// </summary>
    public async Task<TimingOptimizationResult> OptimizeAsync(
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
            throw new ErrorCodeException(ErrorCodes.ValidationFailed, $"Segment '{segmentId:D}' has an empty window; timing cannot proceed.");
        }

        var target = segment.DurationMs;
        var finalAudio = await LoadFinalAudioAsync(tenantId, projectId, runId, segmentId, cancellationToken).ConfigureAwait(false);
        if (finalAudio.DurationMs <= 0)
        {
            throw new ErrorCodeException(ErrorCodes.ProviderInvalidResponse, "Generated audio has zero duration; timing cannot proceed.");
        }

        var storageKey = await LoadStorageKeyAsync(tenantId, finalAudio.ContentObjectId, cancellationToken).ConfigureAwait(false);
        var actual = await ProbeDurationAsync(storageKey, cancellationToken).ConfigureAwait(false);

        var translation = await LoadSelectedTranslationAsync(tenantId, projectId, runId, segmentId, cancellationToken).ConfigureAwait(false);
        var targetLanguage = project.TargetLanguage?.Trim();
        if (string.IsNullOrWhiteSpace(targetLanguage))
        {
            throw new ErrorCodeException(ErrorCodes.ValidationFailed, $"Project '{projectId:D}' has no target language for timing estimates.");
        }

        var maxCandidates = Math.Clamp(_timing.MaxCandidates, 1, 10);
        var maxRewrites = Math.Clamp(_timing.MaxRewrites, 0, 10);
        var alternates = translation.AlternativeTexts
            .Where(t => !string.IsNullOrWhiteSpace(t) && !string.Equals(t.Trim(), translation.PrimaryText.Trim(), StringComparison.Ordinal))
            .Select(t => t.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var candidateTexts = alternates.Take(Math.Max(0, maxCandidates - 1)).ToList();
        var rewriteTexts = alternates.Skip(candidateTexts.Count).Take(maxRewrites).ToList();
        var candidateDurations = candidateTexts
            .Select(t => DurationEstimator.EstimateMs(t, targetLanguage))
            .Where(d => d > 0)
            .ToList();
        var rewriteDurations = rewriteTexts
            .Select(t => DurationEstimator.EstimateMs(t, targetLanguage))
            .Where(d => d > 0)
            .ToList();

        var plan = Plan(target, actual, candidateDurations, rewriteDurations, _timing);
        var chosen = plan.Attempts[plan.ChosenIndex];

        if (plan.StretchSkippedForCap)
        {
            _logger.LogError(
                "Timing invariant: segment {SegmentId} requires stretch beyond {Max}x for window {Target}ms (best {Best}ms); skipping stretch, routing to review.",
                segmentId, _timing.MaxStretchFactor, target, plan.Attempts.Select(a => a.DurationMs).OrderBy(d => Math.Abs(d - target)).First());
        }

        Guid? chosenTranslationVersionId = null;
        if (plan.FinalStatus is SyncStatus.SyncAcceptable or SyncStatus.SyncAcceptableWithWarning)
        {
            var chosenText = TextForAttempt(plan, translation.PrimaryText, candidateTexts, rewriteTexts);
            if (!string.Equals(chosenText.Trim(), translation.PrimaryText.Trim(), StringComparison.Ordinal))
            {
                chosenTranslationVersionId = await PersistAlternateSelectionAsync(
                    tenantId, projectId, runId, segment, translation, chosenText, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                chosenTranslationVersionId = translation.VersionId;
            }
        }

        Guid chosenAudioArtifactId;
        var previewIds = new List<Guid>();
        int persistedActual = chosen.DurationMs;
        double persistedRate = chosen.Rate;
        double persistedStretch = chosen.StretchFactor;

        if (plan.FinalStatus is SyncStatus.SyncAcceptable or SyncStatus.SyncAcceptableWithWarning
            && string.Equals(chosen.Kind, KindStretch, StringComparison.Ordinal))
        {
            var stretch = await RunStretchPreviewAsync(
                tenantId, projectId, runId, segment, execution, owner, token,
                finalAudio, storageKey, chosen.StretchFactor, plan,
                chosenTranslationVersionId, target, cancellationToken).ConfigureAwait(false);
            if (stretch is not null)
            {
                previewIds.Add(stretch.PreviewArtifactId);
                persistedActual = stretch.ProbedMs;
                persistedRate = chosen.Rate;
                persistedStretch = chosen.StretchFactor;
                chosenAudioArtifactId = stretch.PreviewArtifactId;
            }
            else
            {
                var fallback = await LoadFinalArtifactIdAsync(tenantId, runId, segmentId, cancellationToken).ConfigureAwait(false);
                chosenAudioArtifactId = fallback ?? Guid.Empty;
            }
        }
        else
        {
            var finalArtifactId = await LoadFinalArtifactIdAsync(tenantId, runId, segmentId, cancellationToken).ConfigureAwait(false);
            if (finalArtifactId is null || finalArtifactId.Value == Guid.Empty)
            {
                throw new ErrorCodeException(
                    ErrorCodes.PipelineInvariantViolation,
                    $"Segment '{segmentId:D}' has generated audio but no final artifact; VoiceGeneration must complete before TimingOptimization.");
            }

            chosenAudioArtifactId = finalArtifactId.Value;
        }

        var score = ComputeSyncScore(
            persistedActual - target, 0, Math.Abs(persistedRate - 1.0), Math.Abs(persistedStretch - 1.0), 0);
        var status = Classify(Math.Abs(persistedActual - target), _timing.PreferredToleranceMs, _timing.MaxToleranceMs, false);

        await EnsureLeaseRunningAsync(tenantId, execution.Id, owner, token, cancellationToken).ConfigureAwait(false);
        var syncId = await PersistSyncResultAsync(
            tenantId, projectId, runId, segment, target, persistedActual,
            persistedRate, persistedStretch, score, status, cancellationToken).ConfigureAwait(false);

        if (status == SyncStatus.ManualReviewRequired)
        {
            TimingMeters.BudgetExceeded.Add(1);
            var reviewId = await CreateReviewAsync(
                tenantId, projectId, runId, segment, target, persistedActual,
                score, status, plan.Attempts.Count, cancellationToken).ConfigureAwait(false);
            await EnsureLeaseRunningAsync(tenantId, execution.Id, owner, token, cancellationToken).ConfigureAwait(false);
            var stages = new StageExecutionService(_contextFactory, Microsoft.Extensions.Options.Options.Create(_retry));
            await stages.MarkReviewRequiredAsync(tenantId, execution.Id, owner, token, cancellationToken).ConfigureAwait(false);
            TimingMeters.Reviews.Add(1);
            _logger.LogInformation(
                "Timing for segment {SegmentId} routed to review {ReviewId} (err {Err}ms, score {Score}).",
                segmentId, reviewId, persistedActual - target, score);
            return new TimingOptimizationResult(
                segmentId, syncId, Math.Clamp(score / 100.0, 0.0, 1.0), status,
                target, persistedActual, persistedRate, persistedStretch,
                chosenTranslationVersionId, chosenAudioArtifactId, previewIds,
                true, reviewId);
        }

        await EnsureLeaseRunningAsync(tenantId, execution.Id, owner, token, cancellationToken).ConfigureAwait(false);
        var completer = new StageExecutionService(_contextFactory, Microsoft.Extensions.Options.Options.Create(_retry));
        await completer.CompleteAsync(
            tenantId, execution.Id, owner, token,
            [chosenAudioArtifactId.ToString("N")],
            cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Timing optimized segment {SegmentId} for run {RunId}: err {Err}ms, score {Score}, status {Status}.",
            segmentId, runId, persistedActual - target, score, status.ToString());
        return new TimingOptimizationResult(
            segmentId, syncId, Math.Clamp(score / 100.0, 0.0, 1.0), status,
            target, persistedActual, persistedRate, persistedStretch,
            chosenTranslationVersionId, chosenAudioArtifactId, previewIds,
            false, null);
    }

    private sealed record LoadedTranslation(Guid VersionId, string PrimaryText, string[] AlternativeTexts);

    private sealed record LoadedAudio(Guid Id, Guid ContentObjectId, Guid VoiceProfileId, int DurationMs, string Provider, string Model);

    private sealed record StretchOutcome(Guid PreviewArtifactId, Guid GeneratedId, int ProbedMs, IReadOnlyList<string> Args);

    private static string TextForAttempt(
        TimingPlan plan,
        string primary,
        IReadOnlyList<string> candidateTexts,
        IReadOnlyList<string> rewriteTexts)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(primary);
        ArgumentNullException.ThrowIfNull(candidateTexts);
        ArgumentNullException.ThrowIfNull(rewriteTexts);

        var attempt = plan.Attempts[plan.ChosenIndex];
        if (string.Equals(attempt.Kind, KindAlternate, StringComparison.Ordinal))
        {
            var ordinal = 0;
            for (var i = 0; i < plan.ChosenIndex; i++)
            {
                if (string.Equals(plan.Attempts[i].Kind, KindAlternate, StringComparison.Ordinal))
                {
                    ordinal++;
                }
            }

            if (ordinal < candidateTexts.Count)
            {
                return candidateTexts[ordinal];
            }
        }

        if (string.Equals(attempt.Kind, KindRewrite, StringComparison.Ordinal))
        {
            var ordinal = 0;
            for (var i = 0; i < plan.ChosenIndex; i++)
            {
                if (string.Equals(plan.Attempts[i].Kind, KindRewrite, StringComparison.Ordinal))
                {
                    ordinal++;
                }
            }

            if (ordinal < rewriteTexts.Count)
            {
                return rewriteTexts[ordinal];
            }
        }

        return primary;
    }

    private async Task<int> ProbeDurationAsync(string storageKeyOrPath, CancellationToken cancellationToken)
    {
        FfprobeResult probe;
        try
        {
            probe = await _ffprobe.ProbeAsync(storageKeyOrPath, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (LeaseLostException)
        {
            throw;
        }
#pragma warning disable CA1031 // Undecodable bytes are a provider-invalid-response signal, not a crash: review decides.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            throw new ErrorCodeException(ErrorCodes.ProviderInvalidResponse, "Generated audio is undecodable; timing cannot proceed.", ex);
        }

        if (probe.DurationMs <= 0)
        {
            throw new ErrorCodeException(ErrorCodes.ProviderInvalidResponse, "Generated audio has zero duration; timing cannot proceed.");
        }

        if (probe.Streams is null
            || probe.Streams.Count == 0
            || !probe.Streams.Any(s => string.Equals(s.CodecType?.Trim(), "audio", StringComparison.OrdinalIgnoreCase)))
        {
            throw new ErrorCodeException(ErrorCodes.ProviderInvalidResponse, "Generated audio has no audio stream; timing cannot proceed.");
        }

        return (int)Math.Min(probe.DurationMs, int.MaxValue);
    }

    private async Task<StretchOutcome?> RunStretchPreviewAsync(
        Guid tenantId,
        Guid projectId,
        Guid runId,
        SpeechSegment segment,
        StageExecution execution,
        string owner,
        string token,
        LoadedAudio finalAudio,
        string finalStorageKey,
        double factor,
        TimingPlan plan,
        Guid? chosenTranslationVersionId,
        int target,
        CancellationToken cancellationToken)
    {
        var cpuThreads = Math.Clamp(_media.CpuThreads, 1, 256);
        var tempo = ClampStretch(factor, _timing.MaxStretchFactor);
        var inputTemp = Path.Combine(Path.GetTempPath(), string.Concat("dubbing-timing-in-", Guid.NewGuid().ToString("N"), ".wav"));
        var outputTemp = Path.Combine(Path.GetTempPath(), string.Concat("dubbing-timing-out-", Guid.NewGuid().ToString("N"), ".wav"));
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await EnsureLeaseRunningAsync(tenantId, execution.Id, owner, token, cancellationToken).ConfigureAwait(false);

            using (var remote = await _storage.DownloadAsync(finalStorageKey, cancellationToken).ConfigureAwait(false))
            {
                using var local = File.Create(inputTemp);
                await remote.CopyToAsync(local, cancellationToken).ConfigureAwait(false);
            }

            var args = BuildStretchArgs(inputTemp, outputTemp, tempo, cpuThreads);
            FfmpegResult ffmpegResult;
            try
            {
                ffmpegResult = await _ffmpeg.RunAsync(args, Path.GetTempPath(), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (LeaseLostException)
            {
                throw;
            }
#pragma warning disable CA1031 // Stretch failure keeps the final audio; the budget policy decides review.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                _logger.LogWarning(
                    "Timing stretch failed for segment {SegmentId}: {Error}.",
                    segment.Id, ex.Message);
                return null;
            }

            int probedMs;
            try
            {
                probedMs = await ProbeDurationAsync(outputTemp, cancellationToken).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // Undecodable stretch output keeps the final audio; review decides.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                _logger.LogWarning(
                    "Timing stretch output undecodable for segment {SegmentId}: {Error}.",
                    segment.Id, ex.Message);
                return null;
            }

            stopwatch.Stop();
            await EnsureLeaseRunningAsync(tenantId, execution.Id, owner, token, cancellationToken).ConfigureAwait(false);

            var finalArtifactId = await LoadFinalArtifactIdAsync(tenantId, runId, segment.Id, cancellationToken).ConfigureAwait(false);
            var parents = new List<Guid>();
            if (finalArtifactId.HasValue && finalArtifactId.Value != Guid.Empty)
            {
                parents.Add(finalArtifactId.Value);
            }

            var score = ComputeSyncScore(probedMs - target, 0, 0, Math.Abs(tempo - 1.0), 0);
            var status = Classify(Math.Abs(probedMs - target), _timing.PreferredToleranceMs, _timing.MaxToleranceMs, false);
            var argsHash = DurationEstimator.ComputeHash(string.Join(" ", ffmpegResult.Args));
            var payload = JsonSerializer.Serialize(new
            {
                schemaVersion = SchemaVersion,
                runId = runId.ToString("N"),
                segmentId = segment.Id.ToString("N"),
                sequence = segment.Sequence,
                targetMs = target,
                actualMs = probedMs,
                durationErrMs = probedMs - target,
                syncScore = score,
                status = status.ToString(),
                rate = 1.0,
                stretchFactor = tempo,
                ffmpegArgs = ffmpegResult.Args,
                finalArtifactId = finalArtifactId?.ToString("N"),
                translationVersionId = chosenTranslationVersionId?.ToString("N"),
                attempt = execution.Attempt,
            }, JsonOptions);

            PublishResult published;
            var outputBytes = await File.ReadAllBytesAsync(outputTemp, cancellationToken).ConfigureAwait(false);
            using (var stream = new MemoryStream(outputBytes, writable: false))
            {
                published = await _artifacts.PublishAsync(
                    tenantId, projectId, runId,
                    StageType.TimingOptimization, ArtifactType.GeneratedAudioPreview,
                    stream, ".wav", "audio/wav",
                    "ffmpeg", StretchModel,
                    execution.ConfigurationHash, execution.ExecutionSnapshotHash,
                    parents, execution.Id, cancellationToken).ConfigureAwait(false);
            }

            using (TenantContext.BeginScope(tenantId))
            {
                using var db = _contextFactory.CreateDbContext();
                await db.Database.ExecuteSqlRawAsync(
                    "UPDATE artifacts SET metadata_json = {0} WHERE id = {1} AND tenant_id = {2}",
                    payload, published.ArtifactId, tenantId).ConfigureAwait(false);
            }

            var requestHash = ConfigurationHashCalculator.Compute(new
            {
                segmentId = segment.Id.ToString("N"),
                target,
                tempo = tempo.ToString("F3", CultureInfo.InvariantCulture),
                argsHash,
                attempt = execution.Attempt,
            });
            var responseHash = ConfigurationHashCalculator.Compute(new
            {
                duration = probedMs,
                score,
                artifact = published.ArtifactId.ToString("N"),
            });
            var scope = string.Concat("Segment:", segment.Id.ToString("N"), ":stretch:0");
            var idempotencyKey = ProviderExecutionRecorder.BuildIdempotencyKey(
                execution.ProcessingRunId, nameof(StageType.TimingOptimization), scope, execution.Attempt);
            var now = DateTimeOffset.UtcNow;
            await _recorder.RecordAsync(new ProviderExecution(
                Guid.NewGuid(), tenantId, projectId, runId,
                execution.Id, ProviderType.Mock, ProviderCapability.Tts, StretchModel,
                "1", null, null, null,
                execution.Attempt, requestHash, responseHash, Math.Max(0, stopwatch.ElapsedMilliseconds),
                null, null, probedMs / 1000.0, null, null, null,
                OutcomeClass.Success, "timing-stretch",
                StretchTemplateId, argsHash, null, null, idempotencyKey, now), cancellationToken).ConfigureAwait(false);

            var generatedId = Guid.NewGuid();
            using (TenantContext.BeginScope(tenantId))
            {
                using var db = _contextFactory.CreateDbContext();
                db.Set<GeneratedAudioArtifact>().Add(new GeneratedAudioArtifact(
                    generatedId, tenantId, projectId, runId, segment.Id,
                    "ffmpeg", StretchModel, finalAudio.VoiceProfileId,
                    published.ContentObjectId, probedMs, true,
                    execution.Attempt, now));
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }

            _ = plan;
            return new StretchOutcome(published.ArtifactId, generatedId, probedMs, ffmpegResult.Args);
        }
        finally
        {
            try
            {
                if (File.Exists(inputTemp))
                {
                    File.Delete(inputTemp);
                }
            }
            catch (Exception)
            {
                // Best effort; OS temp cleaners cover leftovers.
            }

            try
            {
                if (File.Exists(outputTemp))
                {
                    File.Delete(outputTemp);
                }
            }
            catch (Exception)
            {
                // Best effort; OS temp cleaners cover leftovers.
            }
        }
    }

    private async Task<Guid> PersistSyncResultAsync(
        Guid tenantId,
        Guid projectId,
        Guid runId,
        SpeechSegment segment,
        int targetMs,
        int actualMs,
        double rate,
        double stretchFactor,
        double score100,
        SyncStatus status,
        CancellationToken cancellationToken)
    {
        var row = BuildSyncResult(
            tenantId, projectId, runId, segment.Id,
            targetMs, actualMs, rate, stretchFactor, score100, status);
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            var existing = await db.Set<SyncResult>()
                .FirstOrDefaultAsync(s => s.RunId == runId && s.SegmentId == segment.Id, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                db.Set<SyncResult>().Remove(existing);
            }

            db.Set<SyncResult>().Add(row);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        return row.Id;
    }

    private async Task<Guid> PersistAlternateSelectionAsync(
        Guid tenantId,
        Guid projectId,
        Guid runId,
        SpeechSegment segment,
        LoadedTranslation translation,
        string chosenText,
        CancellationToken cancellationToken)
    {
        var alternatives = translation.AlternativeTexts
            .Where(t => !string.IsNullOrWhiteSpace(t) && !string.Equals(t.Trim(), chosenText.Trim(), StringComparison.Ordinal))
            .Select(t => t.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (!string.IsNullOrWhiteSpace(translation.PrimaryText)
            && !string.Equals(translation.PrimaryText.Trim(), chosenText.Trim(), StringComparison.Ordinal))
        {
            alternatives.Insert(0, translation.PrimaryText.Trim());
        }

        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            var prior = await db.Set<TranslationVersion>()
                .FirstOrDefaultAsync(v => v.Id == translation.VersionId, cancellationToken).ConfigureAwait(false);
            var versionId = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;
            using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var existing = await db.Set<TranslationVersion>()
                    .Where(v => v.SegmentId == segment.Id)
                    .ToListAsync(cancellationToken).ConfigureAwait(false);
                foreach (var row in existing)
                {
                    if (row.IsSelected)
                    {
                        row.SetSelected(false);
                    }
                }

                var template = prior?.PromptTemplateId ?? GuidUtility.From(TranslationService.PromptTemplateIdText);
                db.Set<TranslationVersion>().Add(new TranslationVersion(
                    versionId, tenantId, projectId, runId, segment.Id,
                    chosenText.Trim(), alternatives.ToArray(),
                    prior?.SemanticScore ?? 0.8, prior?.NaturalnessScore ?? 0.8, prior?.TimingScore ?? 0.8,
                    prior?.Provider ?? "Mock", prior?.Model ?? "mock-1",
                    template, prior?.PromptHash, true, now));
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
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

            return versionId;
        }
    }

    private async Task<Guid> CreateReviewAsync(
        Guid tenantId,
        Guid projectId,
        Guid runId,
        SpeechSegment segment,
        int targetMs,
        int actualMs,
        double score100,
        SyncStatus status,
        int attemptCount,
        CancellationToken cancellationToken)
    {
        var payload = BuildReviewPayload(segment.Id, segment.Sequence, targetMs, actualMs, score100, status, attemptCount);
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

        return reviewId;
    }

    private async Task<TimingOptimizationResult?> TryReturnIdempotentAsync(
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
            var sync = await db.Set<SyncResult>()
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.RunId == execution.ProcessingRunId && s.SegmentId == segmentId, cancellationToken).ConfigureAwait(false);
            if (sync is null)
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

            return new TimingOptimizationResult(
                segmentId, sync.Id, sync.SyncScore, sync.Status,
                sync.TargetWindowMs, sync.ActualDurationMs, sync.RateDelta, sync.StretchFactor,
                null, artifactId, [],
                execution.Status == StageStatus.ManualReviewRequired, reviewId);
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
                throw new ErrorCodeException(
                    ErrorCodes.PipelineInvariantViolation,
                    $"Segment '{segmentId:D}' has no translation; Translation must complete before TimingOptimization.");
            }

            if (winner.TenantId != tenantId || winner.ProjectId != projectId || winner.RunId != runId)
            {
                throw new ForbiddenException($"Translation for segment '{segmentId:D}' does not belong to the current tenant/project/run.");
            }

            return new LoadedTranslation(winner.Id, winner.PrimaryText, winner.AlternativeTexts ?? []);
        }
    }

    private async Task<LoadedAudio> LoadFinalAudioAsync(
        Guid tenantId, Guid projectId, Guid runId, Guid segmentId, CancellationToken cancellationToken)
    {
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            var row = await db.Set<GeneratedAudioArtifact>()
                .AsNoTracking()
                .Where(g => g.RunId == runId && g.SegmentId == segmentId && !g.IsPreview)
                .OrderByDescending(g => g.CreatedAt)
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (row is null)
            {
                throw new ErrorCodeException(
                    ErrorCodes.PipelineInvariantViolation,
                    $"Segment '{segmentId:D}' has no final audio; VoiceGeneration must complete before TimingOptimization.");
            }

            if (row.TenantId != tenantId || row.ProjectId != projectId)
            {
                throw new ForbiddenException($"Generated audio for segment '{segmentId:D}' does not belong to the current tenant/project.");
            }

            return new LoadedAudio(row.Id, row.ContentObjectId, row.VoiceProfileId, row.DurationMs, row.Provider, row.Model);
        }
    }

    private async Task<Guid?> LoadFinalArtifactIdAsync(
        Guid tenantId, Guid runId, Guid segmentId, CancellationToken cancellationToken)
    {
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            var rows = await db.Set<Artifact>()
                .AsNoTracking()
                .Where(a => a.ProcessingRunId == runId && a.Type == ArtifactType.GeneratedAudioFinal)
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

    private async Task<string> LoadStorageKeyAsync(Guid tenantId, Guid contentObjectId, CancellationToken cancellationToken)
    {
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            var content = await db.Set<ContentObject>()
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == contentObjectId, cancellationToken).ConfigureAwait(false);
            if (content is null || string.IsNullOrWhiteSpace(content.StorageKey))
            {
                throw new ErrorCodeException(ErrorCodes.ArtifactUnavailable, "Generated audio content is unavailable for timing measurement.");
            }

            return content.StorageKey;
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

            if (execution.StageType != StageType.TimingOptimization)
            {
                throw new DomainException($"Stage execution '{executionId}' is '{execution.StageType}', not TimingOptimization.");
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
