using System.Net.Http.Headers;
using System.Net.Http.Json;
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
/// OpenAI TTS adapter. POST <c>{BaseUrl}/audio/speech</c> with
/// <c>{"model","input","voice"}</c>; response
/// <c>{"contentId","durationMs","voice","confidence"}</c> (production returns
/// binary audio; JSON keeps CI hermetic). Sends <c>Idempotency-Key</c>.
/// </summary>
public sealed class OpenAiTtsProvider : ITtsProvider
{
    public const string ProviderName = "openai";

    private readonly HttpClient _http;
    private readonly OpenAiProviderOptions _options;
    private readonly ProviderOptions _providers;

    public OpenAiTtsProvider(HttpClient http, IOptions<OpenAiProviderOptions> options, IOptions<ProviderOptions> providers)
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

    public Task<TtsResponse> SynthesizeAsync(TtsRequest request, CancellationToken cancellationToken)
    {
        return SynthesizeWithIdempotencyAsync(request, request.RunId, "tts", request.VoiceId, 0, cancellationToken);
    }

    public async Task<TtsResponse> SynthesizeWithIdempotencyAsync(
        TtsRequest request,
        Guid runId,
        string stage,
        string scope,
        int attempt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateConfig();

        var url = string.Concat(BaseUrl(), "/audio/speech");
        var body = new { model = _options.TtsModel, input = request.Text, voice = request.VoiceId };
        var idempotencyKey = ProviderHttpHelper.BuildIdempotencyKey(runId, stage, scope, attempt);

        using var response = await ProviderHttpHelper.SendAsync(
            _http,
            () =>
            {
                var message = new HttpRequestMessage(HttpMethod.Post, url);
                message.Content = JsonContent.Create(body);
                message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", EffectiveKey());
                ProviderHttpHelper.AddIdempotencyKey(message, idempotencyKey);
                return message;
            },
            cancellationToken).ConfigureAwait(false);

        await ProviderHttpHelper.ThrowIfErrorAsync(response, ProviderName, "audio-speech", cancellationToken).ConfigureAwait(false);
        using var doc = await ProviderHttpHelper.ReadJsonAsync(response, ProviderName, "audio-speech", cancellationToken).ConfigureAwait(false);
        return Parse(doc, request.VoiceId);
    }

    internal TtsResponse Parse(JsonDocument doc, string fallbackVoice)
    {
        var root = doc.RootElement;
        if (!root.TryGetProperty("contentId", out var idProp) || idProp.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(idProp.GetString()))
        {
            throw new ErrorCodeException(ErrorCodes.ProviderInvalidResponse, "Provider 'openai' TTS returned no contentId.");
        }

        var duration = root.TryGetProperty("durationMs", out var dur) && dur.ValueKind == JsonValueKind.Number
            ? dur.GetInt32()
            : 1000;
        var voice = root.TryGetProperty("voice", out var voiceProp) && voiceProp.ValueKind == JsonValueKind.String
            ? voiceProp.GetString() ?? fallbackVoice
            : fallbackVoice;
        var confidence = root.TryGetProperty("confidence", out var conf) && conf.ValueKind == JsonValueKind.Number
            ? conf.GetDouble()
            : 0.95;

        return new TtsResponse(
            idProp.GetString()!, duration, voice, confidence, _options.TtsModel, null, null,
            new ProviderUsage(null, null, duration / 1000.0, null),
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
