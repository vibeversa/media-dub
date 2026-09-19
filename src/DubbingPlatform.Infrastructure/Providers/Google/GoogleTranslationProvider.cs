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
/// Google Translate v3 adapter. POST <c>{TranslateBase}/v3/...:translateText</c>
/// with <c>{"contents":[],"sourceLanguage","targetLanguage"}</c>; response
/// <c>{"translations":[{"translatedText","confidence"}],"alternatives":[]}</c>.
/// </summary>
public sealed class GoogleTranslationProvider : ITranslationProvider
{
    public const string ProviderName = "google";

    private const string ModelName = "google-translate-v3";

    private readonly HttpClient _http;
    private readonly GoogleProviderOptions _options;
    private readonly ProviderOptions _providers;

    public GoogleTranslationProvider(HttpClient http, IOptions<GoogleProviderOptions> options, IOptions<ProviderOptions> providers)
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

    public async Task<TranslationResponse> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateConfig();

        var url = string.Concat(TranslateBase(), "/v3/projects/", Uri.EscapeDataString(ProjectId()), "/locations/", Uri.EscapeDataString(_options.Location ?? "global"), ":translateText?key=", Uri.EscapeDataString(EffectiveKey()));
        var payload = new
        {
            contents = new[] { string.Concat("artifact:", request.ArtifactId) },
            sourceLanguage = request.SourceLanguage,
            targetLanguage = request.TargetLanguage,
        };
        var idempotencyKey = ProviderHttpHelper.BuildIdempotencyKey(request.RunId, "translation", request.ArtifactId, 0);

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

        await ProviderHttpHelper.ThrowIfErrorAsync(response, ProviderName, "translateText", cancellationToken).ConfigureAwait(false);
        using var doc = await ProviderHttpHelper.ReadJsonAsync(response, ProviderName, "translateText", cancellationToken).ConfigureAwait(false);
        return Parse(doc);
    }

    internal TranslationResponse Parse(JsonDocument doc)
    {
        var root = doc.RootElement;
        if (!root.TryGetProperty("translations", out var translations) || translations.ValueKind != JsonValueKind.Array || translations.GetArrayLength() == 0)
        {
            throw new ErrorCodeException(ErrorCodes.ProviderInvalidResponse, "Provider 'google' translation returned no translations.");
        }

        var first = translations[0];
        if (!first.TryGetProperty("translatedText", out var textProp) || textProp.ValueKind != JsonValueKind.String)
        {
            throw new ErrorCodeException(ErrorCodes.ProviderInvalidResponse, "Provider 'google' translation returned no translatedText.");
        }

        var primary = textProp.GetString() ?? string.Empty;
        var alternatives = new List<string>();
        if (root.TryGetProperty("alternatives", out var alts) && alts.ValueKind == JsonValueKind.Array)
        {
            foreach (var alt in alts.EnumerateArray())
            {
                if (alt.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(alt.GetString()))
                {
                    alternatives.Add(alt.GetString()!);
                }
            }
        }

        var confidence = first.TryGetProperty("confidence", out var conf) && conf.ValueKind == JsonValueKind.Number
            ? conf.GetDouble()
            : 0.9;

        return new TranslationResponse(
            primary, alternatives, confidence, confidence, confidence, confidence,
            ModelName, null, _options.Location,
            new ProviderUsage(null, null, null, null),
            new Dictionary<string, string>(StringComparer.Ordinal) { ["provider"] = ProviderName });
    }

    private string TranslateBase()
    {
        if (!string.IsNullOrWhiteSpace(_options.TranslateBaseUrl))
        {
            return ProviderEndpointValidator.RequireAllowed(_options.TranslateBaseUrl, nameof(GoogleProviderOptions.TranslateBaseUrl));
        }

        return "https://translation.googleapis.com";
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
}
