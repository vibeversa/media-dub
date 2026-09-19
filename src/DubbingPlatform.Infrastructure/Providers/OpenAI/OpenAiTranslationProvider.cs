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
/// OpenAI chat translation adapter. POST <c>{BaseUrl}/chat/completions</c> with a
/// glossary system prompt (<c>Translate {src}→{tgt}, glossary-aware, JSON only</c>)
/// and the artifact reference as user content; response is the chat shape
/// <c>{"choices":[{"message":{"content":"..."}}]}</c> where content is JSON
/// <c>{"primary","alternatives":[],"confidence"}</c> (plain text content is
/// accepted as primary with no alternatives). Sends <c>Idempotency-Key</c>
/// <c>{run:N}:{stage}:{scope}:{attempt}</c> derived from the run/scope.
/// </summary>
public sealed class OpenAiTranslationProvider : ITranslationProvider
{
    public const string ProviderName = "openai";

    private readonly HttpClient _http;
    private readonly OpenAiProviderOptions _options;
    private readonly ProviderOptions _providers;

    public OpenAiTranslationProvider(HttpClient http, IOptions<OpenAiProviderOptions> options, IOptions<ProviderOptions> providers)
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

    public Task<TranslationResponse> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken)
    {
        return TranslateWithIdempotencyAsync(request, request.RunId, "translation", request.ArtifactId, 0, cancellationToken);
    }

    /// <summary>
    /// Translation with an explicit idempotency scope/attempt (see recorder).
    /// </summary>
    public async Task<TranslationResponse> TranslateWithIdempotencyAsync(
        TranslationRequest request,
        Guid runId,
        string stage,
        string scope,
        int attempt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateConfig();

        var url = string.Concat(BaseUrl(), "/chat/completions");
        var systemPrompt = string.Concat(
            "Translate ",
            request.SourceLanguage,
            " to ",
            request.TargetLanguage,
            ". Glossary-aware, timing-preserving, JSON only: {\"primary\": string, \"alternatives\": string[], \"confidence\": number}.");
        var body = new
        {
            model = _options.ChatModel,
            messages = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = string.Concat("artifact:", request.ArtifactId) },
            },
            temperature = 0.2,
        };
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

        await ProviderHttpHelper.ThrowIfErrorAsync(response, ProviderName, "chat-completions", cancellationToken).ConfigureAwait(false);
        using var doc = await ProviderHttpHelper.ReadJsonAsync(response, ProviderName, "chat-completions", cancellationToken).ConfigureAwait(false);
        return Parse(doc);
    }

    internal TranslationResponse Parse(JsonDocument doc)
    {
        var root = doc.RootElement;
        if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
        {
            throw new ErrorCodeException(ErrorCodes.ProviderInvalidResponse, "Provider 'openai' translation returned no choices.");
        }

        var content = choices[0].TryGetProperty("message", out var message) && message.TryGetProperty("content", out var c)
            ? c.GetString() ?? string.Empty
            : string.Empty;
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new ErrorCodeException(ErrorCodes.ProviderInvalidResponse, "Provider 'openai' translation returned empty content.");
        }

        try
        {
            using var inner = JsonDocument.Parse(content);
            var innerRoot = inner.RootElement;
            if (innerRoot.ValueKind == JsonValueKind.Object && innerRoot.TryGetProperty("primary", out var primaryProp))
            {
                var primary = primaryProp.GetString() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(primary))
                {
                    throw new ErrorCodeException(ErrorCodes.ProviderInvalidResponse, "Provider 'openai' translation returned no primary.");
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
                    _options.ChatModel, null, null,
                    new ProviderUsage(null, null, null, null),
                    new Dictionary<string, string>(StringComparer.Ordinal) { ["provider"] = ProviderName });
            }
        }
        catch (JsonException)
        {
        }

        return new TranslationResponse(
            content.Trim(), [], 0.9, 0.9, 0.9, 0.9,
            _options.ChatModel, null, null,
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
