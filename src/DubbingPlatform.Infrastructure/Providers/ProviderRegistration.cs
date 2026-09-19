using DubbingPlatform.Application.Abstractions.Providers;
using DubbingPlatform.Application.Options;
using DubbingPlatform.Application.Providers;
using DubbingPlatform.Infrastructure.Providers.Mock;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using Polly;
using StackExchange.Redis;

namespace DubbingPlatform.Infrastructure.Providers;

/// <summary>
/// Provider wiring: health tracker (memory + optional Redis mirror), descriptor
/// store, policy loader, cost-gate adapter, resolver, recorder, deterministic
/// mocks (default for local/CI) plus real Azure/OpenAI/Google/Local adapters
/// (selected via <c>Providers:RoutePriority</c>), job poller, and the shared
/// <c>providers</c> HttpClient with standard resilience (retry exponential
/// backoff + jitter for 429/5xx/timeouts only, breaker, 30s total timeout).
/// Missing credentials for enabled or routed non-mock cloud providers fail fast
/// at startup via <see cref="ProviderStartupValidator"/> (mock/local need none).
/// </summary>
public static class ProviderRegistration
{
    public const string HttpClientName = "providers";

    public static void AddDubbingProviders(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var redisConnection = configuration["Redis:Connection"] ?? configuration.GetConnectionString("Redis");
        if (!string.IsNullOrWhiteSpace(redisConnection))
        {
            try
            {
                services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(redisConnection));
            }
            catch (Exception)
            {
                // Redis is optional for health mirroring; memory remains authoritative.
            }
        }

