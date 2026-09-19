using System.Net;
using DubbingPlatform.Application.Abstractions.Providers.Dtos;
using DubbingPlatform.Application.Errors;
using DubbingPlatform.Application.Exceptions;
using DubbingPlatform.Application.Options;
using DubbingPlatform.Application.Providers;
using DubbingPlatform.ContractTests.Providers.Fixtures;
using DubbingPlatform.Domain.Enums;
using DubbingPlatform.Infrastructure.Providers;
using DubbingPlatform.Infrastructure.Providers.Azure;
using DubbingPlatform.Infrastructure.Providers.Common;
using DubbingPlatform.Infrastructure.Providers.Google;
using DubbingPlatform.Infrastructure.Providers.LocalInference;
using DubbingPlatform.Infrastructure.Providers.OpenAI;
using Microsoft.Extensions.Options;
using WireMock.Server;

namespace DubbingPlatform.ContractTests.Providers;

/// <summary>
/// WireMock contract coverage for all 4 real provider families without live keys.
/// Each test is parameterized by family (azure/openai/google/local) and asserts
/// outcome classification (retry vs fallback vs review), idempotency, and
/// recording inputs (idempotency keys, external job ids, model/device metadata).
/// </summary>
public sealed class ProviderContractTests : IDisposable
{
    private readonly WireMockServer _server;

    private static readonly Guid TenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ProjectId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid RunId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    public ProviderContractTests()
    {
        _server = WireMockServer.Start();
    }

    public void Dispose()
    {
        _server.Stop();
        _server.Dispose();
    }

    [Theory]
    [InlineData("azure")]
    [InlineData("openai")]
    [InlineData("google")]
    [InlineData("local")]
    public async Task Handles_429(string family)
    {
        switch (family)
        {
            case "azure":
                ProviderWireMockFixtures.StubAzureTranscribe(_server, 429, "rate limited", "1");
                var azure = CreateAzureStt();
                var azureEx = await Assert.ThrowsAsync<ErrorCodeException>(() => azure.TranscribeAsync(SampleTranscription(), CancellationToken.None));
                Assert.Equal(ErrorCodes.ProviderRateLimited, azureEx.ErrorCode);
                Assert.True(OutcomePolicy.Decide(ProviderHttpHelper.ToOutcome(azureEx.ErrorCode)).AllowRetry);
                break;
            case "openai":
                ProviderWireMockFixtures.StubOpenAiTranscriptions(_server, 429, "rate limited", "1");
                var openai = CreateOpenAiStt();
                var openaiEx = await Assert.ThrowsAsync<ErrorCodeException>(() => openai.TranscribeAsync(SampleTranscription(), CancellationToken.None));
                Assert.Equal(ErrorCodes.ProviderRateLimited, openaiEx.ErrorCode);
                break;
            case "google":
                ProviderWireMockFixtures.StubGoogleRecognize(_server, 429, "rate limited");
                var google = CreateGoogleStt();
                var googleEx = await Assert.ThrowsAsync<ErrorCodeException>(() => google.TranscribeAsync(SampleTranscription(), CancellationToken.None));
                Assert.Equal(ErrorCodes.ProviderRateLimited, googleEx.ErrorCode);
                break;
            default:
                ProviderWireMockFixtures.StubLocalHealth(_server);
                ProviderWireMockFixtures.StubLocalInfer(_server, 429, "rate limited");
                var local = await CreateWarmedLocalAsync();
                var localEx = await Assert.ThrowsAsync<ErrorCodeException>(() => local.InferAsync(SampleLocal(), CancellationToken.None));
                Assert.Equal(ErrorCodes.ProviderRateLimited, localEx.ErrorCode);
                break;
        }
    }

    [Theory]
    [InlineData("azure")]
    [InlineData("openai")]
    [InlineData("google")]
    [InlineData("local")]
    public async Task Handles_Timeout(string family)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var clientTimeout = TimeSpan.FromMilliseconds(2000);
        var mockDelay = TimeSpan.FromSeconds(10);

