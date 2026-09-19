using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DubbingPlatform.Application.Abstractions.Providers;
using DubbingPlatform.Application.Abstractions.Providers.Dtos;
using DubbingPlatform.Application.Errors;
using DubbingPlatform.Application.Exceptions;
using DubbingPlatform.Application.Options;
using DubbingPlatform.Domain.Enums;
using DubbingPlatform.Infrastructure.Observability;
using DubbingPlatform.Infrastructure.Providers.Common;
using Microsoft.Extensions.Options;

namespace DubbingPlatform.Infrastructure.Providers.LocalInference;

/// <summary>
/// Local-inference sidecar bridge (Task 043, optional, disabled by default).
/// HTTP is the default transport: POST <c>{Endpoint}/infer</c> with
/// <c>{"modelName","modelVersion","artifactHash","capability","payload",
/// "deviceProfile"}</c> (plus a <c>X-Sidecar-Protocol: grpc</c> marker when
/// <c>Protocol=grpc</c>; true gRPC transport is a drop-in swap per
/// <c>proto/local_inference.proto</c>). Response
/// <c>{"output","confidence","modelHash","device"}</c>. Model hash + device +
/// warmup state are echoed in <c>RawMetadata</c> so <c>ProviderExecution</c>
/// records them. <see cref="WarmupAsync"/> tries POST <c>{Endpoint}/warmup</c>
/// first and falls back to GET <c>{Endpoint}/health</c> (existing stubs), and
/// must succeed before <see cref="InferAsync"/> accepts work. Concurrency is
/// bounded by a <see cref="SemaphoreSlim"/> (<c>MaxConcurrency</c>: GPU
/// <c>1</c>, CPU <c>2</c>). Local-only routing reuses
/// <c>ExternalProvidersAllowed=false + LocalInferenceAllowed=true</c> (see
/// <c>PolicyChecker</c>; no new policy column). Also bridges
/// transcription/translation/TTS via capability payloads.
/// Failure mapping: model-load fail → <c>PROVIDER_FAILED</c> permanent
/// (fallback per budget); GPU exhaustion (429/503 with
/// <c>exhausted:true</c>) → <c>PROVIDER_RATE_LIMITED</c> delayed retry +
/// <c>localinference.gpu_exhausted</c> scale signal; timeout →
/// <c>PROVIDER_TIMEOUT</c>; invalid version → fail-fast
/// <c>PROVIDER_CONFIGURATION_ERROR</c>.
/// </summary>
public sealed class LocalInferenceProvider : ILocalInferenceProvider, ITranscriptionProvider, ITranslationProvider, ITtsProvider, IDisposable
{
    public const string ProviderName = "local";

    public const string HttpClientName = "local-inference";

    private readonly HttpClient _http;
    private readonly LocalInferenceOptions _options;
    private readonly ModelRegistry _registry;
    private readonly SemaphoreSlim _gate;
    private bool _warmed;
    private bool _disposed;

    public LocalInferenceProvider(HttpClient http, IOptions<LocalInferenceOptions> options)
        : this(http, options, null)
    {
    }

