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
using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Domain.Enums;
using DubbingPlatform.Domain.Exceptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DubbingPlatform.Application.Services;

/// <summary>
/// Result of source-separation for one run. <c>SelectedDialogueArtifactId</c>
/// is the downstream contract: the first entry of the execution's
/// <c>OutputArtifactIdsJson</c> (canonical on skip/fallback, separated
/// dialogue on success). <c>IsSkipped</c> is true only for disabled/auto-skip;
/// low-confidence and error fallbacks are <c>Completed</c> with
/// <c>IsFallback</c> plus a <c>SEPARATION_FALLBACK</c> QC warning.
/// </summary>
public sealed record SeparationDecision(
    Guid SelectedDialogueArtifactId,
    Guid CanonicalArtifactId,
    Guid? DialogueArtifactId,
    Guid? BackgroundArtifactId,
    bool IsSkipped,
    bool IsFallback,
    string? FallbackReason,
    double? NormalizedConfidence,
    double? RawConfidence,
    string? Provider,
    string? Model,
    double Threshold);

/// <summary>
/// Project-settings parsing for separation. Reads top-level
/// <c>sourceSeparation</c> (<c>disabled|enabled|auto</c>, default disabled),
/// <c>separationThreshold</c> (0..1 override, else the configured default),
/// and <c>failOnSeparationError</c> (default false). Unknown policy strings
/// fail closed to disabled; invalid JSON fails closed to all defaults.
/// Keys are matched case-insensitively; values are trimmed.
/// </summary>
public static class SeparationPolicyParser
{
    public static (SourceSeparationPolicy Policy, double Threshold, bool FailOnError) Parse(
        string? settingsJson,
        double defaultThreshold)
    {
        var threshold = double.IsNaN(defaultThreshold) || defaultThreshold < 0.0 || defaultThreshold > 1.0
            ? 0.70
            : defaultThreshold;

        if (string.IsNullOrWhiteSpace(settingsJson))
        {
            return (SourceSeparationPolicy.Disabled, threshold, false);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(settingsJson);
        }
        catch (JsonException)
        {
            return (SourceSeparationPolicy.Disabled, threshold, false);
        }

        using (document)
        {
            if (document.RootElement.ValueKind is not JsonValueKind.Object)
            {
                return (SourceSeparationPolicy.Disabled, threshold, false);
            }

            var policy = SourceSeparationPolicy.Disabled;
            var rawPolicy = GetString(document.RootElement, "sourceSeparation");
            if (!string.IsNullOrWhiteSpace(rawPolicy))
            {
                var normalized = rawPolicy.Trim().ToLowerInvariant();
                policy = normalized switch
                {
                    "enabled" => SourceSeparationPolicy.Enabled,
                    "auto" => SourceSeparationPolicy.Auto,
                    "disabled" => SourceSeparationPolicy.Disabled,
                    _ => SourceSeparationPolicy.Disabled,
                };
            }

            var overrideThreshold = GetDouble(document.RootElement, "separationThreshold");
            if (overrideThreshold.HasValue
                && !double.IsNaN(overrideThreshold.Value)
                && overrideThreshold.Value >= 0.0
                && overrideThreshold.Value <= 1.0)
            {
                threshold = overrideThreshold.Value;
            }

            var failOn = GetBool(document.RootElement, "failOnSeparationError") ?? false;
            return (policy, threshold, failOn);
        }
    }

    private static string? GetString(JsonElement root, string name)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)
                && property.Value.ValueKind == JsonValueKind.String)
            {
                return property.Value.GetString();
            }
        }

        return null;
    }

    private static double? GetDouble(JsonElement root, string name)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (!string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (property.Value.ValueKind == JsonValueKind.Number
                && property.Value.TryGetDouble(out var number))
            {
                return number;
            }

            if (property.Value.ValueKind == JsonValueKind.String
                && double.TryParse(
                    property.Value.GetString()?.Trim(),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var parsed))
            {
                return parsed;
            }

            return null;
        }

        return null;
    }

    private static bool? GetBool(JsonElement root, string name)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (!string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (property.Value.ValueKind is JsonValueKind.True)
            {
                return true;
            }

            if (property.Value.ValueKind is JsonValueKind.False)
            {
                return false;
            }

            if (property.Value.ValueKind == JsonValueKind.String)
            {
                var text = property.Value.GetString()?.Trim();
                if (bool.TryParse(text, out var parsed))
                {
                    return parsed;
                }
            }

            return null;
        }

        return null;
    }
}

/// <summary>
/// Normalized-confidence math. Raw provider confidences are never compared
/// cross-provider; each is mapped to 0..1 via the descriptor
/// <c>ConfidenceSemantics</c> range (<c>min-max</c>, invariant doubles) before
/// threshold comparison. Unparseable, empty, or degenerate ranges default to
/// 0..1. Results are clamped to 0..1; NaN raw maps to 0.
/// </summary>
public static class ConfidenceNormalizer
{
    public static (double Min, double Max) ParseRange(string? semantics)
    {
        if (string.IsNullOrWhiteSpace(semantics))
        {
            return (0.0, 1.0);
        }

        var parts = semantics.Split('-', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2)
        {
            return (0.0, 1.0);
        }

        if (!double.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var min)
            || !double.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var max)
            || double.IsNaN(min)
            || double.IsNaN(max)
            || max <= min)
        {
            return (0.0, 1.0);
        }

        return (min, max);
    }

    public static double Normalize(double raw, string? semantics)
    {
        if (double.IsNaN(raw))
        {
            return 0.0;
        }

        var (min, max) = ParseRange(semantics);
        var normalized = (raw - min) / (max - min);
        if (double.IsNaN(normalized))
        {
            return 0.0;
        }

        return Math.Clamp(normalized, 0.0, 1.0);
    }
}