        services.AddSingleton<IProviderHealthTracker>(provider =>
        {
            var providers = provider.GetRequiredService<IOptions<ProviderOptions>>();
            var retry = provider.GetRequiredService<IOptions<RetryOptions>>();
            var redis = provider.GetService(typeof(IConnectionMultiplexer)) as IConnectionMultiplexer;
            var azure = provider.GetService(typeof(IOptions<AzureProviderOptions>)) as IOptions<AzureProviderOptions>;
            var openai = provider.GetService(typeof(IOptions<OpenAiProviderOptions>)) as IOptions<OpenAiProviderOptions>;
            var google = provider.GetService(typeof(IOptions<GoogleProviderOptions>)) as IOptions<GoogleProviderOptions>;
            return new ProviderHealthTracker(providers, retry, redis, azure, openai, google);
        });
        services.AddSingleton<IDescriptorStore, DescriptorStore>();
        services.AddSingleton<IProcessingPolicyProvider, ProcessingPolicyProvider>();
        services.AddSingleton<IProviderCostGate>(provider =>
        {
            var inner = provider.GetService(typeof(Orchestration.ICostGate)) as Orchestration.ICostGate;
            return inner is null
                ? new AllowAllProviderCostGate()
                : new ProviderCostGateAdapter(inner);
        });
        services.AddSingleton<ProviderResolver>();
        services.AddSingleton<LocalInference.ModelRegistry>();
        services.AddScoped<ProviderExecutionRecorder>();
        services.AddSingleton<ProviderJobPoller>();
        services.AddHttpClient(HttpClientName)
            .AddStandardResilienceHandler(options =>
            {
                options.Retry.MaxRetryAttempts = 3;
                options.Retry.BackoffType = DelayBackoffType.Exponential;
                options.Retry.UseJitter = true;
                options.Retry.ShouldHandle = args => new ValueTask<bool>(HttpClientResiliencePredicates.IsTransient(args.Outcome));
                options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(30);
                options.CircuitBreaker.MinimumThroughput = 5;
                options.CircuitBreaker.BreakDuration = TimeSpan.FromSeconds(30);
                options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(30);
            });
        services.AddHttpClient(LocalInference.LocalInferenceProvider.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(provider =>
            {
                var handler = new HttpClientHandler();
                var options = provider.GetService(typeof(IOptions<LocalInferenceOptions>)) as IOptions<LocalInferenceOptions>;
                var local = options?.Value;
                if (local is not null && !string.IsNullOrWhiteSpace(local.ClientCertificatePath))
                {
                    try
                    {
                        var cert = string.IsNullOrWhiteSpace(local.ClientCertificatePassword)
                            ? System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadCertificateFromFile(local.ClientCertificatePath.Trim())
                            : System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadPkcs12FromFile(
                                local.ClientCertificatePath.Trim(),
                                local.ClientCertificatePassword);
                        handler.ClientCertificates.Add(cert);
                    }
                    catch (Exception ex)
                    {
                        throw new InvalidOperationException(
                            $"LocalInference:ClientCertificatePath '{local.ClientCertificatePath}' could not be loaded for mTLS.", ex);
                    }
                }

                return handler;
            })
            .AddStandardResilienceHandler(options =>
            {
                options.Retry.MaxRetryAttempts = 3;
                options.Retry.BackoffType = DelayBackoffType.Exponential;
                options.Retry.UseJitter = true;
                options.Retry.ShouldHandle = args => new ValueTask<bool>(HttpClientResiliencePredicates.IsTransient(args.Outcome));
                options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(30);
            });
        services.AddHostedService<ProviderStartupValidator>();

        // Deterministic mocks are the default (Providers:DefaultProvider=mock).
        // They stay registered as the default (non-keyed) interface mappings so
        // existing resolution keeps working; real adapters register as concrete
        // singletons plus keyed mappings ("azure","openai","google","gemini","local")
        // for resolver-driven selection and explicit fallback without DI churn.
        services.AddSingleton<IVadProvider, MockVadProvider>();
        services.AddSingleton<IDiarizationProvider, MockDiarizationProvider>();
        services.AddSingleton<ITranscriptionProvider, MockTranscriptionProvider>();
        services.AddSingleton<ITranslationProvider, MockTranslationProvider>();
        services.AddSingleton<ITtsProvider, MockTtsProvider>();
        services.AddSingleton<ISourceSeparationProvider, MockSourceSeparationProvider>();
        services.AddSingleton<IVideoIntelligenceProvider, MockVideoIntelligenceProvider>();
        services.AddSingleton<ILipSyncProvider, MockLipSyncProvider>();
        services.AddSingleton<ILocalInferenceProvider, MockLocalInferenceProvider>();

        services.AddSingleton<Azure.AzureSttProvider>(provider => CreateAzureStt(provider));
        services.AddSingleton<Azure.AzureTranslationProvider>(provider => CreateAzureTranslation(provider));
        services.AddSingleton<Azure.AzureTtsProvider>(provider => CreateAzureTts(provider));
        services.AddSingleton<OpenAI.OpenAiSttProvider>(provider => CreateOpenAiStt(provider));
        services.AddSingleton<OpenAI.OpenAiTranslationProvider>(provider => CreateOpenAiTranslation(provider));
        services.AddSingleton<OpenAI.OpenAiTtsProvider>(provider => CreateOpenAiTts(provider));
        services.AddSingleton<Google.GoogleSttProvider>(provider => CreateGoogleStt(provider));
        services.AddSingleton<Google.GoogleTranslationProvider>(provider => CreateGoogleTranslation(provider));
        services.AddSingleton<Google.GeminiLlmProvider>(provider => CreateGemini(provider));
        services.AddSingleton<Google.GoogleTtsProvider>(provider => CreateGoogleTts(provider));
        services.AddSingleton<LocalInference.LocalInferenceProvider>(provider => CreateLocal(provider));

        services.AddKeyedSingleton<ITranscriptionProvider, Azure.AzureSttProvider>("azure", (provider, _) => provider.GetRequiredService<Azure.AzureSttProvider>());
        services.AddKeyedSingleton<IDiarizationProvider, Azure.AzureSttProvider>("azure", (provider, _) => provider.GetRequiredService<Azure.AzureSttProvider>());
        services.AddKeyedSingleton<ITranslationProvider, Azure.AzureTranslationProvider>("azure", (provider, _) => provider.GetRequiredService<Azure.AzureTranslationProvider>());
        services.AddKeyedSingleton<ITtsProvider, Azure.AzureTtsProvider>("azure", (provider, _) => provider.GetRequiredService<Azure.AzureTtsProvider>());
        services.AddKeyedSingleton<ITranscriptionProvider, OpenAI.OpenAiSttProvider>("openai", (provider, _) => provider.GetRequiredService<OpenAI.OpenAiSttProvider>());
        services.AddKeyedSingleton<ITranslationProvider, OpenAI.OpenAiTranslationProvider>("openai", (provider, _) => provider.GetRequiredService<OpenAI.OpenAiTranslationProvider>());
        services.AddKeyedSingleton<ITtsProvider, OpenAI.OpenAiTtsProvider>("openai", (provider, _) => provider.GetRequiredService<OpenAI.OpenAiTtsProvider>());
        services.AddKeyedSingleton<ITranscriptionProvider, Google.GoogleSttProvider>("google", (provider, _) => provider.GetRequiredService<Google.GoogleSttProvider>());
        services.AddKeyedSingleton<ITranslationProvider, Google.GoogleTranslationProvider>("google", (provider, _) => provider.GetRequiredService<Google.GoogleTranslationProvider>());
        services.AddKeyedSingleton<ITranslationProvider, Google.GeminiLlmProvider>("gemini", (provider, _) => provider.GetRequiredService<Google.GeminiLlmProvider>());
        services.AddKeyedSingleton<ITtsProvider, Google.GoogleTtsProvider>("google", (provider, _) => provider.GetRequiredService<Google.GoogleTtsProvider>());
        services.AddKeyedSingleton<ILocalInferenceProvider, LocalInference.LocalInferenceProvider>("local", (provider, _) => provider.GetRequiredService<LocalInference.LocalInferenceProvider>());
        services.AddKeyedSingleton<ITranscriptionProvider, LocalInference.LocalInferenceProvider>("local", (provider, _) => provider.GetRequiredService<LocalInference.LocalInferenceProvider>());
        services.AddKeyedSingleton<ITranslationProvider, LocalInference.LocalInferenceProvider>("local", (provider, _) => provider.GetRequiredService<LocalInference.LocalInferenceProvider>());
        services.AddKeyedSingleton<ITtsProvider, LocalInference.LocalInferenceProvider>("local", (provider, _) => provider.GetRequiredService<LocalInference.LocalInferenceProvider>());
    }

