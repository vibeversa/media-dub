using System.Net.Http.Json;
using System.Text.Json;
using DubbingPlatform.Application.Abstractions.Providers;
using DubbingPlatform.Application.Abstractions.Providers.Dtos;
using DubbingPlatform.Application.Errors;
using DubbingPlatform.Application.Exceptions;
using DubbingPlatform.Application.Options;
using DubbingPlatform.Infrastructure.Providers.Common;
using Microsoft.Extensions.Options;

namespace DubbingPlatform.Infrastructure.Providers.Azure;

/// <summary>
/// Azure Speech-to-Text adapter (STT + conversation diarization).
/// Wire contract (hermetic, WireMock-testable): POST
/// <c>{SpeechBase}/speech/recognition/transcribe?language={lang}</c> with JSON
/// <c>{"artifactId","language"}</c> and header
/// <c>Ocp-Apim-Subscription-Key</c>; response <c>{"text","confidence","words":[]}</c>.
/// Diarization posts to <c>.../conversation</c> and reads <c>segments[]</c>.
/// Batch: POST <c>{SpeechBase}/speech/batch</c> → <c>202 {"jobId"}</c>;
/// GET <c>{SpeechBase}/speech/batch/{jobId}</c> → <c>{"status","reason","text","confidence"}</c>.
/// Production sends audio binary; the JSON shape keeps CI hermetic.
/// </summary>
public sealed class AzureSttProvider : ITranscriptionProvider, IDiarizationProvider
{
    public const string ProviderName = "azure";

    private const string ModelName = "azure-speech-1";

    private readonly HttpClient _http;
    private readonly AzureProviderOptions _azure;
    private readonly ProviderOptions _providers;

