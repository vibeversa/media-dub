using System.Net.Http.Headers;
using System.Text.Json;
using DubbingPlatform.Application.Abstractions.Providers;
using DubbingPlatform.Application.Abstractions.Providers.Dtos;
using DubbingPlatform.Application.Errors;
using DubbingPlatform.Application.Exceptions;
using DubbingPlatform.Application.Options;
using DubbingPlatform.Infrastructure.Providers.Common;
using Microsoft.Extensions.Options;

namespace DubbingPlatform.Infrastructure.Providers.OpenAI;

/// <summary>
/// OpenAI Whisper adapter. POST <c>{BaseUrl}/audio/transcriptions</c> as
/// multipart (<c>file</c> placeholder + <c>model</c>/<c>language</c> fields;
/// the DTO carries an artifact reference, so the part is a metadata stub and
/// WireMock matches on path). Response <c>{"text","confidence","words":[]}</c>.
/// Batch: POST <c>{BaseUrl}/audio/batch</c> → <c>202 {"jobId"}</c>;
/// GET <c>{BaseUrl}/audio/batch/{jobId}</c> → <c>{"status","reason"}</c>.
/// </summary>
public sealed class OpenAiSttProvider : ITranscriptionProvider
{
    public const string ProviderName = "openai";

    private readonly HttpClient _http;
    private readonly OpenAiProviderOptions _options;
    private readonly ProviderOptions _providers;

    public OpenAiSttProvider(HttpClient http, IOptions<OpenAiProviderOptions> options, IOptions<ProviderOptions> providers)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(providers);
        _http = http;
        _options = options.Value;
        _providers = providers.Value;
    }

    public void ValidateConfig()
    {
        if (!Azure.AzureSttProvider.IsProviderReferenced(_providers, ProviderName)
            && !string.Equals(_providers.DefaultProvider, ProviderName, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(EffectiveKey()))
        {
            throw new ErrorCodeException(
                ErrorCodes.ProviderConfigurationError,
                "Providers:OpenAI:ApiKey (or Providers:OpenAIApiKey) is required when 'openai' is enabled or routed.");
        }
    }

    public async Task<TranscriptionResponse> TranscribeAsync(TranscriptionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateConfig();

        var url = string.Concat(BaseUrl(), "/audio/transcriptions");

        using var response = await ProviderHttpHelper.SendAsync(
            _http,
            () =>
            {
                var content = new MultipartFormDataContent
                {
                    { new StringContent(string.Concat("artifact:", request.ArtifactId)), "file", "audio.wav" },
                    { new StringContent(_options.Model), "model" },
                    { new StringContent(request.Language ?? string.Empty), "language" },
                };
                var message = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
                message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", EffectiveKey());
                return message;
            },
            cancellationToken).ConfigureAwait(false);

        await ProviderHttpHelper.ThrowIfErrorAsync(response, ProviderName, "transcriptions", cancellationToken).ConfigureAwait(false);
        using var doc = await ProviderHttpHelper.ReadJsonAsync(response, ProviderName, "transcriptions", cancellationToken).ConfigureAwait(false);
        return Parse(doc);
    }

    public async Task<string> StartBatchAsync(TranscriptionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateConfig();

        var url = string.Concat(BaseUrl(), "/audio/batch");
        using var response = await ProviderHttpHelper.SendAsync(
            _http,
            () =>
            {
                var message = new HttpRequestMessage(HttpMethod.Post, url);
                message.Content = new StringContent(
                    JsonSerializer.Serialize(new { artifactId = request.ArtifactId, model = _options.Model }),
                    System.Text.Encoding.UTF8,
                    "application/json");
                message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", EffectiveKey());
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

        throw new ErrorCodeException(ErrorCodes.ProviderInvalidResponse, "Provider 'openai' batch start returned no jobId.");
    }

    public async Task<ProviderJobPoller.JobStatus> GetBatchStatusAsync(string externalJobId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(externalJobId);
        ValidateConfig();

        var url = string.Concat(BaseUrl(), "/audio/batch/", Uri.EscapeDataString(externalJobId));
        using var response = await ProviderHttpHelper.SendAsync(
            _http,
            () =>
            {
                var message = new HttpRequestMessage(HttpMethod.Get, url);
                message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", EffectiveKey());
                return message;
            },
            cancellationToken).ConfigureAwait(false);

        await ProviderHttpHelper.ThrowIfErrorAsync(response, ProviderName, "batch-status", cancellationToken).ConfigureAwait(false);
        using var doc = await ProviderHttpHelper.ReadJsonAsync(response, ProviderName, "batch-status", cancellationToken).ConfigureAwait(false);
        var status = doc.RootElement.TryGetProperty("status", out var s) ? s.GetString() : "Running";
        var reason = doc.RootElement.TryGetProperty("reason", out var r) ? r.GetString() : null;
        return ProviderJobPoller.FromStatusString(status, reason);
    }

    internal TranscriptionResponse Parse(JsonDocument doc)
    {
        var root = doc.RootElement;
        if (!root.TryGetProperty("text", out var textProp) || textProp.ValueKind != JsonValueKind.String)
        {
            throw new ErrorCodeException(ErrorCodes.ProviderInvalidResponse, "Provider 'openai' transcription returned no text.");
        }

        var text = textProp.GetString() ?? string.Empty;
        var confidence = root.TryGetProperty("confidence", out var conf) && conf.ValueKind == JsonValueKind.Number
            ? conf.GetDouble()
            : 0.93;

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
                var start = w.TryGetProperty("start", out var st) && st.ValueKind == JsonValueKind.Number ? (int)(st.GetDouble() * 1000) : 0;
                if (w.TryGetProperty("startMs", out var stm) && stm.ValueKind == JsonValueKind.Number)
                {
                    start = stm.GetInt32();
                }

                var end = w.TryGetProperty("end", out var en) && en.ValueKind == JsonValueKind.Number ? (int)(en.GetDouble() * 1000) : start + 300;
                if (w.TryGetProperty("endMs", out var enm) && enm.ValueKind == JsonValueKind.Number)
                {
                    end = enm.GetInt32();
                }

                words.Add(new WordTimestamp(word, start, end, confidence));
            }
        }

        return new TranscriptionResponse(
            text, confidence, words, _options.Model, null, null,
            new ProviderUsage(null, null, null, null),
            new Dictionary<string, string>(StringComparer.Ordinal) { ["provider"] = ProviderName });
    }

    private string BaseUrl()
    {
        return ProviderEndpointValidator.RequireAllowed(_options.BaseUrl, nameof(OpenAiProviderOptions.BaseUrl));
    }

    private string EffectiveKey()
    {
        if (!string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            return _options.ApiKey;
        }

        return _providers.OpenAIApiKey ?? string.Empty;
    }
}