    private static HttpClient CreateProviderClient(IServiceProvider provider)
    {
        var factory = provider.GetRequiredService<IHttpClientFactory>();
        var client = factory.CreateClient(HttpClientName);
        client.Timeout = TimeSpan.FromSeconds(30);
        return client;
    }

    private static Azure.AzureSttProvider CreateAzureStt(IServiceProvider provider)
    {
        return new Azure.AzureSttProvider(
            CreateProviderClient(provider),
            provider.GetRequiredService<IOptions<AzureProviderOptions>>(),
            provider.GetRequiredService<IOptions<ProviderOptions>>());
    }

    private static Azure.AzureTranslationProvider CreateAzureTranslation(IServiceProvider provider)
    {
        return new Azure.AzureTranslationProvider(
            CreateProviderClient(provider),
            provider.GetRequiredService<IOptions<AzureProviderOptions>>(),
            provider.GetRequiredService<IOptions<ProviderOptions>>());
    }

    private static Azure.AzureTtsProvider CreateAzureTts(IServiceProvider provider)
    {
        return new Azure.AzureTtsProvider(
            CreateProviderClient(provider),
            provider.GetRequiredService<IOptions<AzureProviderOptions>>(),
            provider.GetRequiredService<IOptions<ProviderOptions>>());
    }

    private static OpenAI.OpenAiSttProvider CreateOpenAiStt(IServiceProvider provider)
    {
        return new OpenAI.OpenAiSttProvider(
            CreateProviderClient(provider),
            provider.GetRequiredService<IOptions<OpenAiProviderOptions>>(),
            provider.GetRequiredService<IOptions<ProviderOptions>>());
    }

    private static OpenAI.OpenAiTranslationProvider CreateOpenAiTranslation(IServiceProvider provider)
    {
        return new OpenAI.OpenAiTranslationProvider(
            CreateProviderClient(provider),
            provider.GetRequiredService<IOptions<OpenAiProviderOptions>>(),
            provider.GetRequiredService<IOptions<ProviderOptions>>());
    }

    private static OpenAI.OpenAiTtsProvider CreateOpenAiTts(IServiceProvider provider)
    {
        return new OpenAI.OpenAiTtsProvider(
            CreateProviderClient(provider),
            provider.GetRequiredService<IOptions<OpenAiProviderOptions>>(),
            provider.GetRequiredService<IOptions<ProviderOptions>>());
    }

    private static Google.GoogleSttProvider CreateGoogleStt(IServiceProvider provider)
    {
        return new Google.GoogleSttProvider(
            CreateProviderClient(provider),
            provider.GetRequiredService<IOptions<GoogleProviderOptions>>(),
            provider.GetRequiredService<IOptions<ProviderOptions>>());
    }

    private static Google.GoogleTranslationProvider CreateGoogleTranslation(IServiceProvider provider)
    {
        return new Google.GoogleTranslationProvider(
            CreateProviderClient(provider),
            provider.GetRequiredService<IOptions<GoogleProviderOptions>>(),
            provider.GetRequiredService<IOptions<ProviderOptions>>());
    }

    private static Google.GeminiLlmProvider CreateGemini(IServiceProvider provider)
    {
        return new Google.GeminiLlmProvider(
            CreateProviderClient(provider),
            provider.GetRequiredService<IOptions<GoogleProviderOptions>>(),
            provider.GetRequiredService<IOptions<ProviderOptions>>());
    }

