using System.Net.Http.Json;
using System.Text.Json;
using DubbingPlatform.Application.Abstractions.Providers;
using DubbingPlatform.Application.Abstractions.Providers.Dtos;
using DubbingPlatform.Application.Errors;
using DubbingPlatform.Application.Exceptions;
using DubbingPlatform.Application.Options;
using DubbingPlatform.Infrastructure.Providers.Common;
using Microsoft.Extensions.Options;

namespace DubbingPlatform.Infrastructure.Providers.Google;

/// <summary>
/// Google Speech-to-Text v2 adapter. POST <c>{SpeechBase}/v2/...:recognize</c>
/// with <c>{"artifactId","languageCode"}</c>; response
/// <c>{"results":[{"transcript","confidence","words":[]}]}</c>.
/// </summary>
public sealed class GoogleSttProvider : ITranscriptionProvider
{
    public const string ProviderName = "google";

    private const string ModelName = "google-stt-v2";

    private readonly HttpClient _http;
    private readonly GoogleProviderOptions _options;
    private readonly ProviderOptions _providers;

    public GoogleSttProvider(HttpClient http, IOptions<GoogleProviderOptions> options, IOptions<ProviderOptions> providers)
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
        if (!IsReferenced())
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(EffectiveKey()))
        {
            throw new ErrorCodeException(
                ErrorCodes.ProviderConfigurationError,
                "Providers:Google:ApiKey (or Providers:GoogleApiKey) is required when 'google' is enabled or routed.");
        }
    }

    public async Task<TranscriptionResponse> TranscribeAsync(TranscriptionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateConfig();

        var url = string.Concat(SpeechBase(), "/v2/projects/", Uri.EscapeDataString(ProjectId()), "/locations/", Uri.EscapeDataString(_options.Location ?? "global"), ":recognize?key=", Uri.EscapeDataString(EffectiveKey()));
        var payload = new { artifactId = request.ArtifactId, languageCode = request.Language };

        using var response = await ProviderHttpHelper.SendAsync(
            _http,
            () =>
            {
                var message = new HttpRequestMessage(HttpMethod.Post, url);
                message.Content = JsonContent.Create(payload);
                return message;
            },
            cancellationToken).ConfigureAwait(false);

        await ProviderHttpHelper.ThrowIfErrorAsync(response, ProviderName, "recognize", cancellationToken).ConfigureAwait(false);
        using var doc = await ProviderHttpHelper.ReadJsonAsync(response, ProviderName, "recognize", cancellationToken).ConfigureAwait(false);
        return Parse(doc);
    }

    internal TranscriptionResponse Parse(JsonDocument doc)
    {
        var root = doc.RootElement;
        if (!root.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array || results.GetArrayLength() == 0)
        {
            throw new ErrorCodeException(ErrorCodes.ProviderInvalidResponse, "Provider 'google' transcription returned no results.");
        }

        var first = results[0];
        if (!first.TryGetProperty("transcript", out var transcriptProp) || transcriptProp.ValueKind != JsonValueKind.String)
        {
            throw new ErrorCodeException(ErrorCodes.ProviderInvalidResponse, "Provider 'google' transcription returned no transcript.");
        }

        var text = transcriptProp.GetString() ?? string.Empty;
        var confidence = first.TryGetProperty("confidence", out var conf) && conf.ValueKind == JsonValueKind.Number
            ? conf.GetDouble()
            : 0.93;

        var words = new List<WordTimestamp>();
        if (first.TryGetProperty("words", out var wordsProp) && wordsProp.ValueKind == JsonValueKind.Array)
        {
            foreach (var w in wordsProp.EnumerateArray())
            {
                if (!w.TryGetProperty("word", out var wordProp))
                {
                    continue;
                }

                var word = wordProp.GetString() ?? string.Empty;
                var start = w.TryGetProperty("startMs", out var st) && st.ValueKind == JsonValueKind.Number ? st.GetInt32() : 0;
                var end = w.TryGetProperty("endMs", out var en) && en.ValueKind == JsonValueKind.Number ? en.GetInt32() : start + 300;
                words.Add(new WordTimestamp(word, start, end, confidence));
            }
        }

        return new TranscriptionResponse(
            text, confidence, words, ModelName, null, _options.Location,
            new ProviderUsage(null, null, null, null),
            new Dictionary<string, string>(StringComparer.Ordinal) { ["provider"] = ProviderName });
    }

    private string SpeechBase()
    {
        if (!string.IsNullOrWhiteSpace(_options.SpeechBaseUrl))
        {
            return ProviderEndpointValidator.RequireAllowed(_options.SpeechBaseUrl, nameof(GoogleProviderOptions.SpeechBaseUrl));
        }

        return "https://speech.googleapis.com";
    }

    private string ProjectId()
    {
        return string.IsNullOrWhiteSpace(_options.ProjectId) ? "-" : _options.ProjectId.Trim();
    }

    private string EffectiveKey()
    {
        if (!string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            return _options.ApiKey;
        }

        return _providers.GoogleApiKey ?? string.Empty;
    }

    internal bool IsReferenced()
    {
        return Azure.AzureSttProvider.IsProviderReferenced(_providers, "google")
            || Azure.AzureSttProvider.IsProviderReferenced(_providers, "gemini");
    }
}
