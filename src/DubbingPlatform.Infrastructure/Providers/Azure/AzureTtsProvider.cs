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
/// Azure Speech TTS adapter. POST <c>{SpeechBase}/cognitiveservices/v1</c> with
/// header <c>Ocp-Apim-Subscription-Key</c> and JSON
/// <c>{"text","voice","language"}</c>; response
/// <c>{"contentId","durationMs","voice","confidence"}</c>. Production returns
/// binary audio; the JSON shape keeps CI hermetic and records the content id
/// for <c>ProviderExecution</c> without moving bytes through the provider.
/// Idempotency-Key is sent when the caller supplies run/stage/scope/attempt via
/// <see cref="SynthesizeWithIdempotencyAsync"/>.
/// </summary>
public sealed class AzureTtsProvider : ITtsProvider
{
    public const string ProviderName = "azure";

    private const string ModelName = "azure-tts-1";

    private readonly HttpClient _http;
    private readonly AzureProviderOptions _azure;
    private readonly ProviderOptions _providers;

    public AzureTtsProvider(HttpClient http, IOptions<AzureProviderOptions> azure, IOptions<ProviderOptions> providers)
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
        if (!AzureSttProvider.IsProviderReferenced(_providers, ProviderName))
        {
            return;
        }

        var key = !string.IsNullOrWhiteSpace(_azure.SpeechKey) ? _azure.SpeechKey : _providers.AzureApiKey;
        var hasEndpoint = !string.IsNullOrWhiteSpace(_azure.SpeechBaseUrl) || !string.IsNullOrWhiteSpace(_azure.SpeechRegion);
        if (string.IsNullOrWhiteSpace(key) || !hasEndpoint)
        {
            throw new ErrorCodeException(
                ErrorCodes.ProviderConfigurationError,
                "Providers:Azure:SpeechKey and SpeechRegion (or SpeechBaseUrl) are required when 'azure' is enabled or routed.");
        }
    }

    public Task<TtsResponse> SynthesizeAsync(TtsRequest request, CancellationToken cancellationToken)
    {
        return SynthesizeWithIdempotencyAsync(request, idempotencyKey: null, cancellationToken);
    }

    /// <summary>
    /// Synthesis with an explicit idempotency key
    /// (<c>{run:N}:{stage}:{scope}:{attempt}</c>). Duplicate keys reconcile to the
    /// existing output hash with no double charge (see recorder).
    /// </summary>
    public async Task<TtsResponse> SynthesizeWithIdempotencyAsync(TtsRequest request, string? idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateConfig();

        var baseUrl = SpeechBase();
        var url = string.Concat(baseUrl, "/cognitiveservices/v1");
        var payload = new { text = request.Text, voice = request.VoiceId, language = request.Language };

        using var response = await ProviderHttpHelper.SendAsync(
            _http,
            () =>
            {
                var message = new HttpRequestMessage(HttpMethod.Post, url);
                message.Content = JsonContent.Create(payload);
                var key = !string.IsNullOrWhiteSpace(_azure.SpeechKey) ? _azure.SpeechKey : _providers.AzureApiKey;
                message.Headers.TryAddWithoutValidation("Ocp-Apim-Subscription-Key", key);
                if (!string.IsNullOrWhiteSpace(idempotencyKey))
                {
                    ProviderHttpHelper.AddIdempotencyKey(message, idempotencyKey);
                }

                return message;
            },
            cancellationToken).ConfigureAwait(false);

        await ProviderHttpHelper.ThrowIfErrorAsync(response, ProviderName, "tts", cancellationToken).ConfigureAwait(false);
        using var doc = await ProviderHttpHelper.ReadJsonAsync(response, ProviderName, "tts", cancellationToken).ConfigureAwait(false);
        return Parse(doc, request.VoiceId);
    }

    internal TtsResponse Parse(JsonDocument doc, string fallbackVoice)
    {
        var root = doc.RootElement;
        if (!root.TryGetProperty("contentId", out var idProp) || idProp.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(idProp.GetString()))
        {
            throw new ErrorCodeException(ErrorCodes.ProviderInvalidResponse, "Provider 'azure' TTS returned no contentId.");
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
            idProp.GetString()!, duration, voice, confidence, ModelName, null, _azure.SpeechRegion,
            new ProviderUsage(null, null, duration / 1000.0, null),
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

        return string.Concat("https://", region, ".tts.speech.microsoft.com");
    }
}