    private static Google.GoogleTtsProvider CreateGoogleTts(IServiceProvider provider)
    {
        return new Google.GoogleTtsProvider(
            CreateProviderClient(provider),
            provider.GetRequiredService<IOptions<GoogleProviderOptions>>(),
            provider.GetRequiredService<IOptions<ProviderOptions>>());
    }

    private static LocalInference.LocalInferenceProvider CreateLocal(IServiceProvider provider)
    {
        var factory = provider.GetRequiredService<IHttpClientFactory>();
        var client = factory.CreateClient(LocalInference.LocalInferenceProvider.HttpClientName);
        client.Timeout = TimeSpan.FromSeconds(30);
        return new LocalInference.LocalInferenceProvider(
            client,
            provider.GetRequiredService<IOptions<LocalInferenceOptions>>(),
            provider.GetService(typeof(LocalInference.ModelRegistry)) as LocalInference.ModelRegistry);
    }

    private sealed class AllowAllProviderCostGate : IProviderCostGate
    {
        public Task<bool> CanProceedAsync(Guid tenantId, Domain.Enums.ProviderCapability capability, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(true);
        }
    }
}

/// <summary>
/// Startup fail-fast: enabled or routed Azure/OpenAI/Google providers without
/// API keys prevent boot (mock/local exempt). Checks both the legacy
/// <c>Providers:{AzureApiKey,OpenAIApiKey,GoogleApiKey}</c> and the new
/// <c>Azure/OpenAI/Google</c> sections (either satisfies). Credentials come from
/// configuration (env/secret manager); they never appear in logs or hashes.
/// </summary>
public sealed class ProviderStartupValidator : IHostedService
{
    private readonly IOptions<ProviderOptions> _options;
    private readonly IOptions<AzureProviderOptions> _azure;
    private readonly IOptions<OpenAiProviderOptions> _openai;
    private readonly IOptions<GoogleProviderOptions> _google;

    public ProviderStartupValidator(
        IOptions<ProviderOptions> options,
        IOptions<AzureProviderOptions> azure,
        IOptions<OpenAiProviderOptions> openai,
        IOptions<GoogleProviderOptions> google)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(azure);
        ArgumentNullException.ThrowIfNull(openai);
        ArgumentNullException.ThrowIfNull(google);
        _options = options;
        _azure = azure;
        _openai = openai;
        _google = google;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var options = _options.Value;
        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (options.Enabled is not null)
        {
            foreach (var pair in options.Enabled)
            {
                if (pair.Value && !string.IsNullOrWhiteSpace(pair.Key))
                {
                    referenced.Add(pair.Key.Trim());
                }
            }
        }

        if (options.RoutePriority is not null)
        {
            foreach (var pair in options.RoutePriority)
            {
                foreach (var entry in pair.Value ?? [])
                {
                    if (!string.IsNullOrWhiteSpace(entry))
                    {
                        referenced.Add(entry.Trim());
                    }
                }
            }
        }

        RequireKey(referenced, "azure", EffectiveAzureKey(options, _azure.Value), "Azure:SpeechKey");
        RequireKey(referenced, "openai", EffectiveOpenAiKey(options, _openai.Value), "OpenAI:ApiKey");
        if (referenced.Contains("google") || referenced.Contains("gemini"))
        {
            RequireKey(referenced, "google", EffectiveGoogleKey(options, _google.Value), "Google:ApiKey");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    private static void RequireKey(HashSet<string> referenced, string name, string key, string property)
    {
        if (referenced.Contains(name) && string.IsNullOrWhiteSpace(key))
        {
            throw new InvalidOperationException(
                $"Providers:{property} is required when '{name}' is enabled or routed. Set it via environment or secret manager.");
        }
    }

    private static string EffectiveAzureKey(ProviderOptions legacy, AzureProviderOptions azure)
    {
        if (!string.IsNullOrWhiteSpace(azure.SpeechKey) || !string.IsNullOrWhiteSpace(azure.TranslatorKey))
        {
            return string.Concat(azure.SpeechKey, azure.TranslatorKey);
        }

        return legacy.AzureApiKey ?? string.Empty;
    }

    private static string EffectiveOpenAiKey(ProviderOptions legacy, OpenAiProviderOptions openai)
    {
        if (!string.IsNullOrWhiteSpace(openai.ApiKey))
        {
            return openai.ApiKey;
        }

        return legacy.OpenAIApiKey ?? string.Empty;
    }

    private static string EffectiveGoogleKey(ProviderOptions legacy, GoogleProviderOptions google)
    {
        if (!string.IsNullOrWhiteSpace(google.ApiKey))
        {
            return google.ApiKey;
        }

        return legacy.GoogleApiKey ?? string.Empty;
    }
}