/// <summary>
/// Optional source separation with normalized confidence, safe fallback, and
/// auditable decisions. Disabled (or auto with no background) skips with the
/// canonical artifact selected and no provider call. Enabled resolves the
/// provider via <see cref="ProviderResolver"/> (capability
/// <c>SourceSeparation</c>, compatibility checked inside the resolver),
/// calls <c>SeparateAsync</c> with the canonical artifact reference, and
/// normalizes the raw confidence via the descriptor
/// <c>ConfidenceSemantics</c> range. At or above threshold the dialogue stem
/// (+ background when present) is materialized from canonical bytes (the mock
/// contract returns provider-side ids only, not bytes; real byte-returning
/// adapters land in a later task), FFprobe-validated as decodable audio, and
/// published with <c>parents=[canonical]</c>. Below threshold, or on
/// permanent provider errors, the run safely falls back to canonical as
/// <c>Completed</c> with a <c>SEPARATION_FALLBACK</c> stage note + QC warning
/// (never failing the run unless settings
/// <c>failOnSeparationError=true</c>). Transient provider failures retry
/// within the logical stage budget, then fall back (or rethrow for transport
/// retry when fail-on is set). Every provider call is recorded in
/// <c>ProviderExecution</c>, even fallbacks. Lease loss or run cancellation
/// discards provider output with no artifact commit.
/// </summary>
public sealed class SourceSeparationService
{
    /// <summary>
    /// QC and stage-note code for every fallback (low confidence or error).
    /// Queryable via <c>quality_results.code</c> and
    /// <c>stage_executions.error_code</c>.
    /// </summary>
    public const string FallbackCode = "SEPARATION_FALLBACK";

    private readonly IStageExecutionContextFactory _contextFactory;
    private readonly ArtifactService _artifacts;
    private readonly IArtifactStorage _storage;
    private readonly IFFprobeService _ffprobe;
    private readonly ISourceSeparationProvider _separation;
    private readonly ProviderResolver _resolver;
    private readonly IDescriptorStore _descriptors;
    private readonly ProviderExecutionRecorder _recorder;
    private readonly MediaOptions _media;
    private readonly RetryOptions _retry;
    private readonly ILogger<SourceSeparationService> _logger;