    public AzureSttProvider(HttpClient http, IOptions<AzureProviderOptions> azure, IOptions<ProviderOptions> providers)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(azure);
        ArgumentNullException.ThrowIfNull(providers);
        _http = http;
        _azure = azure.Value;
        _providers = providers.Value;
    }

    public void ValidateConfig()
    {
        if (!IsReferenced())
        {
            return;
        }

        var key = EffectiveSpeechKey();
        var hasEndpoint = !string.IsNullOrWhiteSpace(_azure.SpeechBaseUrl) || !string.IsNullOrWhiteSpace(_azure.SpeechRegion);
        if (string.IsNullOrWhiteSpace(key) || !hasEndpoint)
        {
            throw new ErrorCodeException(
                ErrorCodes.ProviderConfigurationError,
                "Providers:Azure:SpeechKey and SpeechRegion (or SpeechBaseUrl) are required when 'azure' is enabled or routed.");
        }
    }

    public async Task<TranscriptionResponse> TranscribeAsync(TranscriptionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateConfig();

        var baseUrl = SpeechBase();
        var url = string.Concat(baseUrl, "/speech/recognition/transcribe?language=", Uri.EscapeDataString(request.Language ?? string.Empty));
        var payload = new { artifactId = request.ArtifactId, language = request.Language };

        using var response = await ProviderHttpHelper.SendAsync(
            _http,
            () =>
            {
                var message = new HttpRequestMessage(HttpMethod.Post, url);
                message.Content = JsonContent.Create(payload);
                message.Headers.TryAddWithoutValidation("Ocp-Apim-Subscription-Key", EffectiveSpeechKey());
                return message;
            },
            cancellationToken).ConfigureAwait(false);

        await ProviderHttpHelper.ThrowIfErrorAsync(response, ProviderName, "transcribe", cancellationToken).ConfigureAwait(false);
        using var doc = await ProviderHttpHelper.ReadJsonAsync(response, ProviderName, "transcribe", cancellationToken).ConfigureAwait(false);
        return ParseTranscription(doc);
    }

    public async Task<DiarizationResponse> DiarizeAsync(DiarizationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateConfig();

        var baseUrl = SpeechBase();
        var url = string.Concat(baseUrl, "/speech/recognition/conversation?language=", Uri.EscapeDataString(request.Language ?? string.Empty));
        var payload = new { artifactId = request.ArtifactId, language = request.Language };

        using var response = await ProviderHttpHelper.SendAsync(
            _http,
            () =>
            {
                var message = new HttpRequestMessage(HttpMethod.Post, url);
                message.Content = JsonContent.Create(payload);
                message.Headers.TryAddWithoutValidation("Ocp-Apim-Subscription-Key", EffectiveSpeechKey());
                return message;
            },
            cancellationToken).ConfigureAwait(false);

        await ProviderHttpHelper.ThrowIfErrorAsync(response, ProviderName, "diarize", cancellationToken).ConfigureAwait(false);
        using var doc = await ProviderHttpHelper.ReadJsonAsync(response, ProviderName, "diarize", cancellationToken).ConfigureAwait(false);
        return ParseDiarization(doc, request.DurationMs);
    }

    /// <summary>
    /// Starts a batch transcription job. Returns the external job id for
    /// <c>ProviderExecution.ExternalJobId</c> storage and lease-loss reconcile.
    /// </summary>
    public async Task<string> StartBatchAsync(TranscriptionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateConfig();

        var url = string.Concat(SpeechBase(), "/speech/batch?language=", Uri.EscapeDataString(request.Language ?? string.Empty));
        var payload = new { artifactId = request.ArtifactId, language = request.Language };

        using var response = await ProviderHttpHelper.SendAsync(
            _http,
            () =>
            {
                var message = new HttpRequestMessage(HttpMethod.Post, url);
                message.Content = JsonContent.Create(payload);
                message.Headers.TryAddWithoutValidation("Ocp-Apim-Subscription-Key", EffectiveSpeechKey());
                return message;
            },
            cancellationToken).ConfigureAwait(false);

        await ProviderHttpHelper.ThrowIfErrorAsync(response, ProviderName, "batch-start", cancellationToken).ConfigureAwait(false);
        using var doc = await ProviderHttpHelper.ReadJsonAsync(response, ProviderName, "batch-start", cancellationToken).ConfigureAwait(false);
        if (doc.RootElement.TryGetProperty("jobId", out var jobId) && jobId.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(jobId.GetString()))
        {
            return jobId.GetString()!;
        }

        throw new ErrorCodeException(ErrorCodes.ProviderInvalidResponse, "Provider 'azure' batch start returned no jobId.");
    }

    /// <summary>
    /// Fetches batch status for <see cref="ProviderJobPoller"/>.
    /// </summary>
    public async Task<ProviderJobPoller.JobStatus> GetBatchStatusAsync(string externalJobId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(externalJobId);
        ValidateConfig();

        var url = string.Concat(SpeechBase(), "/speech/batch/", Uri.EscapeDataString(externalJobId));
        using var response = await ProviderHttpHelper.SendAsync(
            _http,
            () =>
            {
                var message = new HttpRequestMessage(HttpMethod.Get, url);
                message.Headers.TryAddWithoutValidation("Ocp-Apim-Subscription-Key", EffectiveSpeechKey());
                return message;
            },
            cancellationToken).ConfigureAwait(false);

        await ProviderHttpHelper.ThrowIfErrorAsync(response, ProviderName, "batch-status", cancellationToken).ConfigureAwait(false);
        using var doc = await ProviderHttpHelper.ReadJsonAsync(response, ProviderName, "batch-status", cancellationToken).ConfigureAwait(false);
        var status = doc.RootElement.TryGetProperty("status", out var s) ? s.GetString() : "Running";
        var reason = doc.RootElement.TryGetProperty("reason", out var r) ? r.GetString() : null;
        return ProviderJobPoller.FromStatusString(status, reason);
    }

    internal TranscriptionResponse ParseTranscription(JsonDocument doc)
    {
        var root = doc.RootElement;
        if (!root.TryGetProperty("text", out var textProp) || textProp.ValueKind != JsonValueKind.String)
        {
            throw new ErrorCodeException(ErrorCodes.ProviderInvalidResponse, "Provider 'azure' transcription returned no text.");
        }

        var text = textProp.GetString() ?? string.Empty;
        var confidence = root.TryGetProperty("confidence", out var conf) && conf.ValueKind == JsonValueKind.Number
            ? conf.GetDouble()
            : 0.9;

        var words = new List<WordTimestamp>();
        if (root.TryGetProperty("words", out var wordsProp) && wordsProp.ValueKind == JsonValueKind.Array)
        {
            foreach (var w in wordsProp.EnumerateArray())
            {
                if (!w.TryGetProperty("word", out var wordProp))
                {
                    continue;
                }

                var word = wordProp.GetString() ?? string.Empty;
                var start = w.TryGetProperty("offsetMs", out var off) && off.ValueKind == JsonValueKind.Number ? off.GetInt32() : 0;
                var duration = w.TryGetProperty("durationMs", out var dur) && dur.ValueKind == JsonValueKind.Number ? dur.GetInt32() : 300;
                var wordConf = w.TryGetProperty("confidence", out var wc) && wc.ValueKind == JsonValueKind.Number ? wc.GetDouble() : confidence;
                words.Add(new WordTimestamp(word, start, start + duration, wordConf));
            }
        }

        return new TranscriptionResponse(
            text, confidence, words, ModelName, null, _azure.SpeechRegion,
            new ProviderUsage(null, null, null, null),
            new Dictionary<string, string>(StringComparer.Ordinal) { ["provider"] = ProviderName });
    }

    internal DiarizationResponse ParseDiarization(JsonDocument doc, int durationMs)
    {
        var root = doc.RootElement;
        var segments = new List<DiarizationSegment>();
        double confidence = 0.9;

        if (root.TryGetProperty("confidence", out var conf) && conf.ValueKind == JsonValueKind.Number)
        {
            confidence = conf.GetDouble();
        }

        if (root.TryGetProperty("segments", out var segs) && segs.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in segs.EnumerateArray())
            {
                var speaker = s.TryGetProperty("speaker", out var sp) ? sp.GetString() ?? "spk_0" : "spk_0";
                var start = s.TryGetProperty("startMs", out var st) && st.ValueKind == JsonValueKind.Number ? st.GetInt32() : 0;
                var end = s.TryGetProperty("endMs", out var en) && en.ValueKind == JsonValueKind.Number ? en.GetInt32() : Math.Max(start, durationMs);
                var segConf = s.TryGetProperty("confidence", out var sc) && sc.ValueKind == JsonValueKind.Number ? sc.GetDouble() : confidence;
                segments.Add(new DiarizationSegment(speaker, start, end, segConf));
            }
        }

        if (segments.Count == 0)
        {
            throw new ErrorCodeException(ErrorCodes.ProviderInvalidResponse, "Provider 'azure' diarization returned no segments.");
        }

        var labels = segments.Select(s => s.SpeakerLabel).Distinct(StringComparer.Ordinal).OrderBy(l => l, StringComparer.Ordinal).ToList();
        return new DiarizationResponse(
            segments, labels, confidence, ModelName, null, _azure.SpeechRegion,
            new ProviderUsage(null, null, null, null),
            new Dictionary<string, string>(StringComparer.Ordinal) { ["provider"] = ProviderName });
    }

    private string SpeechBase()
    {
        if (!string.IsNullOrWhiteSpace(_azure.SpeechBaseUrl))
        {
            return ProviderEndpointValidator.RequireAllowed(_azure.SpeechBaseUrl, nameof(AzureProviderOptions.SpeechBaseUrl));
        }

        var region = (_azure.SpeechRegion ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(region))
        {
            throw new ErrorCodeException(
                ErrorCodes.ProviderConfigurationError,
                "Providers:Azure:SpeechRegion (or SpeechBaseUrl) is required when 'azure' is enabled or routed.");
        }

        return string.Concat("https://", region, ".stt.speech.microsoft.com");
    }

    private string EffectiveSpeechKey()
    {
        if (!string.IsNullOrWhiteSpace(_azure.SpeechKey))
        {
            return _azure.SpeechKey;
        }

        return _providers.AzureApiKey ?? string.Empty;
    }

    private bool IsReferenced()
    {
        return IsProviderReferenced(_providers, ProviderName);
    }

    internal static bool IsProviderReferenced(ProviderOptions options, string name)
    {
        if (options.Enabled is not null)
        {
            foreach (var pair in options.Enabled)
            {
                if (pair.Value && string.Equals(pair.Key?.Trim(), name, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        if (options.RoutePriority is not null)
        {
            foreach (var pair in options.RoutePriority)
            {
                foreach (var entry in pair.Value ?? [])
                {
                    if (string.Equals(entry?.Trim(), name, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }
}