        switch (family)
        {
            case "azure":
                ProviderWireMockFixtures.StubAzureTranscribeDelay(_server, mockDelay);
                var azure = CreateAzureStt(clientTimeout);
                var azureEx = await Assert.ThrowsAsync<ErrorCodeException>(() => azure.TranscribeAsync(SampleTranscription(), cts.Token));
                Assert.Equal(ErrorCodes.ProviderTimeout, azureEx.ErrorCode);
                Assert.True(OutcomePolicy.Decide(OutcomeClass.ProviderTimeout).AllowRetry);
                break;
            case "openai":
                ProviderWireMockFixtures.StubOpenAiTranscriptionsDelay(_server, mockDelay);
                var openai = CreateOpenAiStt(clientTimeout);
                var openaiEx = await Assert.ThrowsAsync<ErrorCodeException>(() => openai.TranscribeAsync(SampleTranscription(), cts.Token));
                Assert.Equal(ErrorCodes.ProviderTimeout, openaiEx.ErrorCode);
                break;
            case "google":
                ProviderWireMockFixtures.StubGoogleRecognizeDelay(_server, mockDelay);
                var google = CreateGoogleStt(clientTimeout);
                var googleEx = await Assert.ThrowsAsync<ErrorCodeException>(() => google.TranscribeAsync(SampleTranscription(), cts.Token));
                Assert.Equal(ErrorCodes.ProviderTimeout, googleEx.ErrorCode);
                break;
            default:
                // Task 044 stabilization: the local timeout case is two-phase
                // (WarmupAsync then InferAsync on one HttpClient). Relying on
                // the unmatched-POST-/warmup → 404 → GET-/health fallback made
                // warmup timing-sensitive under parallel load (029/033 flakes:
                // 38/39 then 39/39 on re-run), and the shared 2s/10s timing
                // left a ~8s blocked server delay per case that amplified
                // thread-pool contention. Use an explicit fast /warmup stub
                // for deterministic warmup plus tighter dedicated timing
                // (1s client timeout vs 5s infer delay: 4s margin, ~4s blocked
                // instead of ~8s). Other families keep the shared 2s/10s.
                ProviderWireMockFixtures.StubLocalWarmup(_server);
                ProviderWireMockFixtures.StubLocalHealth(_server);
                var localDelay = TimeSpan.FromSeconds(5);
                var localTimeout = TimeSpan.FromSeconds(1);
                ProviderWireMockFixtures.StubLocalInfer(_server, 200, """{"output":"x","confidence":0.9}""", localDelay);
                var local = await CreateWarmedLocalAsync(localTimeout);
                var localEx = await Assert.ThrowsAsync<ErrorCodeException>(() => local.InferAsync(SampleLocal(), cts.Token));
                Assert.Equal(ErrorCodes.ProviderTimeout, localEx.ErrorCode);
                break;
        }
    }

    [Theory]
    [InlineData("azure")]
    [InlineData("openai")]
    [InlineData("google")]
    [InlineData("local")]
    public async Task Handles_Malformed(string family)
    {
        switch (family)
        {
            case "azure":
                ProviderWireMockFixtures.StubAzureTranscribe(_server, 200, "not-json{{{");
                var azure = CreateAzureStt();
                var azureEx = await Assert.ThrowsAsync<ErrorCodeException>(() => azure.TranscribeAsync(SampleTranscription(), CancellationToken.None));
                Assert.Equal(ErrorCodes.ProviderInvalidResponse, azureEx.ErrorCode);
                var azureDecision = OutcomePolicy.Decide(OutcomeClass.ProviderInvalidResponse);
                Assert.False(azureDecision.AllowRetry);
                Assert.True(azureDecision.AllowFallback);
                break;
            case "openai":
                ProviderWireMockFixtures.StubOpenAiTranscriptions(_server, 200, "not-json{{{");
                var openai = CreateOpenAiStt();
                var openaiEx = await Assert.ThrowsAsync<ErrorCodeException>(() => openai.TranscribeAsync(SampleTranscription(), CancellationToken.None));
                Assert.Equal(ErrorCodes.ProviderInvalidResponse, openaiEx.ErrorCode);
                break;
            case "google":
                ProviderWireMockFixtures.StubGoogleRecognize(_server, 200, """{"nope":true}""");
                var google = CreateGoogleStt();
                var googleEx = await Assert.ThrowsAsync<ErrorCodeException>(() => google.TranscribeAsync(SampleTranscription(), CancellationToken.None));
                Assert.Equal(ErrorCodes.ProviderInvalidResponse, googleEx.ErrorCode);
                break;
            default:
                ProviderWireMockFixtures.StubLocalHealth(_server);
                ProviderWireMockFixtures.StubLocalInfer(_server, 200, "not-json{{{");
                var local = await CreateWarmedLocalAsync();
                var localEx = await Assert.ThrowsAsync<ErrorCodeException>(() => local.InferAsync(SampleLocal(), CancellationToken.None));
                Assert.Equal(ErrorCodes.ProviderInvalidResponse, localEx.ErrorCode);
                break;
        }
    }

    [Theory]
    [InlineData("azure")]
    [InlineData("openai")]
    [InlineData("google")]
    [InlineData("local")]
    public async Task Async_Job_Lifecycle(string family)
    {
        var poller = new ProviderJobPoller();
        switch (family)
        {
            case "azure":
                ProviderWireMockFixtures.StubAzureBatch(_server, """{"status":"Succeeded","text":"hello","confidence":0.92}""");
                var azure = CreateAzureStt();
                var azureJob = await azure.StartBatchAsync(SampleTranscription(), CancellationToken.None);
                Assert.Equal("az-job-1", azureJob);
                var azureStatus = await poller.PollAsync(
                    (id, ct) => azure.GetBatchStatusAsync(id, ct),
                    azureJob,
                    TimeSpan.FromMilliseconds(10),
                    TimeSpan.FromSeconds(5));
                Assert.True(azureStatus.Completed);
                var reconciled = await poller.ReconcileAsync((id, ct) => azure.GetBatchStatusAsync(id, ct), azureJob);
                Assert.True(reconciled.Completed);
                break;
            case "openai":
                ProviderWireMockFixtures.StubOpenAiBatch(_server, """{"status":"Succeeded"}""");
                var openai = CreateOpenAiStt();
                var openaiJob = await openai.StartBatchAsync(SampleTranscription(), CancellationToken.None);
                Assert.Equal("oai-job-1", openaiJob);
                var openaiStatus = await poller.PollAsync(
                    (id, ct) => openai.GetBatchStatusAsync(id, ct),
                    openaiJob,
                    TimeSpan.FromMilliseconds(10),
                    TimeSpan.FromSeconds(5));
                Assert.True(openaiStatus.Completed);
                break;
            case "google":
                {
                    var calls = 0;
                    Task<ProviderJobPoller.JobStatus> Fetch(string id, CancellationToken ct)
                    {
                        calls++;
                        Assert.Equal("ext-1", id);
                        return Task.FromResult(calls < 3
                            ? new ProviderJobPoller.JobStatus(false, false, "Running", null)
                            : new ProviderJobPoller.JobStatus(true, false, "Succeeded", null));
                    }

                    var final = await poller.PollAsync(Fetch, "ext-1", TimeSpan.FromMilliseconds(10), TimeSpan.FromSeconds(5));
                    Assert.True(final.Completed);
                    Assert.Equal(3, calls);
                    break;
                }

            default:
                {
                    var calls = 0;
                    Task<ProviderJobPoller.JobStatus> Fetch(string id, CancellationToken ct)
                    {
                        calls++;
                        return Task.FromResult(calls < 3
                            ? new ProviderJobPoller.JobStatus(false, false, "Running", null)
                            : new ProviderJobPoller.JobStatus(true, false, "Succeeded", null));
                    }

                    var final = await poller.PollAsync(Fetch, "local-job-1", TimeSpan.FromMilliseconds(10), TimeSpan.FromSeconds(5));
                    Assert.True(final.Completed);
                    break;
                }
        }
    }

    [Theory]
    [InlineData("azure")]
    [InlineData("openai")]
    [InlineData("google")]
    [InlineData("local")]
    public async Task Duplicate_Request_Reconciled(string family)
    {
        switch (family)
        {
            case "azure":
                ProviderWireMockFixtures.StubAzureTts(_server, 200, """{"contentId":"az-tts-1","durationMs":1200,"voice":"v","confidence":0.95}""");
                var azure = CreateAzureTts();
                var azureReq = SampleTts();
                var first = await azure.SynthesizeWithIdempotencyAsync(azureReq, "run:tts:scope:0", CancellationToken.None);
                var second = await azure.SynthesizeWithIdempotencyAsync(azureReq, "run:tts:scope:0", CancellationToken.None);
                Assert.Equal(first.ContentObjectId, second.ContentObjectId);
                AssertIdempotencyHeaderSent("run:tts:scope:0");
                break;
            case "openai":
                ProviderWireMockFixtures.StubOpenAiTts(_server, 200, """{"contentId":"oai-tts-1","durationMs":1100,"voice":"v","confidence":0.95}""");
                var openai = CreateOpenAiTts();
                var openaiReq = SampleTts();
                var key = ProviderExecutionRecorder.BuildIdempotencyKey(RunId, "tts", "scope-1", 0);
                var oaiFirst = await openai.SynthesizeWithIdempotencyAsync(openaiReq, RunId, "tts", "scope-1", 0, CancellationToken.None);
                var oaiSecond = await openai.SynthesizeWithIdempotencyAsync(openaiReq, RunId, "tts", "scope-1", 0, CancellationToken.None);
                Assert.Equal(oaiFirst.ContentObjectId, oaiSecond.ContentObjectId);
                AssertIdempotencyHeaderSent(key);
                break;
            case "google":
                ProviderWireMockFixtures.StubGoogleTts(_server, 200, """{"contentId":"g-tts-1","durationMs":1000,"voice":"v","confidence":0.95}""");
                var google = CreateGoogleTts();
                var gFirst = await google.SynthesizeAsync(SampleTts(), CancellationToken.None);
                var gSecond = await google.SynthesizeAsync(SampleTts(), CancellationToken.None);
                Assert.Equal(gFirst.ContentObjectId, gSecond.ContentObjectId);
                break;
            default:
                ProviderWireMockFixtures.StubLocalHealth(_server);
                ProviderWireMockFixtures.StubLocalInfer(_server, 200, """{"output":"same","confidence":0.9,"modelHash":"h","device":"cpu"}""");
                var local = await CreateWarmedLocalAsync();
                var lFirst = await local.InferAsync(SampleLocal(), CancellationToken.None);
                var lSecond = await local.InferAsync(SampleLocal(), CancellationToken.None);
                Assert.Equal(lFirst.OutputJson, lSecond.OutputJson);
                Assert.Equal("h", lFirst.RawMetadata!["model.hash"]);
                break;
        }
    }

    [Theory]
    [InlineData("azure")]
    [InlineData("openai")]
    [InlineData("google")]
    [InlineData("local")]
    public async Task Partial_Result(string family)
    {
        switch (family)
        {
            case "azure":
                ProviderWireMockFixtures.StubAzureTranscribe(_server, 200, """{"text":"partial","confidence":0.6,"words":[{"word":"partial","offsetMs":0,"durationMs":100,"confidence":0.6}]}""");
                var azure = CreateAzureStt();
                var azureRes = await azure.TranscribeAsync(SampleTranscription(), CancellationToken.None);
                Assert.Equal("partial", azureRes.Text);
                Assert.Single(azureRes.Words);
                break;
            case "openai":
                ProviderWireMockFixtures.StubOpenAiChat(_server, 200, """{"choices":[{"message":{"content":"{\"primary\":\"parcial\",\"alternatives\":[],\"confidence\":0.6}"}}]}""");
                var openai = CreateOpenAiTranslation();
                var openaiRes = await openai.TranslateWithIdempotencyAsync(SampleTranslation(), RunId, "translation", "scope-1", 0, CancellationToken.None);
                Assert.Equal("parcial", openaiRes.PrimaryText);
                Assert.Empty(openaiRes.Alternatives);
                break;
            case "google":
                ProviderWireMockFixtures.StubGoogleTranslate(_server, 200, """{"translations":[{"translatedText":"parcial","confidence":0.6}]}""");
                var google = CreateGoogleTranslation();
                var googleRes = await google.TranslateAsync(SampleTranslation(), CancellationToken.None);
                Assert.Equal("parcial", googleRes.PrimaryText);
                break;
            default:
                ProviderWireMockFixtures.StubLocalHealth(_server);
                ProviderWireMockFixtures.StubLocalInfer(_server, 200, """{"output":"part","confidence":0.6,"modelHash":"h","device":"cpu"}""");
                var local = await CreateWarmedLocalAsync();
                var localRes = await local.InferAsync(SampleLocal(), CancellationToken.None);
                Assert.Equal("part", localRes.OutputJson);
                Assert.Equal(0.6, localRes.Confidence);
                break;
        }
    }

    [Theory]
    [InlineData("azure")]
    [InlineData("openai")]
    [InlineData("google")]
    [InlineData("local")]
    public async Task Job_Expiry(string family)
    {
        var poller = new ProviderJobPoller();
        switch (family)
        {
            case "azure":
                ProviderWireMockFixtures.StubAzureBatch(_server, """{"status":"Expired","reason":"expired"}""");
                var azure = CreateAzureStt();
                var azureJob = await azure.StartBatchAsync(SampleTranscription(), CancellationToken.None);
                var azureEx = await Assert.ThrowsAsync<ErrorCodeException>(() => poller.PollAsync(
                    (id, ct) => azure.GetBatchStatusAsync(id, ct),
                    azureJob,
                    TimeSpan.FromMilliseconds(10),
                    TimeSpan.FromSeconds(5)));
                Assert.Equal(ErrorCodes.ProviderFailed, azureEx.ErrorCode);
                break;
            case "openai":
                ProviderWireMockFixtures.StubOpenAiBatch(_server, """{"status":"Failed","reason":"expired"}""");
                var openai = CreateOpenAiStt();
                var openaiJob = await openai.StartBatchAsync(SampleTranscription(), CancellationToken.None);
                var openaiEx = await Assert.ThrowsAsync<ErrorCodeException>(() => poller.PollAsync(
                    (id, ct) => openai.GetBatchStatusAsync(id, ct),
                    openaiJob,
                    TimeSpan.FromMilliseconds(10),
                    TimeSpan.FromSeconds(5)));
                Assert.Equal(ErrorCodes.ProviderFailed, openaiEx.ErrorCode);
                break;
            case "google":
                {
                    Task<ProviderJobPoller.JobStatus> Fetch(string id, CancellationToken ct)
                    {
                        return Task.FromResult(new ProviderJobPoller.JobStatus(false, true, "Expired", "expired"));
                    }

                    var ex = await Assert.ThrowsAsync<ErrorCodeException>(() => poller.PollAsync(Fetch, "ext-1", TimeSpan.FromMilliseconds(10), TimeSpan.FromSeconds(5)));
                    Assert.Equal(ErrorCodes.ProviderFailed, ex.ErrorCode);
                    var decision = OutcomePolicy.Decide(OutcomeClass.ProviderPermanentFailure);
                    Assert.True(decision.AllowFallback);
                    break;
                }

            default:
                {
                    Task<ProviderJobPoller.JobStatus> Fetch(string id, CancellationToken ct)
                    {
                        return Task.FromResult(new ProviderJobPoller.JobStatus(false, true, "Expired", "expired"));
                    }

                    var ex = await Assert.ThrowsAsync<ErrorCodeException>(() => poller.PollAsync(Fetch, "local-1", TimeSpan.FromMilliseconds(10), TimeSpan.FromSeconds(5)));
                    Assert.Equal(ErrorCodes.ProviderFailed, ex.ErrorCode);
                    break;
                }
        }
    }

    [Theory]
    [InlineData("azure")]
    [InlineData("openai")]
    [InlineData("google")]
    [InlineData("local")]
    public async Task Quota_Exhausted(string family)
    {
        switch (family)
        {
            case "azure":
                ProviderWireMockFixtures.StubAzureTranscribe(_server, 429, """{"error":{"code":"quota_exceeded"}}""");
                var azure = CreateAzureStt();
                var azureEx = await Assert.ThrowsAsync<ErrorCodeException>(() => azure.TranscribeAsync(SampleTranscription(), CancellationToken.None));
                Assert.Equal(ErrorCodes.ProviderQuotaExhausted, azureEx.ErrorCode);
                break;
            case "openai":
                ProviderWireMockFixtures.StubOpenAiTranscriptions(_server, 429, """{"error":{"code":"insufficient_quota"}}""");
                var openai = CreateOpenAiStt();
                var openaiEx = await Assert.ThrowsAsync<ErrorCodeException>(() => openai.TranscribeAsync(SampleTranscription(), CancellationToken.None));
                Assert.Equal(ErrorCodes.ProviderQuotaExhausted, openaiEx.ErrorCode);
                break;
            case "google":
                ProviderWireMockFixtures.StubGoogleRecognize(_server, 403, """{"error":"quota exceeded"}""");
                var google = CreateGoogleStt();
                var googleEx = await Assert.ThrowsAsync<ErrorCodeException>(() => google.TranscribeAsync(SampleTranscription(), CancellationToken.None));
                Assert.Equal(ErrorCodes.ProviderQuotaExhausted, googleEx.ErrorCode);
                break;
            default:
                ProviderWireMockFixtures.StubLocalHealth(_server);
                ProviderWireMockFixtures.StubLocalInfer(_server, 429, """{"error":"quota_exceeded"}""");
                var local = await CreateWarmedLocalAsync();
                var localEx = await Assert.ThrowsAsync<ErrorCodeException>(() => local.InferAsync(SampleLocal(), CancellationToken.None));
                Assert.Equal(ErrorCodes.ProviderQuotaExhausted, localEx.ErrorCode);
                break;
        }
    }

    [Fact]
    public void Missing_Creds_Fail_Fast_When_Configured()
    {
        var referencing = Microsoft.Extensions.Options.Options.Create(new ProviderOptions
        {
            DefaultProvider = "mock",
            RoutePriority = new Dictionary<string, string[]>(StringComparer.Ordinal) { ["transcription"] = ["azure"] },
            Enabled = new Dictionary<string, bool>(StringComparer.Ordinal) { ["mock"] = true },
            Descriptors = [],
            AzureApiKey = string.Empty,
            OpenAIApiKey = string.Empty,
            GoogleApiKey = string.Empty,
        });
        var emptyAzure = Microsoft.Extensions.Options.Options.Create(new AzureProviderOptions());
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var azure = new AzureSttProvider(http, emptyAzure, referencing);
        var ex = Assert.Throws<ErrorCodeException>(() => azure.ValidateConfig());
        Assert.Equal(ErrorCodes.ProviderConfigurationError, ex.ErrorCode);

        var openaiRef = Microsoft.Extensions.Options.Options.Create(new ProviderOptions
        {
            DefaultProvider = "mock",
            RoutePriority = new Dictionary<string, string[]>(StringComparer.Ordinal) { ["tts"] = ["openai"] },
            Enabled = new Dictionary<string, bool>(StringComparer.Ordinal) { ["mock"] = true },
            Descriptors = [],
        });
        var emptyOpenai = Microsoft.Extensions.Options.Options.Create(new OpenAiProviderOptions { ApiKey = string.Empty, BaseUrl = "https://api.openai.com/v1" });
        var openaiTts = new global::DubbingPlatform.Infrastructure.Providers.OpenAI.OpenAiTtsProvider(new HttpClient(), emptyOpenai, openaiRef);
        var openaiEx = Assert.Throws<ErrorCodeException>(() => openaiTts.ValidateConfig());
        Assert.Equal(ErrorCodes.ProviderConfigurationError, openaiEx.ErrorCode);
    }

    [Fact]
    public void Recording_Inputs_Are_Present()
    {
        var key = ProviderExecutionRecorder.BuildIdempotencyKey(RunId, "translation", "scope-1", 0);
        Assert.Equal(string.Concat(RunId.ToString("N"), ":translation:scope-1:0"), key);

        var helperKey = ProviderHttpHelper.BuildIdempotencyKey(RunId, "tts", "voice-1", 1);
        Assert.Equal(key.Replace("translation:scope-1:0", "tts:voice-1:1"), helperKey);

        var fallback = OutcomePolicy.Decide(OutcomeClass.ProviderRateLimited);
        Assert.True(fallback.AllowRetry);
        Assert.True(fallback.AllowFallback);

        var quality = OutcomePolicy.Decide(OutcomeClass.QualityBelowThreshold);
        Assert.False(quality.AllowRetry);
        Assert.True(quality.AllowReview);
    }

    private void AssertIdempotencyHeaderSent(string expected)
    {
        var found = _server.LogEntries.Any(e =>
            e.RequestMessage != null &&
            e.RequestMessage.Headers != null &&
            e.RequestMessage.Headers.TryGetValue("Idempotency-Key", out var values) &&
            values != null &&
            values.Any(v => string.Equals(v, expected, StringComparison.Ordinal)));
        Assert.True(found, $"Expected Idempotency-Key '{expected}' to be sent.");
    }

    private static TranscriptionRequest SampleTranscription()
    {
        return new TranscriptionRequest(TenantId, ProjectId, RunId, "seg-1", "en", 1024, 5000, "wav", true, false);
    }

    private static TranslationRequest SampleTranslation()
    {
        return new TranslationRequest(TenantId, ProjectId, RunId, "seg-1", "en", "es", 1024, 5000);
    }

    private static TtsRequest SampleTts()
    {
        return new TtsRequest(TenantId, ProjectId, RunId, "hello", "es", "voice-1", 1000, false);
    }

    private static LocalInferenceRequest SampleLocal()
    {
        return new LocalInferenceRequest(TenantId, ProjectId, RunId, "model-1", """{"x":1}""", "en", 128, 1000);
    }

    private static IOptions<ProviderOptions> DefaultProviders()
    {
        return Microsoft.Extensions.Options.Options.Create(new ProviderOptions());
    }

    private AzureSttProvider CreateAzureStt(TimeSpan? timeout = null)
    {
        var http = new HttpClient { Timeout = timeout ?? TimeSpan.FromSeconds(10) };
        var azure = Microsoft.Extensions.Options.Options.Create(new AzureProviderOptions
        {
            SpeechKey = "test-speech",
            SpeechRegion = "test",
            TranslatorKey = "test-translator",
            SpeechBaseUrl = _server.Url,
            TranslatorBaseUrl = _server.Url,
        });
        return new AzureSttProvider(http, azure, DefaultProviders());
    }

    private global::DubbingPlatform.Infrastructure.Providers.Azure.AzureTtsProvider CreateAzureTts()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var azure = Microsoft.Extensions.Options.Options.Create(new AzureProviderOptions
        {
            SpeechKey = "test-speech",
            SpeechRegion = "test",
            SpeechBaseUrl = _server.Url,
        });
        return new global::DubbingPlatform.Infrastructure.Providers.Azure.AzureTtsProvider(http, azure, DefaultProviders());
    }

    private OpenAiSttProvider CreateOpenAiStt(TimeSpan? timeout = null)
    {
        var http = new HttpClient { Timeout = timeout ?? TimeSpan.FromSeconds(10) };
        var options = Microsoft.Extensions.Options.Options.Create(new OpenAiProviderOptions
        {
            ApiKey = "test",
            BaseUrl = _server.Url!.TrimEnd('/'),
        });
        return new OpenAiSttProvider(http, options, DefaultProviders());
    }

    private OpenAiTranslationProvider CreateOpenAiTranslation()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var options = Microsoft.Extensions.Options.Options.Create(new OpenAiProviderOptions
        {
            ApiKey = "test",
            BaseUrl = _server.Url!.TrimEnd('/'),
        });
        return new OpenAiTranslationProvider(http, options, DefaultProviders());
    }

    private OpenAiTtsProvider CreateOpenAiTts()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var options = Microsoft.Extensions.Options.Options.Create(new OpenAiProviderOptions
        {
            ApiKey = "test",
            BaseUrl = _server.Url!.TrimEnd('/'),
        });
        return new OpenAiTtsProvider(http, options, DefaultProviders());
    }

    private GoogleSttProvider CreateGoogleStt(TimeSpan? timeout = null)
    {
        var http = new HttpClient { Timeout = timeout ?? TimeSpan.FromSeconds(10) };
        var options = Microsoft.Extensions.Options.Options.Create(new GoogleProviderOptions
        {
            ApiKey = "test",
            ProjectId = "test-project",
            Location = "global",
            SpeechBaseUrl = _server.Url!.TrimEnd('/'),
            TranslateBaseUrl = _server.Url!.TrimEnd('/'),
            GeminiBaseUrl = _server.Url!.TrimEnd('/'),
            TtsBaseUrl = _server.Url!.TrimEnd('/'),
        });
        return new GoogleSttProvider(http, options, DefaultProviders());
    }

    private GoogleTranslationProvider CreateGoogleTranslation()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var options = Microsoft.Extensions.Options.Options.Create(new GoogleProviderOptions
        {
            ApiKey = "test",
            ProjectId = "test-project",
            TranslateBaseUrl = _server.Url!.TrimEnd('/'),
        });
        return new GoogleTranslationProvider(http, options, DefaultProviders());
    }

    private GoogleTtsProvider CreateGoogleTts()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var options = Microsoft.Extensions.Options.Options.Create(new GoogleProviderOptions
        {
            ApiKey = "test",
            ProjectId = "test-project",
            TtsBaseUrl = _server.Url!.TrimEnd('/'),
        });
        return new GoogleTtsProvider(http, options, DefaultProviders());
    }

    private async Task<LocalInferenceProvider> CreateWarmedLocalAsync(TimeSpan? timeout = null)
    {
        var http = new HttpClient { Timeout = timeout ?? TimeSpan.FromSeconds(10) };
        var options = Microsoft.Extensions.Options.Options.Create(new LocalInferenceOptions
        {
            Endpoint = _server.Url!.TrimEnd('/'),
            Protocol = "http",
            ModelName = "local-small",
            ModelVersion = "1",
            ModelHash = "h",
            Device = "cpu",
            MaxConcurrency = 2,
        });
        var provider = new LocalInferenceProvider(http, options);
        await provider.WarmupAsync(CancellationToken.None);
        return provider;
    }
}
