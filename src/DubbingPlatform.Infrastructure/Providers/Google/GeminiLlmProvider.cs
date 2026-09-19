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
/// Gemini LLM adapter for context-aware translation. POST
/// <c>{GeminiBase}/v1/models/{model}:generateContent?key={key}</c> with
/// <c>{"contents":[{"parts":[{"text"}]}],"systemInstruction":{"parts":[{"text":glossary prompt}]}}</c>;
/// response <c>{"candidates":[{"content":{"parts":[{"text":"{\"primary\":...}"}]}}]}</c>.
/// Used for translation when <c>Providers:Google:UseGeminiForTranslation=true</c>
/// (routing/descriptors select it); the adapter itself is unconditional.
/// </summary>
public sealed class GeminiLlmProvider : ITranslationProvider
{
    public const string ProviderName = "google";

    private readonly HttpClient _http;
    private readonly GoogleProviderOptions _options;
    private readonly ProviderOptions _providers;

    public GeminiLlmProvider(HttpClient http, IOptions<GoogleProviderOptions> options, IOptions<ProviderOptions> providers)
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

        var url = string.Concat(GeminiBase(), "/v1/models/", Uri.EscapeDataString(_options.GeminiModel ?? "gemini-1.5-flash"), ":generateContent?key=", Uri.EscapeDataString(EffectiveKey()));
        var systemPrompt = string.Concat(
            "Translate ",
            request.SourceLanguage,
            " to ",
            request.TargetLanguage,
            ". Glossary-aware, timing-preserving, JSON only: {\"primary\": string, \"alternatives\": string[], \"confidence\": number}.");
        var body = new
        {
            contents = new object[]
            {
                new { parts = new object[] { new { text = string.Concat("artifact:", request.ArtifactId) } } },
            },
            systemInstruction = new { parts = new object[] { new { text = systemPrompt } } },
        };
        var idempotencyKey = ProviderHttpHelper.BuildIdempotencyKey(request.RunId, "translation", request.ArtifactId, 0);

        using var response = await ProviderHttpHelper.SendAsync(
            _http,
            () =>
            {
                var message = new HttpRequestMessage(HttpMethod.Post, url);
                message.Content = JsonContent.Create(body);
                ProviderHttpHelper.AddIdempotencyKey(message, idempotencyKey);
                return message;
            },
            cancellationToken).ConfigureAwait(false);

        await ProviderHttpHelper.ThrowIfErrorAsync(response, ProviderName, "generateContent", cancellationToken).ConfigureAwait(false);
        using var doc = await ProviderHttpHelper.ReadJsonAsync(response, ProviderName, "generateContent", cancellationToken).ConfigureAwait(false);
        return Parse(doc);
    }

    internal TranslationResponse Parse(JsonDocument doc)
    {
        var root = doc.RootElement;
        if (!root.TryGetProperty("candidates", out var candidates) || candidates.ValueKind != JsonValueKind.Array || candidates.GetArrayLength() == 0)
        {
            throw new ErrorCodeException(ErrorCodes.ProviderInvalidResponse, "Provider 'gemini' returned no candidates.");
        }

        string? text = null;
        var candidate = candidates[0];
        if (candidate.TryGetProperty("content", out var content)
            && content.TryGetProperty("parts", out var parts)
            && parts.ValueKind == JsonValueKind.Array && parts.GetArrayLength() > 0
            && parts[0].TryGetProperty("text", out var textProp))
        {
            text = textProp.GetString();
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ErrorCodeException(ErrorCodes.ProviderInvalidResponse, "Provider 'gemini' returned empty content.");
        }

        try
        {
            using var inner = JsonDocument.Parse(text);
            var innerRoot = inner.RootElement;
            if (innerRoot.ValueKind == JsonValueKind.Object && innerRoot.TryGetProperty("primary", out var primaryProp))
            {
                var primary = primaryProp.GetString() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(primary))
                {
                    throw new ErrorCodeException(ErrorCodes.ProviderInvalidResponse, "Provider 'gemini' returned no primary.");
                }

                var alternatives = new List<string>();
                if (innerRoot.TryGetProperty("alternatives", out var alts) && alts.ValueKind == JsonValueKind.Array)
                {
                    foreach (var alt in alts.EnumerateArray())
                    {
                        if (alt.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(alt.GetString()))
                        {
                            alternatives.Add(alt.GetString()!);
                        }
                    }
                }

                var confidence = innerRoot.TryGetProperty("confidence", out var conf) && conf.ValueKind == JsonValueKind.Number
                    ? conf.GetDouble()
                    : 0.9;

                return new TranslationResponse(
                    primary, alternatives, confidence, confidence, confidence, confidence,
                    _options.GeminiModel ?? "gemini-1.5-flash", null, _options.Location,
                    new ProviderUsage(null, null, null, null),
                    new Dictionary<string, string>(StringComparer.Ordinal) { ["provider"] = "gemini" });
            }
        }
        catch (JsonException)
        {
        }

        return new TranslationResponse(
            text.Trim(), [], 0.9, 0.9, 0.9, 0.9,
            _options.GeminiModel ?? "gemini-1.5-flash", null, _options.Location,
            new ProviderUsage(null, null, null, null),
            new Dictionary<string, string>(StringComparer.Ordinal) { ["provider"] = "gemini" });
    }

    private string GeminiBase()
    {
        if (!string.IsNullOrWhiteSpace(_options.GeminiBaseUrl))
        {
            return ProviderEndpointValidator.RequireAllowed(_options.GeminiBaseUrl, nameof(GoogleProviderOptions.GeminiBaseUrl));
        }

        return "https://generativelanguage.googleapis.com";
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
