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
/// Azure Translator Text adapter. POST
/// <c>{TranslatorBase}/translate?api-version=3.0&amp;from={src}&amp;to={tgt}</c> with
/// header <c>Ocp-Apim-Subscription-Key</c> and body <c>[{"Text":"..."}]</c>;
/// response <c>[{"translations":[{"text","to"}],"alternatives":[]}]</c>.
/// Source text is the artifact reference (<c>artifact:{artifactId}</c>) because
/// the DTO carries an artifact id; workers resolve content before calling.
/// </summary>
public sealed class AzureTranslationProvider : ITranslationProvider
{
    public const string ProviderName = "azure";

    private const string ModelName = "azure-translator-1";

    private readonly HttpClient _http;
    private readonly AzureProviderOptions _azure;
    private readonly ProviderOptions _providers;

    public AzureTranslationProvider(HttpClient http, IOptions<AzureProviderOptions> azure, IOptions<ProviderOptions> providers)
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

        if (string.IsNullOrWhiteSpace(EffectiveTranslatorKey()))
        {
            throw new ErrorCodeException(
                ErrorCodes.ProviderConfigurationError,
                "Providers:Azure:TranslatorKey is required when 'azure' is enabled or routed.");
        }
    }

    public async Task<TranslationResponse> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateConfig();

        var baseUrl = TranslatorBase();
        var url = string.Concat(
            baseUrl,
            "/translate?api-version=",
            Uri.EscapeDataString(_azure.ApiVersion ?? "3.0"),
            "&from=",
            Uri.EscapeDataString(request.SourceLanguage ?? string.Empty),
            "&to=",
            Uri.EscapeDataString(request.TargetLanguage ?? string.Empty));
        var sourceText = string.Concat("artifact:", request.ArtifactId);
        var body = new[] { new { Text = sourceText } };
        var idempotencyKey = ProviderHttpHelper.BuildIdempotencyKey(request.RunId, "translation", request.ArtifactId, 0);

        using var response = await ProviderHttpHelper.SendAsync(
            _http,
            () =>
            {
                var message = new HttpRequestMessage(HttpMethod.Post, url);
                message.Content = JsonContent.Create(body);
                message.Headers.TryAddWithoutValidation("Ocp-Apim-Subscription-Key", EffectiveTranslatorKey());
                ProviderHttpHelper.AddIdempotencyKey(message, idempotencyKey);
                if (!string.IsNullOrWhiteSpace(_azure.TranslatorRegion))
                {
                    message.Headers.TryAddWithoutValidation("Ocp-Apim-Subscription-Region", _azure.TranslatorRegion.Trim());
                }

                return message;
            },
            cancellationToken).ConfigureAwait(false);

        await ProviderHttpHelper.ThrowIfErrorAsync(response, ProviderName, "translate", cancellationToken).ConfigureAwait(false);
        using var doc = await ProviderHttpHelper.ReadJsonAsync(response, ProviderName, "translate", cancellationToken).ConfigureAwait(false);
        return Parse(doc);
    }

    internal TranslationResponse Parse(JsonDocument doc)
    {
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0)
        {
            throw new ErrorCodeException(ErrorCodes.ProviderInvalidResponse, "Provider 'azure' translation returned an empty body.");
        }

        var first = root[0];
        if (!first.TryGetProperty("translations", out var translations) || translations.ValueKind != JsonValueKind.Array || translations.GetArrayLength() == 0)
        {
            throw new ErrorCodeException(ErrorCodes.ProviderInvalidResponse, "Provider 'azure' translation returned no translations.");
        }

        var primaryElement = translations[0];
        if (!primaryElement.TryGetProperty("text", out var textProp) || textProp.ValueKind != JsonValueKind.String)
        {
            throw new ErrorCodeException(ErrorCodes.ProviderInvalidResponse, "Provider 'azure' translation returned no text.");
        }

        var primary = textProp.GetString() ?? string.Empty;
        var alternatives = new List<string>();
        for (var i = 1; i < translations.GetArrayLength(); i++)
        {
            if (translations[i].TryGetProperty("text", out var alt) && alt.ValueKind == JsonValueKind.String)
            {
                var value = alt.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    alternatives.Add(value!);
                }
            }
        }

        if (first.TryGetProperty("alternatives", out var alts) && alts.ValueKind == JsonValueKind.Array)
        {
            foreach (var alt in alts.EnumerateArray())
            {
                if (alt.ValueKind == JsonValueKind.String)
                {
                    var value = alt.GetString();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        alternatives.Add(value!);
                    }
                }
            }
        }

        var confidence = first.TryGetProperty("confidence", out var conf) && conf.ValueKind == JsonValueKind.Number
            ? conf.GetDouble()
            : 0.9;

        return new TranslationResponse(
            primary, alternatives, confidence, confidence, confidence, confidence,
            ModelName, null, _azure.TranslatorRegion,
            new ProviderUsage(null, null, null, null),
            new Dictionary<string, string>(StringComparer.Ordinal) { ["provider"] = ProviderName });
    }

    private string TranslatorBase()
    {
        if (!string.IsNullOrWhiteSpace(_azure.TranslatorBaseUrl))
        {
            return ProviderEndpointValidator.RequireAllowed(_azure.TranslatorBaseUrl, nameof(AzureProviderOptions.TranslatorBaseUrl));
        }

        return "https://api.cognitive.microsofttranslator.com";
    }

    private string EffectiveTranslatorKey()
    {
        if (!string.IsNullOrWhiteSpace(_azure.TranslatorKey))
        {
            return _azure.TranslatorKey;
        }

        return _providers.AzureApiKey ?? string.Empty;
    }
}
