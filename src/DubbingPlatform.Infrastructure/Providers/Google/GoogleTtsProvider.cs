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
/// Google Cloud TTS adapter. POST <c>{TtsBase}/v1/text:synthesize?key={key}</c>
/// with <c>{"input":{"text"},"voice":{"languageCode"},"audioConfig":{}}</c>;
/// response <c>{"contentId","durationMs","voice","confidence"}</c> (production
/// returns <c>audioContent</c> base64; the JSON shape keeps CI hermetic and
/// records the content id for <c>ProviderExecution</c>).
/// </summary>
public sealed class GoogleTtsProvider : ITtsProvider
{
    public const string ProviderName = "google";

    private const string ModelName = "google-tts-1";

    private readonly HttpClient _http;
    private readonly GoogleProviderOptions _options;
    private readonly ProviderOptions _providers;

    public GoogleTtsProvider(HttpClient http, IOptions<GoogleProviderOptions> options, IOptions<ProviderOptions> providers)
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
        if (!Azure.AzureSttProvider.IsProviderReferenced(_providers, "google")
            && !Azure.AzureSttProvider.IsProviderReferenced(_providers, "gemini")
            && !string.Equals(_providers.DefaultProvider, "google", StringComparison.OrdinalIgnoreCase))
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

    public async Task<TtsResponse> SynthesizeAsync(TtsRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateConfig();

        var url = string.Concat(TtsBase(), "/v1/text:synthesize?key=", Uri.EscapeDataString(EffectiveKey()));
        var payload = new
        {
            input = new { text = request.Text },
            voice = new { languageCode = request.Language, name = request.VoiceId },
            audioConfig = new { audioEncoding = "LINEAR16" },
        };
        var idempotencyKey = ProviderHttpHelper.BuildIdempotencyKey(request.RunId, "tts", request.VoiceId, 0);

        using var response = await ProviderHttpHelper.SendAsync(
            _http,
            () =>
            {
                var message = new HttpRequestMessage(HttpMethod.Post, url);
                message.Content = JsonContent.Create(payload);
                ProviderHttpHelper.AddIdempotencyKey(message, idempotencyKey);
                return message;
            },
            cancellationToken).ConfigureAwait(false);

        await ProviderHttpHelper.ThrowIfErrorAsync(response, ProviderName, "text-synthesize", cancellationToken).ConfigureAwait(false);
        using var doc = await ProviderHttpHelper.ReadJsonAsync(response, ProviderName, "text-synthesize", cancellationToken).ConfigureAwait(false);
        return Parse(doc, request.VoiceId);
    }

    internal TtsResponse Parse(JsonDocument doc, string fallbackVoice)
    {
        var root = doc.RootElement;
        string? contentId = null;
        if (root.TryGetProperty("contentId", out var idProp) && idProp.ValueKind == JsonValueKind.String)
        {
            contentId = idProp.GetString();
        }
        else if (root.TryGetProperty("audioContent", out var audioProp) && audioProp.ValueKind == JsonValueKind.String)
        {
            contentId = string.Concat("google-tts-", (audioProp.GetString() ?? string.Empty).Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        if (string.IsNullOrWhiteSpace(contentId))
        {
            throw new ErrorCodeException(ErrorCodes.ProviderInvalidResponse, "Provider 'google' TTS returned no content.");
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
            contentId!, duration, voice, confidence, ModelName, null, _options.Location,
            new ProviderUsage(null, null, duration / 1000.0, null),
            new Dictionary<string, string>(StringComparer.Ordinal) { ["provider"] = ProviderName });
    }

    private string TtsBase()
    {
        if (!string.IsNullOrWhiteSpace(_options.TtsBaseUrl))
        {
            return ProviderEndpointValidator.RequireAllowed(_options.TtsBaseUrl, nameof(GoogleProviderOptions.TtsBaseUrl));
        }

        return "https://texttospeech.googleapis.com";
    }

    private string EffectiveKey()
    {
        if (!string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            return _options.ApiKey;
        }

        return _providers.GoogleApiKey ?? string.Empty;
    }
}