    public SourceSeparationService(
        IStageExecutionContextFactory contextFactory,
        ArtifactService artifacts,
        IArtifactStorage storage,
        IFFprobeService ffprobe,
        ISourceSeparationProvider separation,
        ProviderResolver resolver,
        IDescriptorStore descriptors,
        ProviderExecutionRecorder recorder,
        IOptions<MediaOptions> media,
        IOptions<RetryOptions> retry,
        ILogger<SourceSeparationService> logger)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        ArgumentNullException.ThrowIfNull(artifacts);
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(ffprobe);
        ArgumentNullException.ThrowIfNull(separation);
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(descriptors);
        ArgumentNullException.ThrowIfNull(recorder);
        ArgumentNullException.ThrowIfNull(media);
        ArgumentNullException.ThrowIfNull(retry);
        ArgumentNullException.ThrowIfNull(logger);
        _contextFactory = contextFactory;
        _artifacts = artifacts;
        _storage = storage;
        _ffprobe = ffprobe;
        _separation = separation;
        _resolver = resolver;
        _descriptors = descriptors;
        _recorder = recorder;
        _media = media.Value;
        _retry = retry.Value;
        _logger = logger;
    }

    /// <summary>
    /// Decides separation for one run. <paramref name="canonicalArtifactId"/>
    /// may be empty to auto-resolve the latest <c>CanonicalAudio</c> artifact
    /// for the run. Completes (or skips) the execution lease-fenced and
    /// returns the downstream-selected dialogue artifact id first in the
    /// execution output.
    /// </summary>
    public async Task<SeparationDecision> DecideAsync(
        Guid tenantId,
        Guid projectId,
        Guid runId,
        Guid canonicalArtifactId,
        Guid executionId,
        string owner,
        string token,
        CancellationToken cancellationToken = default)
    {
        RequireTenant(tenantId);
        RequireId(projectId, nameof(projectId));
        RequireId(runId, nameof(runId));
        RequireId(executionId, nameof(executionId));
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        var project = await LoadOwnedProjectAsync(tenantId, projectId, cancellationToken).ConfigureAwait(false);
        var execution = await LoadExecutionAsync(tenantId, executionId, projectId, runId, owner, token, cancellationToken).ConfigureAwait(false);

        var idempotent = await TryReturnIdempotentAsync(tenantId, execution, cancellationToken).ConfigureAwait(false);
        if (idempotent is not null)
        {
            return idempotent;
        }

        await EnsureRunActiveAsync(tenantId, runId, projectId, cancellationToken).ConfigureAwait(false);

        var canonical = await ResolveCanonicalAsync(tenantId, projectId, runId, canonicalArtifactId, cancellationToken).ConfigureAwait(false);
        var (policy, threshold, failOn) = SeparationPolicyParser.Parse(project.SettingsJson, _media.SeparationThreshold);

        if (policy == SourceSeparationPolicy.Disabled)
        {
            var reason = "Source separation disabled by project settings (settings.sourceSeparation=disabled); canonical audio selected.";
            await EnsureLeaseRunningAsync(tenantId, executionId, owner, token, cancellationToken).ConfigureAwait(false);
            var stages = new StageExecutionService(_contextFactory, global::Microsoft.Extensions.Options.Options.Create(_retry));
            await stages.SkipAsync(
                tenantId, executionId, owner, token,
                [canonical.Artifact.Id.ToString("N")],
                reason,
                cancellationToken).ConfigureAwait(false);
            _logger.LogInformation(
                "Source separation skipped for run {RunId}: disabled.",
                runId);
            return new SeparationDecision(
                canonical.Artifact.Id, canonical.Artifact.Id, null, null,
                true, false, reason, null, null, null, null, threshold);
        }

        if (policy == SourceSeparationPolicy.Auto)
        {
            var needsSeparation = await NeedsSeparationAsync(tenantId, projectId, runId, cancellationToken).ConfigureAwait(false);
            if (!needsSeparation)
            {
                var reason = "Source separation auto-skipped: no background content detected (mono/analysis plan); canonical audio selected.";
                await EnsureLeaseRunningAsync(tenantId, executionId, owner, token, cancellationToken).ConfigureAwait(false);
                var stages = new StageExecutionService(_contextFactory, global::Microsoft.Extensions.Options.Options.Create(_retry));
                await stages.SkipAsync(
                    tenantId, executionId, owner, token,
                    [canonical.Artifact.Id.ToString("N")],
                    reason,
                    cancellationToken).ConfigureAwait(false);
                _logger.LogInformation(
                    "Source separation skipped for run {RunId}: auto no-background.",
                    runId);
                return new SeparationDecision(
                    canonical.Artifact.Id, canonical.Artifact.Id, null, null,
                    true, false, reason, null, null, null, null, threshold);
            }
        }

        return await SeparateEnabledAsync(
            tenantId, project, canonical, execution, owner, token,
            threshold, failOn, cancellationToken).ConfigureAwait(false);
    }

    private async Task<SeparationDecision> SeparateEnabledAsync(
        Guid tenantId,
        DubbingProject project,
        CanonicalBundle canonical,
        StageExecution execution,
        string owner,
        string token,
        double threshold,
        bool failOn,
        CancellationToken cancellationToken)
    {
        var projectId = project.Id;
        var runId = execution.ProcessingRunId;
        var executionId = execution.Id;

        await EnsureLeaseRunningAsync(tenantId, executionId, owner, token, cancellationToken).ConfigureAwait(false);

        ProviderType provider;
        string model;
        try
        {
            var durationMs = await LoadDurationMsAsync(tenantId, projectId, cancellationToken).ConfigureAwait(false);
            (provider, model) = await _resolver.ResolveAsync(
                ProviderCapability.SourceSeparation,
                tenantId,
                project.SourceLanguage,
                canonical.Content.SizeBytes,
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
#pragma warning disable CA1031 // Safe-fallback contract: resolver failures fall back to canonical unless fail-on is set.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            if (failOn)
            {
                throw;
            }

            var reason = string.Concat(
                "Source separation unavailable (",
                Truncate(ClassifyMessage(ex)),
                "); canonical audio selected.");
            return await FallbackAsync(
                tenantId, canonical, execution, owner, token,
                provider: null, model: null,
                rawConfidence: null, normalizedConfidence: null, threshold,
                reason, OutcomeClass.ProviderUnavailable,
                requestHash: null, responseHash: null, latencyMs: 0,
                usage: null, descriptorRegion: null,
                cancellationToken).ConfigureAwait(false);
        }

        var semantics = await LoadSemanticsAsync(tenantId, provider, cancellationToken).ConfigureAwait(false);

        if (provider != ProviderType.Mock)
        {
            if (failOn)
            {
                throw new ErrorCodeException(
                    ErrorCodes.ProviderConfigurationError,
                    string.Concat("No source-separation adapter for provider '", provider.ToString(), "'."));
            }

            var reason = string.Concat(
                "No source-separation adapter for provider '",
                provider.ToString(),
                "'; canonical audio selected.");
            return await FallbackAsync(
                tenantId, canonical, execution, owner, token,
                provider.ToString(), model,
                null, null, threshold,
                reason, OutcomeClass.UnsupportedCapability,
                requestHash: null, responseHash: null, latencyMs: 0,
                usage: null, descriptorRegion: null,
                cancellationToken).ConfigureAwait(false);
        }

        var request = new SeparationRequest(
            tenantId, projectId, runId,
            canonical.Artifact.Id.ToString("N"),
            project.SourceLanguage,
            canonical.Content.SizeBytes,
            await LoadDurationMsAsync(tenantId, projectId, cancellationToken).ConfigureAwait(false),
            "flac");
        var requestHash = ConfigurationHashCalculator.Compute(request);

        var maxAttempts = MaxAttempts();
        SeparationResponse? response = null;
        long latencyMs = 0;
        Exception? lastTransient = null;

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await EnsureLeaseRunningAsync(tenantId, executionId, owner, token, cancellationToken).ConfigureAwait(false);

            var stopwatch = Stopwatch.StartNew();
            try
            {
                response = await _separation.SeparateAsync(request, cancellationToken).ConfigureAwait(false);
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
#pragma warning disable CA1031 // Retry-budget contract: transient provider failures retry, permanent fall back (or rethrow when fail-on).
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
                    if (failOn)
                    {
                        throw;
                    }

                    var reason = string.Concat(
                        "Source separation transient failure (",
                        Truncate(ClassifyMessage(ex)),
                        "); canonical audio selected.");
                    return await FallbackAsync(
                        tenantId, canonical, execution, owner, token,
                        provider.ToString(), model,
                        null, null, threshold,
                        reason, MapOutcome(ex),
                        requestHash, null, latencyMs,
                        null, await LoadRegionAsync(tenantId, provider, cancellationToken).ConfigureAwait(false),
                        cancellationToken).ConfigureAwait(false);
                }

                if (failOn)
                {
                    throw;
                }

                var permanentReason = string.Concat(
                    "Source separation failed (",
                    Truncate(ClassifyMessage(ex)),
                    "); canonical audio selected.");
                return await FallbackAsync(
                    tenantId, canonical, execution, owner, token,
                    provider.ToString(), model,
                    null, null, threshold,
                    permanentReason, MapOutcome(ex),
                    requestHash, null, latencyMs,
                    null, await LoadRegionAsync(tenantId, provider, cancellationToken).ConfigureAwait(false),
                    cancellationToken).ConfigureAwait(false);
            }
        }

        if (response is null)
        {
            if (lastTransient is not null && failOn)
            {
                throw lastTransient;
            }

            var reason = "Source separation returned no result; canonical audio selected.";
            return await FallbackAsync(
                tenantId, canonical, execution, owner, token,
                provider.ToString(), model,
                null, null, threshold,
                reason, OutcomeClass.ProviderPermanentFailure,
                requestHash, null, latencyMs,
                null, await LoadRegionAsync(tenantId, provider, cancellationToken).ConfigureAwait(false),
                cancellationToken).ConfigureAwait(false);
        }

        if (string.IsNullOrWhiteSpace(response.DialogueArtifactId)
            || double.IsNaN(response.Confidence))
        {
            if (failOn)
            {
                throw new ErrorCodeException(
                    ErrorCodes.ProviderInvalidResponse,
                    "Source-separation provider returned an invalid response.");
            }

            var reason = "Source-separation provider returned an invalid response; canonical audio selected.";
            var responseHash = ConfigurationHashCalculator.Compute(new
            {
                dialogue = response.DialogueArtifactId ?? string.Empty,
                background = response.BackgroundArtifactId ?? string.Empty,
                confidence = response.Confidence,
                model = response.Model,
            });
            return await FallbackAsync(
                tenantId, canonical, execution, owner, token,
                provider.ToString(), response.Model,
                response.Confidence, null, threshold,
                reason, OutcomeClass.ProviderInvalidResponse,
                requestHash, responseHash, latencyMs,
                response.Usage, await LoadRegionAsync(tenantId, provider, cancellationToken).ConfigureAwait(false),
                cancellationToken).ConfigureAwait(false);
        }

        var normalized = ConfidenceNormalizer.Normalize(response.Confidence, semantics);
        var responseHashOk = ConfigurationHashCalculator.Compute(new
        {
            dialogue = response.DialogueArtifactId,
            background = response.BackgroundArtifactId ?? string.Empty,
            confidence = response.Confidence,
            model = response.Model,
        });

        if (normalized < threshold)
        {
            if (failOn)
            {
                throw new ErrorCodeException(
                    ErrorCodes.ProviderFailed,
                    string.Concat(
                        "Source-separation confidence ",
                        normalized.ToString("F2", System.Globalization.CultureInfo.InvariantCulture),
                        " is below threshold ",
                        threshold.ToString("F2", System.Globalization.CultureInfo.InvariantCulture),
                        "."));
            }

            var reason = string.Concat(
                "Source-separation confidence ",
                normalized.ToString("F2", System.Globalization.CultureInfo.InvariantCulture),
                " below threshold ",
                threshold.ToString("F2", System.Globalization.CultureInfo.InvariantCulture),
                "; canonical audio selected.");
            return await FallbackAsync(
                tenantId, canonical, execution, owner, token,
                provider.ToString(), response.Model,
                response.Confidence, normalized, threshold,
                reason, OutcomeClass.QualityBelowThreshold,
                requestHash, responseHashOk, latencyMs,
                response.Usage, await LoadRegionAsync(tenantId, provider, cancellationToken).ConfigureAwait(false),
                cancellationToken).ConfigureAwait(false);
        }

        return await PublishStemsAsync(
            tenantId, canonical, execution, owner, token,
            provider, model, semantics, response, normalized, threshold,
            requestHash, responseHashOk, latencyMs, failOn,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<SeparationDecision> PublishStemsAsync(
        Guid tenantId,
        CanonicalBundle canonical,
        StageExecution execution,
        string owner,
        string token,
        ProviderType provider,
        string model,
        string semantics,
        SeparationResponse response,
        double normalized,
        double threshold,
        string requestHash,
        string responseHash,
        long latencyMs,
        bool failOn,
        CancellationToken cancellationToken)
    {
        var runId = execution.ProcessingRunId;
        var projectId = execution.ProjectId;
        var executionId = execution.Id;

        await EnsureLeaseRunningAsync(tenantId, executionId, owner, token, cancellationToken).ConfigureAwait(false);

        var workDir = CreateTempWorkingDir();
        try
        {
            var tempPath = Path.Combine(workDir, "canonical.flac");
            await DownloadToFileAsync(canonical.Content.StorageKey, tempPath, cancellationToken).ConfigureAwait(false);

            FfprobeResult probe;
            try
            {
                probe = await _ffprobe.ProbeAsync(tempPath, cancellationToken).ConfigureAwait(false);
            }
            catch (ErrorCodeException ex) when (string.Equals(ex.ErrorCode, ErrorCodes.MediaCorrupt, StringComparison.Ordinal))
            {
                if (failOn)
                {
                    throw;
                }

                var reason = "Separated audio failed validation (undecodable); canonical audio selected.";
                return await FallbackAsync(
                    tenantId, canonical, execution, owner, token,
                    provider.ToString(), response.Model,
                    response.Confidence, normalized, threshold,
                    reason, OutcomeClass.ProviderInvalidResponse,
                    requestHash, responseHash, latencyMs,
                    response.Usage, await LoadRegionAsync(tenantId, provider, cancellationToken).ConfigureAwait(false),
                    cancellationToken).ConfigureAwait(false);
            }

            var audio = probe.Streams.FirstOrDefault(s => string.Equals(s.CodecType, "audio", StringComparison.OrdinalIgnoreCase));
            if (audio is null || !MediaValidator.IsDecodableAudio(audio.Codec))
            {
                if (failOn)
                {
                    throw new ErrorCodeException(ErrorCodes.MediaCorrupt, "Separated audio has no decodable audio stream.");
                }

                var reason = "Separated audio failed validation (no decodable stream); canonical audio selected.";
                return await FallbackAsync(
                    tenantId, canonical, execution, owner, token,
                    provider.ToString(), response.Model,
                    response.Confidence, normalized, threshold,
                    reason, OutcomeClass.ProviderInvalidResponse,
                    requestHash, responseHash, latencyMs,
                    response.Usage, await LoadRegionAsync(tenantId, provider, cancellationToken).ConfigureAwait(false),
                    cancellationToken).ConfigureAwait(false);
            }

            await EnsureLeaseRunningAsync(tenantId, executionId, owner, token, cancellationToken).ConfigureAwait(false);

            PublishResult dialogue;
            using (var stream = new FileStream(tempPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                dialogue = await _artifacts.PublishAsync(
                    tenantId, projectId, runId,
                    StageType.SourceSeparation, ArtifactType.DialogueStem,
                    stream, ".flac", "audio/flac",
                    provider.ToString(), model,
                    execution.ConfigurationHash, execution.ExecutionSnapshotHash,
                    [canonical.Artifact.Id],
                    executionId,
                    cancellationToken).ConfigureAwait(false);
            }

            Guid? backgroundId = null;
            if (!string.IsNullOrWhiteSpace(response.BackgroundArtifactId))
            {
                await EnsureLeaseRunningAsync(tenantId, executionId, owner, token, cancellationToken).ConfigureAwait(false);
                PublishResult background;
                using (var stream = new FileStream(tempPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    background = await _artifacts.PublishAsync(
                        tenantId, projectId, runId,
                        StageType.SourceSeparation, ArtifactType.BackgroundStem,
                        stream, ".flac", "audio/flac",
                        provider.ToString(), model,
                        execution.ConfigurationHash, execution.ExecutionSnapshotHash,
                        [canonical.Artifact.Id],
                        executionId,
                        cancellationToken).ConfigureAwait(false);
                }

                backgroundId = background.ArtifactId;
                await WriteStemMetadataAsync(
                    tenantId, background.ArtifactId,
                    provider, model, response, normalized, threshold, semantics, canonical,
                    cancellationToken).ConfigureAwait(false);
            }

            await WriteStemMetadataAsync(
                tenantId, dialogue.ArtifactId,
                provider, model, response, normalized, threshold, semantics, canonical,
                cancellationToken).ConfigureAwait(false);

            await RecordExecutionAsync(
                tenantId, execution, provider, model, response,
                OutcomeClass.Success, null,
                requestHash, responseHash, latencyMs,
                await LoadRegionAsync(tenantId, provider, cancellationToken).ConfigureAwait(false),
                cancellationToken).ConfigureAwait(false);

            var outputIds = backgroundId.HasValue
                ? new[] { dialogue.ArtifactId.ToString("N"), backgroundId.Value.ToString("N") }
                : new[] { dialogue.ArtifactId.ToString("N") };

            var stages = new StageExecutionService(_contextFactory, global::Microsoft.Extensions.Options.Options.Create(_retry));
            await stages.CompleteAsync(
                tenantId, executionId, owner, token, outputIds, cancellationToken).ConfigureAwait(false);

            _logger.LogInformation(
                "Source separation selected stems for run {RunId} via {Provider} (confidence {Confidence}).",
                runId, provider.ToString(), normalized.ToString("F2", System.Globalization.CultureInfo.InvariantCulture));

            return new SeparationDecision(
                dialogue.ArtifactId, canonical.Artifact.Id, dialogue.ArtifactId, backgroundId,
                false, false, null, normalized, response.Confidence,
                provider.ToString(), model, threshold);
        }
        finally
        {
            DeleteWorkDirQuietly(workDir);
        }
    }

    private async Task<SeparationDecision> FallbackAsync(
        Guid tenantId,
        CanonicalBundle canonical,
        StageExecution execution,
        string owner,
        string token,
        string? provider,
        string? model,
        double? rawConfidence,
        double? normalizedConfidence,
        double threshold,
        string reason,
        OutcomeClass outcome,
        string? requestHash,
        string? responseHash,
        long latencyMs,
        ProviderUsage? usage,
        string? descriptorRegion,
        CancellationToken cancellationToken)
    {
        var executionId = execution.Id;

        await EnsureLeaseRunningAsync(tenantId, executionId, owner, token, cancellationToken).ConfigureAwait(false);

        if (requestHash is not null)
        {
            await RecordExecutionRawAsync(
                tenantId, execution, provider, model, outcome, reason,
                requestHash, responseHash, latencyMs, usage, descriptorRegion,
                cancellationToken).ConfigureAwait(false);
        }

        var stages = new StageExecutionService(_contextFactory, global::Microsoft.Extensions.Options.Options.Create(_retry));
        await stages.CompleteAsync(
            tenantId, executionId, owner, token,
            [canonical.Artifact.Id.ToString("N")],
            cancellationToken).ConfigureAwait(false);
        await stages.NoteFallbackAsync(
            tenantId, executionId, owner, token,
            FallbackCode, reason, cancellationToken).ConfigureAwait(false);
        await CreateQualityWarningAsync(
            tenantId, execution, canonical.Artifact.Id,
            reason, rawConfidence, normalizedConfidence, threshold,
            provider, model, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Source separation fallback for run {RunId}: {Reason}.",
            execution.ProcessingRunId, reason);

        return new SeparationDecision(
            canonical.Artifact.Id, canonical.Artifact.Id, null, null,
            false, true, reason, normalizedConfidence, rawConfidence,
            provider, model, threshold);
    }

    private async Task RecordExecutionAsync(
        Guid tenantId,
        StageExecution execution,
        ProviderType provider,
        string model,
        SeparationResponse response,
        OutcomeClass outcome,
        string? fallbackReason,
        string requestHash,
        string? responseHash,
        long latencyMs,
        string? region,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var scope = string.Concat(execution.ScopeType.ToString(), ":", execution.ScopeId);
        var idempotencyKey = ProviderExecutionRecorder.BuildIdempotencyKey(
            execution.ProcessingRunId, nameof(StageType.SourceSeparation), scope, execution.Attempt);
        string? externalJobId = null;
        if (response.RawMetadata is not null
            && response.RawMetadata.TryGetValue("mock.job_id", out var jobId)
            && !string.IsNullOrWhiteSpace(jobId))
        {
            externalJobId = jobId.Trim();
        }

        var row = new ProviderExecution(
            Guid.NewGuid(), tenantId, execution.ProjectId, execution.ProcessingRunId,
            execution.Id, provider, ProviderCapability.SourceSeparation, model,
            response.ModelVersion, response.Deployment, region, null,
            execution.Attempt, requestHash, responseHash, Math.Max(0, latencyMs),
            response.Usage?.TokensIn, response.Usage?.TokensOut, response.Usage?.AudioSeconds,
            response.Usage?.EstimatedCostUsd, response.Usage?.EstimatedCostUsd, null,
            outcome,
            string.IsNullOrWhiteSpace(fallbackReason) ? null : Truncate(fallbackReason),
            null, null, null, externalJobId, idempotencyKey, now);
        await _recorder.RecordAsync(row, cancellationToken).ConfigureAwait(false);
    }

    private async Task RecordExecutionRawAsync(
        Guid tenantId,
        StageExecution execution,
        string? providerName,
        string? model,
        OutcomeClass outcome,
        string reason,
        string requestHash,
        string? responseHash,
        long latencyMs,
        ProviderUsage? usage,
        string? region,
        CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<ProviderType>(providerName ?? string.Empty, ignoreCase: true, out var provider))
        {
            provider = ProviderType.Mock;
        }

        var now = DateTimeOffset.UtcNow;
        var scope = string.Concat(execution.ScopeType.ToString(), ":", execution.ScopeId);
        var idempotencyKey = ProviderExecutionRecorder.BuildIdempotencyKey(
            execution.ProcessingRunId, nameof(StageType.SourceSeparation), scope, execution.Attempt);
        var row = new ProviderExecution(
            Guid.NewGuid(), tenantId, execution.ProjectId, execution.ProcessingRunId,
            execution.Id, provider, ProviderCapability.SourceSeparation,
            string.IsNullOrWhiteSpace(model) ? "mock-default" : model.Trim(),
            null, null, region, null,
            execution.Attempt, requestHash, responseHash, Math.Max(0, latencyMs),
            usage?.TokensIn, usage?.TokensOut, usage?.AudioSeconds,
            usage?.EstimatedCostUsd, usage?.EstimatedCostUsd, null,
            outcome, Truncate(reason),
            null, null, null, null, idempotencyKey, now);
        await _recorder.RecordAsync(row, cancellationToken).ConfigureAwait(false);
    }

    private async Task CreateQualityWarningAsync(
        Guid tenantId,
        StageExecution execution,
        Guid selectedArtifactId,
        string reason,
        double? rawConfidence,
        double? normalizedConfidence,
        double threshold,
        string? provider,
        string? model,
        CancellationToken cancellationToken)
    {
        var details = JsonSerializer.Serialize(new
        {
            code = FallbackCode,
            reason,
            rawConfidence,
            normalizedConfidence,
            threshold,
            provider,
            model,
            selectedArtifactId = selectedArtifactId.ToString("N"),
        });

        var row = new QualityResult(
            Guid.NewGuid(), tenantId, execution.ProjectId, execution.ProcessingRunId,
            execution.ScopeType, execution.ScopeId, execution.SegmentId,
            QualityStatus.PassWithWarnings, FallbackCode, "Warning",
            Truncate(reason), details, selectedArtifactId, DateTimeOffset.UtcNow);

        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            db.Set<QualityResult>().Add(row);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task WriteStemMetadataAsync(
        Guid tenantId,
        Guid artifactId,
        ProviderType provider,
        string model,
        SeparationResponse response,
        double normalized,
        double threshold,
        string semantics,
        CanonicalBundle canonical,
        CancellationToken cancellationToken)
    {
        var metadata = JsonSerializer.Serialize(new
        {
            provider = provider.ToString(),
            model,
            providerDialogueId = response.DialogueArtifactId,
            providerBackgroundId = response.BackgroundArtifactId,
            rawConfidence = response.Confidence,
            normalizedConfidence = normalized,
            threshold,
            confidenceSemantics = semantics,
            canonicalArtifactId = canonical.Artifact.Id.ToString("N"),
            canonicalContentHash = canonical.Content.ContentHash,
        });

        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            await db.Database.ExecuteSqlRawAsync(
                "UPDATE artifacts SET metadata_json = {0} WHERE id = {1} AND tenant_id = {2}",
                metadata, artifactId, tenantId).ConfigureAwait(false);
        }
    }

    private async Task<SeparationDecision?> TryReturnIdempotentAsync(
        Guid tenantId,
        StageExecution execution,
        CancellationToken cancellationToken)
    {
        if (execution.Status is not (StageStatus.Completed or StageStatus.Skipped))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(execution.OutputArtifactIdsJson))
        {
            return null;
        }

        string[] outputIds;
        try
        {
            outputIds = JsonSerializer.Deserialize<string[]>(execution.OutputArtifactIdsJson) ?? [];
        }
        catch (JsonException)
        {
            return null;
        }

        if (outputIds.Length == 0 || !Guid.TryParseExact(outputIds[0], "N", out var selected))
        {
            return null;
        }

        Guid? dialogue = null;
        Guid? background = null;
        if (!string.Equals(execution.ErrorCode?.Trim(), FallbackCode, StringComparison.Ordinal)
            && execution.Status == StageStatus.Completed)
        {
            dialogue = selected;
            if (outputIds.Length > 1 && Guid.TryParseExact(outputIds[1], "N", out var bg))
            {
                background = bg;
            }
        }

        var isSkipped = execution.Status == StageStatus.Skipped;
        var isFallback = !isSkipped
            && string.Equals(execution.ErrorCode?.Trim(), FallbackCode, StringComparison.Ordinal);

        Guid canonicalId = selected;
        if (dialogue.HasValue || isFallback)
        {
            var resolved = await TryResolveCanonicalIdAsync(tenantId, execution.ProcessingRunId, cancellationToken).ConfigureAwait(false);
            if (resolved.HasValue)
            {
                canonicalId = resolved.Value;
            }
        }

        return new SeparationDecision(
            selected, canonicalId, dialogue, background,
            isSkipped, isFallback, execution.ErrorMessage,
            null, null, null, null, _media.SeparationThreshold);
    }

    private sealed record CanonicalBundle(Artifact Artifact, ContentObject Content);

    private async Task<CanonicalBundle> ResolveCanonicalAsync(
        Guid tenantId,
        Guid projectId,
        Guid runId,
        Guid canonicalArtifactId,
        CancellationToken cancellationToken)
    {
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            Artifact? artifact = null;
            if (canonicalArtifactId != Guid.Empty)
            {
                artifact = await db.Set<Artifact>()
                    .AsNoTracking()
                    .FirstOrDefaultAsync(a => a.Id == canonicalArtifactId, cancellationToken).ConfigureAwait(false);
                if (artifact is null || artifact.TenantId != tenantId || artifact.ProcessingRunId != runId)
                {
                    throw new ErrorCodeException(ErrorCodes.ArtifactUnavailable, "Canonical audio artifact was not found.");
                }
            }
            else
            {
                artifact = await db.Set<Artifact>()
                    .AsNoTracking()
                    .Where(a => a.ProcessingRunId == runId && a.Type == ArtifactType.CanonicalAudio)
                    .OrderByDescending(a => a.CreatedAt)
                    .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
                if (artifact is null)
                {
                    throw new ErrorCodeException(ErrorCodes.ArtifactUnavailable, "Canonical audio artifact was not found.");
                }
            }

            var content = await db.Set<ContentObject>()
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == artifact.ContentObjectId, cancellationToken).ConfigureAwait(false);
            if (content is null || content.Status != ContentObjectStatus.Committed)
            {
                throw new ErrorCodeException(ErrorCodes.ArtifactUnavailable, "Canonical audio content is unavailable.");
            }

            return new CanonicalBundle(artifact, content);
        }
    }

    private async Task<Guid?> TryResolveCanonicalIdAsync(Guid tenantId, Guid runId, CancellationToken cancellationToken)
    {
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            return await db.Set<Artifact>()
                .Where(a => a.ProcessingRunId == runId && a.Type == ArtifactType.CanonicalAudio)
                .OrderByDescending(a => a.CreatedAt)
                .Select(a => (Guid?)a.Id)
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<bool> NeedsSeparationAsync(
        Guid tenantId,
        Guid projectId,
        Guid runId,
        CancellationToken cancellationToken)
    {
        var fromAnalysis = await TryReadAnalysisPlanAsync(tenantId, runId, cancellationToken).ConfigureAwait(false);
        if (fromAnalysis.HasValue)
        {
            return fromAnalysis.Value;
        }

        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            var channels = await db.Set<MediaAsset>()
                .Where(a => a.ProjectId == projectId && a.Status == MediaAssetStatus.Valid)
                .OrderBy(a => a.CreatedAt)
                .Select(a => (int?)a.Channels)
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            return (channels ?? 2) >= 2;
        }
    }

    private async Task<bool?> TryReadAnalysisPlanAsync(Guid tenantId, Guid runId, CancellationToken cancellationToken)
    {
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            var metadata = await db.Set<Artifact>()
                .Where(a => a.ProcessingRunId == runId && a.Type == ArtifactType.FfprobeAnalysis)
                .OrderByDescending(a => a.CreatedAt)
                .Select(a => a.MetadataJson)
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(metadata))
            {
                return null;
            }

            try
            {
                using var document = JsonDocument.Parse(metadata);
                if (document.RootElement.ValueKind is not JsonValueKind.Object)
                {
                    return null;
                }

                if (!document.RootElement.TryGetProperty("plan", out var plan)
                    || plan.ValueKind is not JsonValueKind.Object)
                {
                    return null;
                }

                foreach (var property in plan.EnumerateObject())
                {
                    if (string.Equals(property.Name, "needsSeparation", StringComparison.OrdinalIgnoreCase))
                    {
                        if (property.Value.ValueKind is JsonValueKind.True)
                        {
                            return true;
                        }

                        if (property.Value.ValueKind is JsonValueKind.False)
                        {
                            return false;
                        }

                        return null;
                    }
                }

                return null;
            }
            catch (JsonException)
            {
                return null;
            }
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

    private async Task<string> LoadSemanticsAsync(Guid tenantId, ProviderType provider, CancellationToken cancellationToken)
    {
        try
        {
            var candidates = await _descriptors.GetCandidatesAsync(
                ProviderCapability.SourceSeparation, tenantId, cancellationToken).ConfigureAwait(false);
            var match = candidates.FirstOrDefault(d => d.Provider == provider);
            return string.IsNullOrWhiteSpace(match?.ConfidenceSemantics) ? "0-1" : match.ConfidenceSemantics;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
#pragma warning disable CA1031 // Descriptor fallback: unknown semantics default to 0-1 rather than failing the run.
        catch (Exception)
#pragma warning restore CA1031
        {
            return "0-1";
        }
    }

    private async Task<string?> LoadRegionAsync(Guid tenantId, ProviderType provider, CancellationToken cancellationToken)
    {
        try
        {
            var candidates = await _descriptors.GetCandidatesAsync(
                ProviderCapability.SourceSeparation, tenantId, cancellationToken).ConfigureAwait(false);
            var match = candidates.FirstOrDefault(d => d.Provider == provider);
            return string.IsNullOrWhiteSpace(match?.Region) ? null : match.Region;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
#pragma warning disable CA1031 // Region is audit-only; failures degrade to null.
        catch (Exception)
#pragma warning restore CA1031
        {
            return null;
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

            if (execution.StageType != StageType.SourceSeparation)
            {
                throw new DomainException($"Stage execution '{executionId}' is '{execution.StageType}', not SourceSeparation.");
            }

            if (execution.Status is StageStatus.Completed or StageStatus.Skipped)
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

    private async Task DownloadToFileAsync(string storageKey, string destPath, CancellationToken cancellationToken)
    {
        Stream download;
        try
        {
            download = await _storage.DownloadAsync(storageKey, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutException or SocketException or IOException)
        {
            throw;
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

    private int MaxAttempts()
    {
        if (_retry.PerStageMaxAttempts.TryGetValue(nameof(StageType.SourceSeparation), out var perStage))
        {
            return Math.Clamp(perStage, 1, 10);
        }

        return Math.Clamp(_retry.LogicalStageMaxAttempts, 1, 10);
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
            return "Source separation fallback.";
        }

        var trimmed = message.Trim();
        return trimmed.Length <= 500 ? trimmed : trimmed.Substring(0, 500);
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
