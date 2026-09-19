using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
/// Outcome of one segment translation. <see cref="NeedsReview"/> is true only
/// for the persistent-low-quality terminal path (review item created,
/// execution moved to <c>ManualReviewRequired</c>); otherwise the winner is
/// completed. <see cref="UsedContext"/> is false when no window assignment
/// existed (translated without context plus a warning, never failed).
/// </summary>
public sealed record TranslationSegmentResult(
    Guid SegmentId,
    Guid SelectedVersionId,
    Guid TranslationArtifactId,
    double SemanticScore,
    string Provider,
    string Model,
    bool NeedsReview,
    Guid? ReviewItemId,
    bool UsedContext,
    Guid? ContextWindowId);

/// <summary>
/// One scored candidate (primary or alternative, initial or fallback).
/// <see cref="Score"/> is the deterministic weighted blend;
/// <see cref="IsFallback"/> marks fallback-provider candidates.
/// </summary>
public sealed record TranslationCandidate(
    string Text,
    double Semantic,
    double Naturalness,
    double Timing,
    double Score,
    string Provider,
    string Model,
    bool IsFallback,
    int Index);

/// <summary>
/// Translation meters. Counter names are frozen: renaming breaks dashboards.
/// </summary>
public static class TranslationMeters
{
    public const string MeterName = "DubbingPlatform.Translation";

    public const string BudgetExceededMetricName = "translation.budget_exceeded_total";

    public const string ReviewMetricName = "translation.review_total";

    private static readonly Meter Meter = new(MeterName);

    public static readonly Counter<long> BudgetExceeded =
        Meter.CreateCounter<long>(BudgetExceededMetricName);

    public static readonly Counter<long> Reviews =
        Meter.CreateCounter<long>(ReviewMetricName);
}

/// <summary>
/// Segment-scoped context-aware translation with versioned candidates,
/// deterministic scoring, glossary enforcement, bounded budgets, and review
/// routing. Flow per call: load the selected transcript (empty source fails
/// fast with <c>VALIDATION_FAILED</c>), load the assigned window when present
/// (missing assignment translates without context plus a warning, never fails),
/// check the capability cost gate (<c>QUOTA_EXCEEDED</c> with no provider call
/// when blocked), resolve the provider (capability <c>Translation</c>, Mock
/// adapter only), call the provider once (primary plus alternatives arrive in
/// one call where supported; the result list is truncated to
/// <c>Translation:MaxCandidates</c>), score each candidate deterministically as
/// <c>0.5*semantic + 0.3*naturalness + 0.2*timing</c> (provider scores when
/// finite, else <c>0.8</c> default; timing from the length-ratio table below),
/// prefer glossary-compliant candidates when any exist, select the max by the
/// deterministic tie-break, persist one <c>Translation</c> artifact (schema
/// <c>v1</c>, parents are the transcript and context artifacts when present)
/// plus one <c>TranslationVersion</c> (primary is the winner, alternatives are
/// the rest, previous versions unselected in the same transaction), record a
/// <c>ProviderExecution</c> per call, and complete the execution lease-fenced.
/// Budgets (<c>MaxCandidates</c>/<c>MaxTokens</c>/<c>MaxCost</c>/
/// <c>MaxWallClockSec</c>) stop the loop and select best-so-far (or review
/// when no candidate exists); wall-clock uses a linked
/// <c>CancellationTokenSource</c> so caller cancellation still propagates as
/// cancellation. Quality below <c>Translation:QualityThreshold</c> tries one
/// fallback provider call when a compatible invocable Mock descriptor exists,
/// then routes to review (<c>TRANSLATION_QUALITY</c>, <c>Open</c>,
/// execution <c>ManualReviewRequired</c>); the run never fails for one weak
/// segment. Provider rate-limit codes propagate for delayed saga retry;
/// transport errors propagate for transport retry; permanent failures fail
/// lease-fenced. Terminal executions return their stored outcome (idempotent
/// redelivery safe). Never logs source, context, or translation text: only
/// ids, counts, scores, and hashes.
/// </summary>
public sealed class TranslationService
{
    /// <summary>Review reason for persistent low translation quality.</summary>
    public const string ReviewReason = "TRANSLATION_QUALITY";

    /// <summary>Artifact/row schema version for every translation payload.</summary>
    public const string SchemaVersion = "1";

    /// <summary>Deterministic prompt template identity (no secrets in the id).</summary>
    public const string PromptTemplateIdText = "translation-segment-v1";

    /// <summary>Deterministic prompt template version.</summary>
    public const string PromptTemplateVersion = "1";

    /// <summary>Speaker display used when a segment has no mapped speaker.</summary>
    public const string UnknownSpeaker = "unknown";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IStageExecutionContextFactory _contextFactory;
    private readonly ArtifactService _artifacts;
    private readonly ITranslationProvider _translation;
    private readonly ProviderResolver _resolver;
    private readonly IDescriptorStore _descriptors;
    private readonly ProviderExecutionRecorder _recorder;
    private readonly IProviderCostGate _costGate;
    private readonly CostService? _costs;
    private readonly TranslationOptions _options;
    private readonly ProviderOptions _providers;
    private readonly RetryOptions _retry;
    private readonly ILogger<TranslationService> _logger;