    public LocalInferenceProvider(HttpClient http, IOptions<LocalInferenceOptions> options, ModelRegistry? registry)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(options);
        _http = http;
        _options = options.Value;
        _registry = registry ?? new ModelRegistry(options);
        _gate = new SemaphoreSlim(Math.Max(1, _options.MaxConcurrency), Math.Max(1, _options.MaxConcurrency));
    }

    public void ValidateConfig()
    {
        if (string.IsNullOrWhiteSpace(_options.Endpoint))
        {
            throw new ErrorCodeException(
                ErrorCodes.ProviderConfigurationError,
                "LocalInference:Endpoint must not be empty.");
        }

        try
        {
            LocalInferenceOptions.RequireSidecarEndpoint(_options.Endpoint, nameof(LocalInferenceOptions.Endpoint));
        }
        catch (ArgumentException ex)
        {
            throw new ErrorCodeException(ErrorCodes.ProviderConfigurationError, ex.Message, ex);
        }

        if (string.IsNullOrWhiteSpace(_options.ModelVersion))
        {
            throw new ErrorCodeException(
                ErrorCodes.ProviderConfigurationError,
                "LocalInference:ModelVersion must not be empty (invalid version, fail-fast).");
        }
    }

    /// <summary>
    /// Warms the sidecar: POST <c>{Endpoint}/warmup</c> with
    /// <c>{modelName,modelVersion,deviceProfile}</c>, falling back to GET
    /// <c>{Endpoint}/health</c> when the sidecar has no <c>/warmup</c> route
    /// (404). Must succeed before <see cref="InferAsync"/> accepts work.
    /// Invalid versions fail fast; other non-success maps to
    /// <c>PROVIDER_FAILED</c>.
    /// </summary>
    public async Task WarmupAsync(CancellationToken cancellationToken)
    {
        ValidateConfig();
        var entry = ResolveEntry(ProviderCapability.LocalInference);
        var baseUrl = EndpointBase();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(_options.WarmupTimeoutSec, 1, 300)));
        var token = timeout.Token;

        var warmupUrl = string.Concat(baseUrl, "/warmup");
        var warmupBody = new
        {
            modelName = entry.Id,
            modelVersion = entry.Version,
            deviceProfile = entry.DeviceProfile,
        };

        using (var warmupResponse = await ProviderHttpHelper.SendAsync(
            _http,
            () =>
            {
                var message = new HttpRequestMessage(HttpMethod.Post, warmupUrl);
                message.Content = JsonContent.Create(warmupBody);
                ApplyProtocolMarker(message);
                return message;
            },
            token).ConfigureAwait(false))
        {
            if (warmupResponse.StatusCode == HttpStatusCode.NotFound)
            {
                // Legacy sidecars expose only GET /health; fall through.
            }
            else
            {
                await ThrowIfWarmupErrorAsync(warmupResponse, token).ConfigureAwait(false);
                _warmed = true;
                return;
            }
        }

        var healthUrl = string.Concat(baseUrl, "/health");
        using var healthResponse = await ProviderHttpHelper.SendAsync(
            _http,
            () => new HttpRequestMessage(HttpMethod.Get, healthUrl),
            token).ConfigureAwait(false);

        await ThrowIfWarmupErrorAsync(healthResponse, token).ConfigureAwait(false);
        _warmed = true;
    }

    public bool IsWarmed => _warmed;

    public async Task<LocalInferenceResponse> InferAsync(LocalInferenceRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateConfig();
        EnsureWarmed();

        var entry = ResolveEntry(ProviderCapability.LocalInference);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var url = string.Concat(EndpointBase(), "/infer");
            var payload = new
            {
                modelName = entry.Id,
                modelVersion = entry.Version,
                artifactHash = entry.ArtifactHash,
                capability = "local-inference",
                payload = request.PayloadJson,
                deviceProfile = entry.DeviceProfile,
            };

            using var response = await ProviderHttpHelper.SendAsync(
                _http,
                () =>
                {
                    var message = new HttpRequestMessage(HttpMethod.Post, url);
                    message.Content = JsonContent.Create(payload);
                    ApplyProtocolMarker(message);
                    return message;
                },
                cancellationToken).ConfigureAwait(false);

            await ThrowIfInferErrorAsync(response, entry.DeviceProfile, cancellationToken).ConfigureAwait(false);
            using var doc = await ProviderHttpHelper.ReadJsonAsync(response, ProviderName, "infer", cancellationToken).ConfigureAwait(false);
            return Parse(request.ModelId, doc, entry);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<TranscriptionResponse> TranscribeAsync(TranscriptionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var entry = ResolveEntry(ProviderCapability.Transcription);
        var output = await InferCapabilityAsync("transcription", request.ArtifactId, entry, cancellationToken).ConfigureAwait(false);
        return new TranscriptionResponse(
            output, 0.9, [], entry.Id, entry.Version, null,
            new ProviderUsage(null, null, null, null),
            Metadata(entry));
    }

    public async Task<TranslationResponse> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var entry = ResolveEntry(ProviderCapability.Translation);
        var output = await InferCapabilityAsync("translation", request.ArtifactId, entry, cancellationToken).ConfigureAwait(false);
        return new TranslationResponse(
            output, [], 0.9, 0.9, 0.9, 0.9,
            entry.Id, entry.Version, null,
            new ProviderUsage(null, null, null, null),
            Metadata(entry));
    }

    public async Task<TtsResponse> SynthesizeAsync(TtsRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var entry = ResolveEntry(ProviderCapability.Tts);
        var output = await InferCapabilityAsync("tts", request.Text, entry, cancellationToken).ConfigureAwait(false);
        var contentId = string.IsNullOrWhiteSpace(output) ? string.Concat("local-tts-", Guid.NewGuid().ToString("N")) : output;
        return new TtsResponse(
            contentId, request.DurationMs, request.VoiceId, 0.9,
            entry.Id, entry.Version, null,
            new ProviderUsage(null, null, null, null),
            Metadata(entry));
    }

    /// <summary>
    /// Whether the response body signals GPU exhaustion
    /// (<c>"exhausted":true</c>, case-insensitive, tolerant whitespace).
    /// </summary>
    public static bool IsGpuExhaustionSignal(string? bodyPreview)
    {
        if (string.IsNullOrWhiteSpace(bodyPreview))
        {
            return false;
        }

        return bodyPreview.Contains("\"exhausted\"", StringComparison.OrdinalIgnoreCase)
            && bodyPreview.Contains("true", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether the body signals a model-load failure (permanent, fallback).
    /// </summary>
    public static bool IsModelLoadFailure(string? bodyPreview)
    {
        if (string.IsNullOrWhiteSpace(bodyPreview))
        {
            return false;
        }

        return bodyPreview.Contains("model_load", StringComparison.OrdinalIgnoreCase)
            || bodyPreview.Contains("model-load", StringComparison.OrdinalIgnoreCase)
            || bodyPreview.Contains("model_not_found", StringComparison.OrdinalIgnoreCase)
            || bodyPreview.Contains("invalid_model", StringComparison.OrdinalIgnoreCase)
            || bodyPreview.Contains("model-failed-to-load", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether the body signals an invalid model version (fail-fast).
    /// </summary>
    public static bool IsInvalidVersionSignal(string? bodyPreview)
    {
        if (string.IsNullOrWhiteSpace(bodyPreview))
        {
            return false;
        }

        return bodyPreview.Contains("invalid_version", StringComparison.OrdinalIgnoreCase)
            || bodyPreview.Contains("version_mismatch", StringComparison.OrdinalIgnoreCase)
            || bodyPreview.Contains("version-mismatch", StringComparison.OrdinalIgnoreCase)
            || bodyPreview.Contains("unsupported_version", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<string> InferCapabilityAsync(string capability, string payload, LocalInferenceModelOptions entry, CancellationToken cancellationToken)
    {
        ValidateConfig();
        EnsureWarmed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var url = string.Concat(EndpointBase(), "/infer");
            var body = new
            {
                modelName = entry.Id,
                modelVersion = entry.Version,
                artifactHash = entry.ArtifactHash,
                capability,
                payload,
                deviceProfile = entry.DeviceProfile,
            };

            using var response = await ProviderHttpHelper.SendAsync(
                _http,
                () =>
                {
                    var message = new HttpRequestMessage(HttpMethod.Post, url);
                    message.Content = JsonContent.Create(body);
                    ApplyProtocolMarker(message);
                    return message;
                },
                cancellationToken).ConfigureAwait(false);

            await ThrowIfInferErrorAsync(response, entry.DeviceProfile, cancellationToken).ConfigureAwait(false);
            using var doc = await ProviderHttpHelper.ReadJsonAsync(response, ProviderName, "infer", cancellationToken).ConfigureAwait(false);
            if (doc.RootElement.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.String)
            {
                return output.GetString() ?? string.Empty;
            }

            throw new ErrorCodeException(ErrorCodes.ProviderInvalidResponse, "Provider 'local' returned no output.");
        }
        finally
        {
            _gate.Release();
        }
    }

    internal LocalInferenceResponse Parse(string modelId, JsonDocument doc)
    {
        return Parse(modelId, doc, ResolveEntry(ProviderCapability.LocalInference));
    }

    internal LocalInferenceResponse Parse(string modelId, JsonDocument doc, LocalInferenceModelOptions entry)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(entry);
        var root = doc.RootElement;
        if (!root.TryGetProperty("output", out var outputProp) || outputProp.ValueKind != JsonValueKind.String)
        {
            throw new ErrorCodeException(ErrorCodes.ProviderInvalidResponse, "Provider 'local' returned no output.");
        }

        var output = outputProp.GetString() ?? string.Empty;
        var confidence = root.TryGetProperty("confidence", out var conf) && conf.ValueKind == JsonValueKind.Number
            ? conf.GetDouble()
            : 0.9;

        return new LocalInferenceResponse(
            modelId, output, confidence, entry.Id, entry.Version, entry.DeviceProfile,
            new ProviderUsage(null, null, null, null),
            Metadata(entry));
    }

    private Dictionary<string, string> Metadata()
    {
        return Metadata(ResolveEntry(ProviderCapability.LocalInference));
    }

    private Dictionary<string, string> Metadata(LocalInferenceModelOptions entry)
    {
        var hash = string.IsNullOrWhiteSpace(entry.ArtifactHash)
            ? (string.IsNullOrWhiteSpace(_options.ModelHash) ? "unspecified" : _options.ModelHash)
            : entry.ArtifactHash;
        var device = string.IsNullOrWhiteSpace(entry.DeviceProfile)
            ? (_options.Device ?? "cpu")
            : entry.DeviceProfile;
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["provider"] = ProviderName,
            ["model.hash"] = hash,
            ["device"] = device,
            ["warmed"] = _warmed ? "true" : "false",
        };
    }

    private LocalInferenceModelOptions ResolveEntry(ProviderCapability capability)
    {
        try
        {
            return _registry.ResolveModel(capability);
        }
        catch (ErrorCodeException ex) when (string.Equals(ex.ErrorCode, ErrorCodes.ProviderConfigurationError, StringComparison.Ordinal))
        {
            if (capability == ProviderCapability.LocalInference)
            {
                throw;
            }

            return _registry.ResolveModel(ProviderCapability.LocalInference);
        }
    }

    private async Task ThrowIfInferErrorAsync(HttpResponseMessage response, string deviceProfile, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var bodyPreview = await ReadBodyPreviewAsync(response, cancellationToken).ConfigureAwait(false);
        var status = (int)response.StatusCode;
        var path = response.RequestMessage?.RequestUri?.AbsolutePath ?? "infer";

        if (IsInvalidVersionSignal(bodyPreview))
        {
            throw new ErrorCodeException(
                ErrorCodes.ProviderConfigurationError,
                $"Provider '{ProviderName}' invalid model version for {path} ({status}, fail-fast).");
        }

        if ((response.StatusCode == HttpStatusCode.TooManyRequests
            || response.StatusCode == HttpStatusCode.ServiceUnavailable)
            && IsGpuExhaustionSignal(bodyPreview))
        {
            LocalInferenceMetrics.Exhausted(deviceProfile);
            var retryAfter = ProviderHttpHelper.GetRetryAfter(response);
            var suffix = retryAfter.HasValue
                ? $" Retry after {retryAfter.Value.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)}s."
                : string.Empty;
            throw new ErrorCodeException(
                ErrorCodes.ProviderRateLimited,
                $"Provider '{ProviderName}' GPU exhausted for {path} ({status}).{suffix}");
        }

        if (IsModelLoadFailure(bodyPreview))
        {
            throw new ErrorCodeException(
                ErrorCodes.ProviderFailed,
                $"Provider '{ProviderName}' model load failed for {path} ({status}).");
        }

        await ProviderHttpHelper.ThrowIfErrorAsync(response, ProviderName, "infer", cancellationToken).ConfigureAwait(false);
    }

    private async Task ThrowIfWarmupErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var bodyPreview = await ReadBodyPreviewAsync(response, cancellationToken).ConfigureAwait(false);
        var status = (int)response.StatusCode;
        var path = response.RequestMessage?.RequestUri?.AbsolutePath ?? "warmup";

        if (IsInvalidVersionSignal(bodyPreview))
        {
            throw new ErrorCodeException(
                ErrorCodes.ProviderConfigurationError,
                $"Provider '{ProviderName}' invalid model version for {path} ({status}, fail-fast).");
        }

        if ((response.StatusCode == HttpStatusCode.TooManyRequests
            || response.StatusCode == HttpStatusCode.ServiceUnavailable)
            && IsGpuExhaustionSignal(bodyPreview))
        {
            LocalInferenceMetrics.Exhausted(_options.Device);
            throw new ErrorCodeException(
                ErrorCodes.ProviderRateLimited,
                $"Provider '{ProviderName}' GPU exhausted for {path} ({status}).");
        }

        await ProviderHttpHelper.ThrowIfErrorAsync(response, ProviderName, "warmup", cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string> ReadBodyPreviewAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linked.CancelAfter(TimeSpan.FromSeconds(2));
            var body = await response.Content.ReadAsStringAsync(linked.Token).ConfigureAwait(false);
            body ??= string.Empty;
            body = body.Trim();
            return body.Length > 2048 ? body.Substring(0, 2048) : body;
        }
        catch (OperationCanceledException)
        {
            return string.Empty;
        }
#pragma warning disable CA1031 // Best-effort preview for local classification; never fails the call.
        catch (Exception)
#pragma warning restore CA1031
        {
            return string.Empty;
        }
    }

    private void ApplyProtocolMarker(HttpRequestMessage message)
    {
        if (string.Equals(_options.Protocol, "grpc", StringComparison.OrdinalIgnoreCase))
        {
            message.Headers.TryAddWithoutValidation("X-Sidecar-Protocol", "grpc");
        }
    }

    private void EnsureWarmed()
    {
        if (!_warmed)
        {
            throw new ErrorCodeException(
                ErrorCodes.ProviderConfigurationError,
                "Local sidecar is not warmed. Call WarmupAsync before accepting work.");
        }
    }

    private string EndpointBase()
    {
        try
        {
            return LocalInferenceOptions.RequireSidecarEndpoint(_options.Endpoint, nameof(LocalInferenceOptions.Endpoint));
        }
        catch (ArgumentException ex)
        {
            throw new ErrorCodeException(ErrorCodes.ProviderConfigurationError, ex.Message, ex);
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _gate.Dispose();
            _disposed = true;
        }
    }
}