    public TranslationService(
        IStageExecutionContextFactory contextFactory,
        ArtifactService artifacts,
        ITranslationProvider translation,
        ProviderResolver resolver,
        IDescriptorStore descriptors,
        ProviderExecutionRecorder recorder,
        IProviderCostGate costGate,
        IOptions<TranslationOptions> translationOptions,
        IOptions<ProviderOptions> providerOptions,
        IOptions<RetryOptions> retryOptions,
        ILogger<TranslationService> logger,
        CostService? costs = null)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        ArgumentNullException.ThrowIfNull(artifacts);
        ArgumentNullException.ThrowIfNull(translation);
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(descriptors);
        ArgumentNullException.ThrowIfNull(recorder);
        ArgumentNullException.ThrowIfNull(costGate);
        ArgumentNullException.ThrowIfNull(translationOptions);
        ArgumentNullException.ThrowIfNull(providerOptions);
        ArgumentNullException.ThrowIfNull(retryOptions);
        ArgumentNullException.ThrowIfNull(logger);
        _contextFactory = contextFactory;
        _artifacts = artifacts;
        _translation = translation;
        _resolver = resolver;
        _descriptors = descriptors;
        _recorder = recorder;
        _costGate = costGate;
        _costs = costs;
        _options = translationOptions.Value;
        _providers = providerOptions.Value;
        _retry = retryOptions.Value;
        _logger = logger;
    }

    /// <summary>
    /// Ideal target/source character ratio per language pair for timing.
    /// Table (case-insensitive, trimmed, <c>source→target</c>):
    /// <c>en→es = 1.0</c>, <c>en→de = 1.1</c>, default (any other pair,
    /// empty, or unknown) = <c>1.0</c>. Rationale: Spanish tracks English
    /// length closely for dubbing timing, while German averages ~10% longer;
    /// unknown pairs assume parity so timing never rewards truncation.
    /// </summary>
    public static double IdealRatio(string? sourceLanguage, string? targetLanguage)
    {
        var source = (sourceLanguage ?? string.Empty).Trim().ToLowerInvariant();
        var target = (targetLanguage ?? string.Empty).Trim().ToLowerInvariant();
        if (string.Equals(source, "en", StringComparison.Ordinal) && string.Equals(target, "de", StringComparison.Ordinal))
        {
            return 1.1;
        }

        return 1.0;
    }

    /// <summary>
    /// Timing sub-score in 0..1: <c>1 - min(1, abs(adjusted-1))</c> where
    /// <c>adjusted = (targetChars/sourceChars) / ideal</c> and
    /// <c>ideal</c> comes from <see cref="IdealRatio"/>. Pure. Zero or
    /// negative source length yields 0 (no division by zero).
    /// </summary>
    public static double ComputeTimingScore(int sourceChars, int targetChars, string? sourceLanguage, string? targetLanguage)
    {
        if (sourceChars <= 0 || targetChars < 0)
        {
            return 0.0;
        }

        var ideal = IdealRatio(sourceLanguage, targetLanguage);
        if (ideal <= 0.0 || double.IsNaN(ideal))
        {
            ideal = 1.0;
        }

        var actual = (double)targetChars / (double)sourceChars;
        var adjusted = actual / ideal;
        if (double.IsNaN(adjusted) || double.IsInfinity(adjusted))
        {
            return 0.0;
        }

        return Math.Clamp(1.0 - Math.Min(1.0, Math.Abs(adjusted - 1.0)), 0.0, 1.0);
    }

    /// <summary>
    /// Weighted blend <c>0.5*semantic + 0.3*naturalness + 0.2*timing</c>.
    /// NaN semantic/naturalness fall back to <c>0.8</c> (provider omitted the
    /// sub-score); finite values clamp to 0..1. Pure.
    /// </summary>
    public static double ScoreCandidate(double semantic, double naturalness, double timing)
    {
        var semanticFixed = double.IsNaN(semantic) ? 0.8 : Math.Clamp(semantic, 0.0, 1.0);
        var naturalnessFixed = double.IsNaN(naturalness) ? 0.8 : Math.Clamp(naturalness, 0.0, 1.0);
        var timingFixed = double.IsNaN(timing) ? 0.0 : Math.Clamp(timing, 0.0, 1.0);
        return Math.Clamp(
            (0.5 * semanticFixed) + (0.3 * naturalnessFixed) + (0.2 * timingFixed),
            0.0, 1.0);
    }

    /// <summary>
    /// SHA-256 hex (lowercase) of the UTF-8 bytes. Pure; same input always
    /// yields the same hash on any machine. Never receives secrets (prompt
    /// text plus tenant content only).
    /// </summary>
    public static string ComputeHash(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Glossary compliance: for every term whose source side appears in
    /// <paramref name="sourceText"/> (case-insensitive), the candidate must
    /// contain the target side (case-insensitive). Empty glossary is compliant.
    /// Pure.
    /// </summary>
    public static bool IsGlossaryCompliant(
        string sourceText,
        string candidateText,
        IReadOnlyDictionary<string, string> glossary)
    {
        ArgumentNullException.ThrowIfNull(sourceText);
        ArgumentNullException.ThrowIfNull(candidateText);
        ArgumentNullException.ThrowIfNull(glossary);
        foreach (var pair in glossary)
        {
            if (string.IsNullOrWhiteSpace(pair.Key) || string.IsNullOrWhiteSpace(pair.Value))
            {
                continue;
            }

            if (sourceText.Contains(pair.Key, StringComparison.OrdinalIgnoreCase)
                && !candidateText.Contains(pair.Value, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Deterministic winner selection. Rule: when at least one candidate is
    /// glossary-compliant, only compliant candidates are considered (a
    /// compliant alternative is preferred over a higher-scoring violating
    /// primary, so glossary wins over raw score); otherwise all candidates
    /// are considered. Ordering within the considered set is score desc,
    /// semantic desc, naturalness desc, provider ordinal, model ordinal, text
    /// ordinal, index asc. Pure; returns null for empty input.
    /// </summary>
    public static TranslationCandidate? SelectBest(
        IReadOnlyList<TranslationCandidate> candidates,
        string sourceText,
        IReadOnlyDictionary<string, string> glossary)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(sourceText);
        ArgumentNullException.ThrowIfNull(glossary);
        if (candidates.Count == 0)
        {
            return null;
        }

        var considered = candidates;
        if (glossary.Count > 0)
        {
            var compliant = candidates
                .Where(c => c is not null && IsGlossaryCompliant(sourceText, c.Text, glossary))
                .ToList();
            if (compliant.Count > 0)
            {
                considered = compliant;
            }
        }

        TranslationCandidate? best = null;
        foreach (var candidate in considered)
        {
            ArgumentNullException.ThrowIfNull(candidate);
            if (best is null || Compare(candidate, best) < 0)
            {
                best = candidate;
            }
        }

        return best;
    }

    private static int Compare(TranslationCandidate left, TranslationCandidate right)
    {
        var score = right.Score.CompareTo(left.Score);
        if (score != 0)
        {
            return score;
        }

        var semantic = right.Semantic.CompareTo(left.Semantic);
        if (semantic != 0)
        {
            return semantic;
        }

        var naturalness = right.Naturalness.CompareTo(left.Naturalness);
        if (naturalness != 0)
        {
            return naturalness;
        }

        var provider = string.Compare(left.Provider, right.Provider, StringComparison.Ordinal);
        if (provider != 0)
        {
            return provider;
        }

        var model = string.Compare(left.Model, right.Model, StringComparison.Ordinal);
        if (model != 0)
        {
            return model;
        }

        var text = string.Compare(left.Text, right.Text, StringComparison.Ordinal);
        if (text != 0)
        {
            return text;
        }

        return left.Index.CompareTo(right.Index);
    }

    /// <summary>
    /// Builds the deterministic translation prompt (template plus tenant
    /// content only; never credentials). Pure.
    /// </summary>
    public static string BuildPrompt(
        string sourceText,
        string? contextText,
        IReadOnlyDictionary<string, string> glossary,
        string style,
        string speaker,
        string sourceLanguage,
        string targetLanguage,
        int durationMs)
    {
        ArgumentNullException.ThrowIfNull(sourceText);
        ArgumentNullException.ThrowIfNull(glossary);
        var builder = new StringBuilder();
        builder.Append("Translate from ").Append(sourceLanguage.Trim()).Append(" to ").Append(targetLanguage.Trim()).Append('.');
        builder.Append("\n# speaker: ").Append(string.IsNullOrWhiteSpace(speaker) ? UnknownSpeaker : speaker.Trim());
        builder.Append("\n# durationMs: ").Append(durationMs.ToString(CultureInfo.InvariantCulture));
        if (!string.IsNullOrWhiteSpace(style))
        {
            builder.Append("\n# style: ").Append(style.Trim());
        }

        if (glossary.Count > 0)
        {
            builder.Append("\n# glossary:");
            foreach (var pair in glossary.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                builder.Append("\n# - ").Append(pair.Key).Append(" => ").Append(pair.Value);
            }
        }

        builder.Append("\n# context: ").Append(string.IsNullOrWhiteSpace(contextText) ? "none" : contextText);
        builder.Append("\n# source: ").Append(sourceText);
        return builder.ToString();
    }

    /// <summary>
    /// Translates one segment end to end (provider calls, persistence,
    /// selection, quality policy, stage commit). See class docs for the policy
    /// matrix. Terminal executions return their stored outcome (idempotent
    /// redelivery safe).
    /// </summary>
    public async Task<TranslationSegmentResult> TranslateSegmentAsync(
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
        var transcript = await LoadSelectedTranscriptAsync(tenantId, projectId, runId, segmentId, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(transcript.Text))
        {
            throw new ErrorCodeException(ErrorCodes.ValidationFailed, $"Segment '{segmentId:D}' has empty source text; translation cannot proceed.");
        }

        var sourceText = transcript.Text;
        var context = await LoadContextAsync(tenantId, runId, segmentId, cancellationToken).ConfigureAwait(false);
        if (!context.UsedContext)
        {
            _logger.LogWarning(
                "Translation for segment {SegmentId} proceeds without context (no window assignment).",
                segmentId);
        }

        var settings = ContextBuilderService.ParseSettings(project.SettingsJson);
        var target = settings.TargetOverride ?? project.TargetLanguage;
        if (string.IsNullOrWhiteSpace(target))
        {
            throw new ErrorCodeException(ErrorCodes.ValidationFailed, $"Project '{projectId:D}' has no target language for translation.");
        }

        target = target.Trim();
        var speaker = await LoadSpeakerDisplayAsync(tenantId, projectId, segment.SpeakerId, cancellationToken).ConfigureAwait(false);
        var glossary = (IReadOnlyDictionary<string, string>)settings.Glossary;

        if (!await _costGate.CanProceedAsync(tenantId, ProviderCapability.Translation, cancellationToken).ConfigureAwait(false))
        {
            throw new ErrorCodeException(ErrorCodes.QuotaExceeded, "Cost guard blocks translation for the current tenant.");
        }

        // Task 036 per-segment preflight: atomic hold before the paid call
        // (QUOTA_EXCEEDED when the project cap would be exceeded). Null when
        // CostService is not wired (existing tests); the capability gate above
        // still applies. Reconciled to provider-reported actuals after invoke.
        CostReservation? costHold = null;
        var costEstimate = 0.0;
        if (_costs is not null)
        {
            costEstimate = CostService.Estimate(
                ProviderCapability.Translation,
                new CostUsageDims(0.0, sourceText.Length, 0));
            costHold = await _costs.ReserveAsync(
                tenantId, projectId, runId, segmentId,
                ProviderCapability.Translation, costEstimate, cancellationToken).ConfigureAwait(false);
        }

        var threshold = Clamp01(_options.QualityThreshold);
        var maxCandidates = Math.Clamp(_options.MaxCandidates, 1, 10);
        var maxTokens = Math.Clamp(_options.MaxTokens, 1, 100000);
        var maxCost = double.IsNaN(_options.MaxCost) ? 2.0 : Math.Max(0.0, _options.MaxCost);
        var wallClockSec = Math.Clamp(_options.MaxWallClockSec, 1, 600);
        var inputBytes = (long)Encoding.UTF8.GetByteCount(sourceText);

        ProviderType provider;
        string model;
        try
        {
            (provider, model) = await _resolver.ResolveAsync(
                ProviderCapability.Translation,
                tenantId,
                project.SourceLanguage,
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
#pragma warning disable CA1031 // Resolver failures are permanent provider-configuration failures with no translation to persist.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            throw new ErrorCodeException(ErrorCodes.ProviderConfigurationError, $"No translation route is available: {Truncate(ClassifyMessage(ex))}.");
        }

        if (provider != ProviderType.Mock)
        {
            throw new ErrorCodeException(
                ErrorCodes.ProviderConfigurationError,
                string.Concat("No translation adapter for provider '", provider.ToString(), "'."));
        }

        var promptText = BuildPrompt(
            sourceText, context.ContextText, glossary, settings.Style,
            speaker, project.SourceLanguage, target, segment.DurationMs);
        var promptHash = ComputeHash(promptText);
        var requestHash = ConfigurationHashCalculator.Compute(new
        {
            segmentId = segmentId.ToString("N"),
            source = project.SourceLanguage,
            target,
            speaker,
            style = settings.Style,
            glossary = glossary.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => new { term = p.Key, translation = p.Value }),
            durationMs = segment.DurationMs,
            promptHash,
            templateId = PromptTemplateIdText,
            templateVersion = PromptTemplateVersion,
            contextWindowId = context.WindowId?.ToString("N"),
        });

        using var wallClock = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        wallClock.CancelAfter(TimeSpan.FromSeconds(wallClockSec));
        var budget = wallClock.Token;

        var collected = new List<TranslationCandidate>();
        var tokensUsed = 0;
        var costUsed = 0.0;
        var budgetExceeded = false;

        TranslationCallResult? primary = null;
        try
        {
            primary = await InvokeOnceAsync(
                execution, provider, model, sourceText, target, project.SourceLanguage,
                segment, requestHash, promptHash, isFallback: false, budget, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (wallClock.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            budgetExceeded = true;
            TranslationMeters.BudgetExceeded.Add(1);
            _logger.LogWarning(
                "Translation wall-clock budget ({Seconds}s) exceeded for segment {SegmentId}; no candidate collected.",
                wallClockSec, segmentId);
            await ReleaseCostHoldAsync(costHold, tenantId, cancellationToken).ConfigureAwait(false);
            throw new ErrorCodeException(ErrorCodes.ProviderTimeout, $"Translation wall-clock budget ({wallClockSec}s) exceeded for segment '{segmentId:D}'.", ex);
        }
#pragma warning disable CA1031 // Release is best effort; the provider error propagates for worker routing.
        catch (Exception)
        {
            await ReleaseCostHoldAsync(costHold, tenantId, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
#pragma warning restore CA1031

        if (primary is not null)
        {
            await ReconcileCostHoldAsync(costHold, tenantId, primary.Response.Usage?.EstimatedCostUsd, costEstimate).ConfigureAwait(false);
            costHold = null;
            var primaryTokens = TokensOf(primary.Response, sourceText);
            var primaryCost = CostOf(primary.Response);
            tokensUsed += primaryTokens;
            costUsed += primaryCost;
            if (tokensUsed > maxTokens || costUsed > maxCost)
            {
                budgetExceeded = true;
                TranslationMeters.BudgetExceeded.Add(1);
                _logger.LogWarning(
                    "Translation budget exceeded for segment {SegmentId} after primary call ({Tokens} tokens, {Cost} USD). Selecting best-so-far.",
                    segmentId, tokensUsed, costUsed);
            }

            collected.AddRange(BuildCandidates(
                primary, sourceText, project.SourceLanguage, target, maxCandidates, isFallback: false));
            if (collected.Count > maxCandidates)
            {
                budgetExceeded = true;
                TranslationMeters.BudgetExceeded.Add(1);
                _logger.LogWarning(
                    "Translation candidate budget exceeded for segment {SegmentId} ({Count} > {Max}); truncating.",
                    segmentId, collected.Count, maxCandidates);
                collected = collected.Take(maxCandidates).ToList();
            }
        }

        var best = SelectBest(collected, sourceText, glossary);
        var fallbackUsed = false;

        if (best is not null && best.Semantic < threshold && !budgetExceeded)
        {
            var fallback = await TryFallbackOnceAsync(
                execution, project.SourceLanguage, sourceText, target, segment,
                requestHash, promptHash, inputBytes, provider, model,
                budget, cancellationToken).ConfigureAwait(false);
            if (fallback is not null)
            {
                fallbackUsed = true;
                var fallbackTokens = TokensOf(fallback.Response, sourceText);
                var fallbackCost = CostOf(fallback.Response);
                if (tokensUsed + fallbackTokens > maxTokens || costUsed + fallbackCost > maxCost)
                {
                    budgetExceeded = true;
                    TranslationMeters.BudgetExceeded.Add(1);
                    _logger.LogWarning(
                        "Translation budget exceeded for segment {SegmentId} after fallback call; keeping primary best-so-far.",
                        segmentId);
                }
                else
                {
                    tokensUsed += fallbackTokens;
                    costUsed += fallbackCost;
                    var fallbackCandidates = BuildCandidates(
                        fallback, sourceText, project.SourceLanguage, target, maxCandidates, isFallback: true);
                    foreach (var candidate in fallbackCandidates)
                    {
                        if (collected.Count >= maxCandidates)
                        {
                            break;
                        }

                        collected.Add(candidate);
                    }

                    best = SelectBest(collected, sourceText, glossary);
                }
            }
        }

        if (best is null)
        {
            var reviewId = await CreateReviewAsync(
                tenantId, projectId, runId, segment, null,
                0.0, threshold, context.WindowId, budgetExceeded,
                cancellationToken).ConfigureAwait(false);
            await EnsureLeaseRunningAsync(tenantId, executionId, owner, token, cancellationToken).ConfigureAwait(false);
            var reviewStages = new StageExecutionService(_contextFactory, Microsoft.Extensions.Options.Options.Create(_retry));
            await reviewStages.MarkReviewRequiredAsync(tenantId, executionId, owner, token, cancellationToken).ConfigureAwait(false);
            TranslationMeters.Reviews.Add(1);
            _logger.LogInformation(
                "Translation for segment {SegmentId} routed to review {ReviewId} (no candidate).",
                segmentId, reviewId);
            return new TranslationSegmentResult(
                segmentId, Guid.Empty, Guid.Empty, 0.0,
                provider.ToString(), model, true, reviewId,
                context.UsedContext, context.WindowId);
        }

        if (best.Semantic < threshold)
        {
            await EnsureLeaseRunningAsync(tenantId, executionId, owner, token, cancellationToken).ConfigureAwait(false);
            var persisted = await PersistVersionAsync(
                tenantId, projectId, runId, segment, transcript, context,
                execution, best, collected, glossary, settings.Style, speaker,
                sourceText, target, project.SourceLanguage, promptHash,
                fallbackUsed, budgetExceeded, cancellationToken).ConfigureAwait(false);
            var review = await CreateReviewAsync(
                tenantId, projectId, runId, segment, persisted.VersionId,
                best.Semantic, threshold, context.WindowId, budgetExceeded,
                cancellationToken).ConfigureAwait(false);
            await EnsureLeaseRunningAsync(tenantId, executionId, owner, token, cancellationToken).ConfigureAwait(false);
            var reviewStages = new StageExecutionService(_contextFactory, Microsoft.Extensions.Options.Options.Create(_retry));
            await reviewStages.MarkReviewRequiredAsync(tenantId, executionId, owner, token, cancellationToken).ConfigureAwait(false);
            TranslationMeters.Reviews.Add(1);
            _logger.LogInformation(
                "Translation for segment {SegmentId} routed to review {ReviewId} (semantic {Semantic}).",
                segmentId, review, best.Semantic);
            return new TranslationSegmentResult(
                segmentId, persisted.VersionId, persisted.ArtifactId,
                best.Semantic, best.Provider, best.Model, true, review,
                context.UsedContext, context.WindowId);
        }

        await EnsureLeaseRunningAsync(tenantId, executionId, owner, token, cancellationToken).ConfigureAwait(false);
        var stored = await PersistVersionAsync(
            tenantId, projectId, runId, segment, transcript, context,
            execution, best, collected, glossary, settings.Style, speaker,
            sourceText, target, project.SourceLanguage, promptHash,
            fallbackUsed, budgetExceeded, cancellationToken).ConfigureAwait(false);

        await EnsureLeaseRunningAsync(tenantId, executionId, owner, token, cancellationToken).ConfigureAwait(false);
        var stages = new StageExecutionService(_contextFactory, Microsoft.Extensions.Options.Options.Create(_retry));
        await stages.CompleteAsync(
            tenantId, executionId, owner, token,
            [stored.ArtifactId.ToString("N")],
            cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Translated segment {SegmentId} for run {RunId}: version {VersionId} selected (semantic {Semantic}).",
            segmentId, runId, stored.VersionId, best.Semantic);
        return new TranslationSegmentResult(
            segmentId, stored.VersionId, stored.ArtifactId,
            best.Semantic, best.Provider, best.Model, false, null,
            context.UsedContext, context.WindowId);
    }

    private sealed record TranslationCallResult(
        ProviderType Provider,
        string Model,
        TranslationResponse Response,
        string ResponseHash,
        long LatencyMs,
        bool IsFallback);

    private sealed record ContextRef(
        bool UsedContext,
        Guid? WindowId,
        string? ContextText,
        Guid? ContextArtifactId);

    private sealed record LoadedTranscript(Guid VersionId, string Text, Guid? ArtifactId);

    private sealed record PersistedVersion(Guid VersionId, Guid ArtifactId);

    private List<TranslationCandidate> BuildCandidates(
        TranslationCallResult call,
        string sourceText,
        string sourceLanguage,
        string targetLanguage,
        int maxCandidates,
        bool isFallback)
    {
        var texts = new List<string> { call.Response.PrimaryText };
        if (call.Response.Alternatives is not null)
        {
            texts.AddRange(call.Response.Alternatives.Where(t => !string.IsNullOrWhiteSpace(t)));
        }

        var semantic = double.IsNaN(call.Response.SemanticScore) ? 0.8 : Math.Clamp(call.Response.SemanticScore, 0.0, 1.0);
        var naturalness = double.IsNaN(call.Response.NaturalnessScore) ? 0.8 : Math.Clamp(call.Response.NaturalnessScore, 0.0, 1.0);
        var candidates = new List<TranslationCandidate>(texts.Count);
        for (var i = 0; i < texts.Count && candidates.Count < maxCandidates; i++)
        {
            var text = texts[i].Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            var timing = ComputeTimingScore(sourceText.Length, text.Length, sourceLanguage, targetLanguage);
            var score = ScoreCandidate(semantic, naturalness, timing);
            candidates.Add(new TranslationCandidate(
                text, semantic, naturalness, timing, score,
                call.Provider.ToString(), call.Model, isFallback, i));
        }

        return candidates;
    }

    private async Task<TranslationCallResult> InvokeOnceAsync(
        StageExecution execution,
        ProviderType provider,
        string model,
        string sourceText,
        string targetLanguage,
        string sourceLanguage,
        SpeechSegment segment,
        string requestHash,
        string promptHash,
        bool isFallback,
        CancellationToken budget,
        CancellationToken caller)
    {
        var request = new TranslationRequest(
            execution.TenantId, execution.ProjectId, execution.ProcessingRunId,
            segment.Id.ToString("N"),
            sourceLanguage,
            targetLanguage,
            Encoding.UTF8.GetByteCount(sourceText),
            segment.DurationMs);

        var stopwatch = Stopwatch.StartNew();
        TranslationResponse response;
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(budget, caller);
            response = await _translation.TranslateAsync(request, linked.Token).ConfigureAwait(false);
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
                execution, provider, model, null, promptHash,
                outcome, requestHash, null, stopwatch.ElapsedMilliseconds,
                isFallback, CancellationToken.None).ConfigureAwait(false);

            if (ex is ErrorCodeException)
            {
                throw;
            }

            throw new ErrorCodeException(ErrorCodes.ProviderFailed, $"Translation provider failed for segment '{segment.Id:D}': {Truncate(ClassifyMessage(ex))}.", ex);
        }

        var validationError = ValidateResponse(response);
        if (validationError is not null)
        {
            var invalidHash = ConfigurationHashCalculator.Compute(new
            {
                primary = response.PrimaryText,
                semantic = response.SemanticScore,
                model = response.Model,
            });
            await RecordExecutionAsync(
                execution, provider, model, response, promptHash,
                OutcomeClass.ProviderInvalidResponse, requestHash, invalidHash, stopwatch.ElapsedMilliseconds,
                isFallback, CancellationToken.None).ConfigureAwait(false);
            throw new ErrorCodeException(ErrorCodes.ProviderInvalidResponse, validationError);
        }

        var hash = ConfigurationHashCalculator.Compute(new
        {
            primary = response.PrimaryText,
            alternatives = response.Alternatives,
            semantic = response.SemanticScore,
            naturalness = response.NaturalnessScore,
            timing = response.TimingScore,
            model = response.Model,
        });
        await RecordExecutionAsync(
            execution, provider, model, response, promptHash,
            OutcomeClass.Success, requestHash, hash, stopwatch.ElapsedMilliseconds,
            isFallback, CancellationToken.None).ConfigureAwait(false);
        return new TranslationCallResult(provider, model, response, hash, stopwatch.ElapsedMilliseconds, isFallback);
    }

    private async Task<TranslationCallResult?> TryFallbackOnceAsync(
        StageExecution execution,
        string sourceLanguage,
        string sourceText,
        string targetLanguage,
        SpeechSegment segment,
        string requestHash,
        string promptHash,
        long inputBytes,
        ProviderType primary,
        string primaryModel,
        CancellationToken budget,
        CancellationToken caller)
    {
        ProviderCapabilityDescriptor? descriptor;
        try
        {
            descriptor = await FindFallbackDescriptorAsync(
                execution.TenantId, sourceLanguage, inputBytes, segment.DurationMs, budget).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Descriptor lookup failure means no fallback; the quality policy decides review.
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
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(budget, caller);
            linked.Token.ThrowIfCancellationRequested();
            var result = await InvokeOnceAsync(
                execution, descriptor.Provider, fallbackModel, sourceText, targetLanguage,
                sourceLanguage, segment, requestHash, promptHash,
                isFallback: true, linked.Token, caller).ConfigureAwait(false);
            _logger.LogInformation(
                "Translation fallback for segment {SegmentId} via {Provider} returned semantic {Semantic}.",
                segment.Id, descriptor.Provider.ToString(), result.Response.SemanticScore);
            return result;
        }
#pragma warning disable CA1031 // Fallback failure keeps the primary best-so-far; the quality policy decides review.
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not LeaseLostException)
#pragma warning restore CA1031
        {
            _logger.LogWarning(
                "Translation fallback failed for segment {SegmentId}: {Error}.",
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
        var candidates = await _descriptors.GetCandidatesAsync(ProviderCapability.Translation, tenantId, cancellationToken).ConfigureAwait(false);
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

            if (string.Equals(option.Capability?.Trim(), nameof(ProviderCapability.Translation), StringComparison.OrdinalIgnoreCase)
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

    private async Task<PersistedVersion> PersistVersionAsync(
        Guid tenantId,
        Guid projectId,
        Guid runId,
        SpeechSegment segment,
        LoadedTranscript transcript,
        ContextRef context,
        StageExecution execution,
        TranslationCandidate winner,
        IReadOnlyList<TranslationCandidate> collected,
        IReadOnlyDictionary<string, string> glossary,
        string style,
        string speaker,
        string sourceText,
        string targetLanguage,
        string sourceLanguage,
        string promptHash,
        bool fallbackUsed,
        bool budgetExceeded,
        CancellationToken cancellationToken)
    {
        var ordered = collected
            .OrderBy(c => Compare(c, winner) < 0 ? 0 : 1)
            .ThenBy(c => c.Index)
            .ToList();
        var alternatives = ordered
            .Where(c => !string.Equals(c.Text, winner.Text, StringComparison.Ordinal))
            .Select(c => c.Text)
            .Distinct(StringComparer.Ordinal)
            .Take(Math.Max(0, Math.Clamp(_options.MaxCandidates, 1, 10) - 1))
            .ToArray();

        var payload = JsonSerializer.Serialize(new
        {
            schemaVersion = SchemaVersion,
            runId = runId.ToString("N"),
            segmentId = segment.Id.ToString("N"),
            sequence = segment.Sequence,
            sourceLanguage,
            targetLanguage,
            sourceText,
            primary = winner.Text,
            alternatives,
            semanticScore = winner.Semantic,
            naturalnessScore = winner.Naturalness,
            timingScore = winner.Timing,
            combinedScore = winner.Score,
            provider = winner.Provider,
            model = winner.Model,
            promptTemplateId = PromptTemplateIdText,
            promptTemplateVersion = PromptTemplateVersion,
            promptHash,
            contextWindowId = context.WindowId?.ToString("N"),
            contextArtifactId = context.ContextArtifactId?.ToString("N"),
            transcriptArtifactId = transcript.ArtifactId?.ToString("N"),
            transcriptVersionId = transcript.VersionId.ToString("N"),
            speaker,
            durationMs = segment.DurationMs,
            style,
            glossary = glossary.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => new { term = p.Key, translation = p.Value }),
            usedContext = context.UsedContext,
            fallbackUsed,
            budgetExceeded,
        }, JsonOptions);

        var parents = new List<Guid>();
        if (transcript.ArtifactId.HasValue && transcript.ArtifactId.Value != Guid.Empty)
        {
            parents.Add(transcript.ArtifactId.Value);
        }

        if (context.ContextArtifactId.HasValue && context.ContextArtifactId.Value != Guid.Empty)
        {
            parents.Add(context.ContextArtifactId.Value);
        }

        PublishResult published;
        using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(payload), writable: false))
        {
            published = await _artifacts.PublishAsync(
                tenantId, projectId, runId,
                StageType.Translation, ArtifactType.Translation,
                stream, ".json", "application/json",
                winner.Provider, winner.Model,
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

        var templateGuid = GuidUtility.From(PromptTemplateIdText);
        var versionId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var existing = await db.Set<TranslationVersion>()
                    .Where(v => v.SegmentId == segment.Id)
                    .ToListAsync(cancellationToken).ConfigureAwait(false);
                foreach (var prior in existing)
                {
                    if (prior.IsSelected)
                    {
                        prior.SetSelected(false);
                    }
                }

                db.Set<TranslationVersion>().Add(new TranslationVersion(
                    versionId, tenantId, projectId, runId, segment.Id,
                    winner.Text, alternatives,
                    Clamp01(winner.Semantic), Clamp01(winner.Naturalness), Clamp01(winner.Timing),
                    winner.Provider, winner.Model,
                    templateGuid, promptHash, true, now));
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
        }

        return new PersistedVersion(versionId, published.ArtifactId);
    }

    private async Task<Guid> CreateReviewAsync(
        Guid tenantId,
        Guid projectId,
        Guid runId,
        SpeechSegment segment,
        Guid? versionId,
        double semantic,
        double threshold,
        Guid? windowId,
        bool budgetExceeded,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(new
        {
            reason = ReviewReason,
            segmentId = segment.Id.ToString("N"),
            sequence = segment.Sequence,
            versionId = versionId?.ToString("N"),
            semantic,
            threshold,
            contextWindowId = windowId?.ToString("N"),
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

        return reviewId;
    }

    private async Task<TranslationSegmentResult?> TryReturnIdempotentAsync(
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
            var versions = await db.Set<TranslationVersion>()
                .AsNoTracking()
                .Where(v => v.SegmentId == segmentId)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            var winner = versions.FirstOrDefault(v => v.IsSelected)
                ?? versions.OrderByDescending(v => v.CreatedAt).FirstOrDefault();
            if (winner is null)
            {
                return null;
            }

            var artifactId = await FindTranslationArtifactAsync(db, execution.ProcessingRunId, segmentId, cancellationToken).ConfigureAwait(false)
                ?? Guid.Empty;

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

            var assignment = await db.Set<SegmentContextAssignment>()
                .AsNoTracking()
                .FirstOrDefaultAsync(a => a.SegmentId == segmentId, cancellationToken).ConfigureAwait(false);

            return new TranslationSegmentResult(
                segmentId, winner.Id, artifactId,
                winner.SemanticScore, winner.Provider, winner.Model,
                execution.Status == StageStatus.ManualReviewRequired, reviewId,
                assignment is not null, assignment?.ContextWindowId);
        }
    }

    private async Task<Guid?> FindTranslationArtifactAsync(
        DbContext db,
        Guid runId,
        Guid segmentId,
        CancellationToken cancellationToken)
    {
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

    private async Task<ContextRef> LoadContextAsync(
        Guid tenantId,
        Guid runId,
        Guid segmentId,
        CancellationToken cancellationToken)
    {
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            var assignment = await db.Set<SegmentContextAssignment>()
                .AsNoTracking()
                .FirstOrDefaultAsync(a => a.SegmentId == segmentId, cancellationToken).ConfigureAwait(false);
            if (assignment is null)
            {
                return new ContextRef(false, null, null, null);
            }

            var window = await db.Set<ContextWindow>()
                .AsNoTracking()
                .FirstOrDefaultAsync(w => w.Id == assignment.ContextWindowId, cancellationToken).ConfigureAwait(false);
            if (window is null || window.TenantId != tenantId || window.RunId != runId)
            {
                return new ContextRef(false, null, null, null);
            }

            var artifactId = await FindContextArtifactAsync(db, runId, window.Sequence, window.ContextHash, cancellationToken).ConfigureAwait(false);
            return new ContextRef(true, window.Id, window.ContextText, artifactId);
        }
    }

    private async Task<Guid?> FindContextArtifactAsync(
        DbContext db,
        Guid runId,
        int sequence,
        string contextHash,
        CancellationToken cancellationToken)
    {
        var rows = await db.Set<Artifact>()
            .AsNoTracking()
            .Where(a => a.ProcessingRunId == runId && a.Type == ArtifactType.ContextWindow)
            .OrderByDescending(a => a.CreatedAt)
            .Select(a => new { a.Id, a.MetadataJson })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var row in rows)
        {
            if (string.IsNullOrWhiteSpace(row.MetadataJson))
            {
                continue;
            }

            if (TryParseContextIdentity(row.MetadataJson, out var parsedSequence, out var parsedHash)
                && parsedSequence == sequence
                && string.Equals(parsedHash, contextHash, StringComparison.Ordinal))
            {
                return row.Id;
            }
        }

        return null;
    }

    private static bool TryParseContextIdentity(string json, out int sequence, out string hash)
    {
        sequence = -1;
        hash = string.Empty;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("sequence", out var sequenceElement)
                || sequenceElement.ValueKind is not JsonValueKind.Number
                || !sequenceElement.TryGetInt32(out sequence)
                || sequence < 0)
            {
                return false;
            }

            if (!document.RootElement.TryGetProperty("contextHash", out var hashElement)
                || hashElement.ValueKind is not JsonValueKind.String)
            {
                return false;
            }

            hash = hashElement.GetString() ?? string.Empty;
            return !string.IsNullOrWhiteSpace(hash);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private async Task<LoadedTranscript> LoadSelectedTranscriptAsync(
        Guid tenantId,
        Guid projectId,
        Guid runId,
        Guid segmentId,
        CancellationToken cancellationToken)
    {
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            var versions = await db.Set<TranscriptVersion>()
                .AsNoTracking()
                .Where(v => v.RunId == runId && v.SegmentId == segmentId)
                .OrderByDescending(v => v.CreatedAt)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            foreach (var version in versions)
            {
                if (version.TenantId != tenantId || version.ProjectId != projectId)
                {
                    throw new ForbiddenException($"Transcript version '{version.Id:D}' does not belong to the current tenant/project/run.");
                }
            }

            var selected = versions.FirstOrDefault(v => v.IsSelected)
                ?? versions.OrderByDescending(v => v.Confidence).FirstOrDefault();
            if (selected is null)
            {
                throw new ErrorCodeException(ErrorCodes.ValidationFailed, $"Segment '{segmentId:D}' has no selected transcript; translation cannot proceed.");
            }

            return new LoadedTranscript(selected.Id, selected.Text, selected.WordTimestampsArtifactId);
        }
    }

    private async Task<string> LoadSpeakerDisplayAsync(
        Guid tenantId,
        Guid projectId,
        Guid? speakerId,
        CancellationToken cancellationToken)
    {
        if (!speakerId.HasValue || speakerId.Value == Guid.Empty)
        {
            return UnknownSpeaker;
        }

        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            var speaker = await db.Set<Speaker>()
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.Id == speakerId.Value, cancellationToken).ConfigureAwait(false);
            if (speaker is null || speaker.TenantId != tenantId || speaker.ProjectId != projectId)
            {
                return UnknownSpeaker;
            }

            return string.IsNullOrWhiteSpace(speaker.DisplayName) ? UnknownSpeaker : speaker.DisplayName;
        }
    }

    private async Task RecordExecutionAsync(
        StageExecution execution,
        ProviderType provider,
        string model,
        TranslationResponse? response,
        string promptHash,
        OutcomeClass outcome,
        string requestHash,
        string? responseHash,
        long latencyMs,
        bool isFallback,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var scope = isFallback
            ? string.Concat(execution.ScopeType.ToString(), ":", execution.ScopeId, ":", provider.ToString(), ":", model, ":fallback")
            : string.Concat(execution.ScopeType.ToString(), ":", execution.ScopeId, ":", provider.ToString(), ":", model);
        var idempotencyKey = ProviderExecutionRecorder.BuildIdempotencyKey(
            execution.ProcessingRunId, nameof(StageType.Translation), scope, execution.Attempt);
        string? externalJobId = null;
        if (response?.RawMetadata is not null
            && response.RawMetadata.TryGetValue("mock.job_id", out var jobId)
            && !string.IsNullOrWhiteSpace(jobId))
        {
            externalJobId = jobId.Trim();
        }

        var row = new ProviderExecution(
            Guid.NewGuid(), execution.TenantId, execution.ProjectId, execution.ProcessingRunId,
            execution.Id, provider, ProviderCapability.Translation, model,
            response?.ModelVersion, response?.Deployment, null, null,
            execution.Attempt, requestHash, responseHash, Math.Max(0, latencyMs),
            response?.Usage?.TokensIn, response?.Usage?.TokensOut, response?.Usage?.AudioSeconds,
            response?.Usage?.EstimatedCostUsd, response?.Usage?.EstimatedCostUsd, null,
            outcome, isFallback ? "fallback" : null,
            string.Concat(PromptTemplateIdText, ":", PromptTemplateVersion), promptHash,
            null, externalJobId, idempotencyKey, now);
        await _recorder.RecordAsync(row, cancellationToken).ConfigureAwait(false);
    }

    private static int TokensOf(TranslationResponse response, string sourceText)
    {
        var fromUsage = (response.Usage?.TokensIn ?? 0) + (response.Usage?.TokensOut ?? 0);
        if (fromUsage > 0)
        {
            return fromUsage;
        }

        var chars = sourceText.Length + response.PrimaryText.Length
            + (response.Alternatives?.Sum(t => t?.Length ?? 0) ?? 0);
        return Math.Max(1, chars / 4);
    }

    private static double CostOf(TranslationResponse response)
    {
        if (response.Usage?.EstimatedCostUsd.HasValue == true)
        {
            var cost = response.Usage.EstimatedCostUsd.Value;
            return double.IsNaN(cost) || cost < 0.0 ? 0.0 : cost;
        }

        return 0.0;
    }

    private static string? ValidateResponse(TranslationResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);

        if (string.IsNullOrWhiteSpace(response.PrimaryText))
        {
            return "Translation provider returned empty primary text.";
        }

        if (response.Alternatives is null)
        {
            return "Translation provider returned no alternatives collection.";
        }

        if (double.IsNaN(response.SemanticScore) || double.IsNaN(response.NaturalnessScore))
        {
            return "Translation provider returned invalid quality scores.";
        }

        return null;
    }

    private static bool IsTransport(Exception exception)
    {
        return exception is HttpRequestException or TimeoutException or SocketException or IOException;
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

    private static double Clamp01(double value)
    {
        if (double.IsNaN(value))
        {
            return 0;
        }

        return Math.Clamp(value, 0.0, 1.0);
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
#pragma warning disable CA1031 // Reconcile is best effort after success; the translation result stands.
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

            if (execution.StageType != StageType.Translation)
            {
                throw new DomainException($"Stage execution '{executionId}' is '{execution.StageType}', not Translation.");
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
            return "Translation failed.";
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
