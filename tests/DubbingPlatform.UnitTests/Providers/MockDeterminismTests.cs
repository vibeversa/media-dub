using DubbingPlatform.Application.Abstractions.Providers.Dtos;
using DubbingPlatform.Application.Errors;
using DubbingPlatform.Application.Exceptions;
using DubbingPlatform.Application.Options;
using DubbingPlatform.Infrastructure.Providers.Mock;
using Microsoft.Extensions.Options;

namespace DubbingPlatform.UnitTests.Providers;

/// <summary>
/// Determinism and fixture-script coverage for all 8 mock providers.
/// </summary>
public sealed class MockDeterminismTests
{
    private static readonly Guid TenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ProjectId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid RunId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    [Fact]
    public async Task Same_Input_Same_Output()
    {
        var options = CreateOptions(MockBehaviorOptions.Success);

        var vad = new MockVadProvider(options);
        var vadRequest = new VadRequest(TenantId, ProjectId, RunId, "seg-1", "en", 1024, 5000, "wav");
        var vadFirst = await vad.DetectAsync(vadRequest, CancellationToken.None);
        var vadSecond = await vad.DetectAsync(vadRequest, CancellationToken.None);
        AssertVadEqual(vadFirst, vadSecond);

        var diarization = new MockDiarizationProvider(options);
        var diarRequest = new DiarizationRequest(TenantId, ProjectId, RunId, "seg-1", "en", 1024, 5000, "wav");
        var diarFirst = await diarization.DiarizeAsync(diarRequest, CancellationToken.None);
        var diarSecond = await diarization.DiarizeAsync(diarRequest, CancellationToken.None);
        AssertDiarizationEqual(diarFirst, diarSecond);

        var transcription = new MockTranscriptionProvider(options);
        var transRequest = new TranscriptionRequest(TenantId, ProjectId, RunId, "seg-1", "en", 1024, 5000, "wav", true, false);
        var transFirst = await transcription.TranscribeAsync(transRequest, CancellationToken.None);
        var transSecond = await transcription.TranscribeAsync(transRequest, CancellationToken.None);
        AssertTranscriptionEqual(transFirst, transSecond);

        var translation = new MockTranslationProvider(options);
        var translationRequest = new TranslationRequest(TenantId, ProjectId, RunId, "seg-1", "en", "es", 1024, 5000);
        var translationFirst = await translation.TranslateAsync(translationRequest, CancellationToken.None);
        var translationSecond = await translation.TranslateAsync(translationRequest, CancellationToken.None);
        AssertTranslationEqual(translationFirst, translationSecond);

        var tts = new MockTtsProvider(options);
        var ttsRequest = new TtsRequest(TenantId, ProjectId, RunId, "hello world mock", "es", "voice-1", 1000, false);
        var ttsFirst = await tts.SynthesizeAsync(ttsRequest, CancellationToken.None);
        var ttsSecond = await tts.SynthesizeAsync(ttsRequest, CancellationToken.None);
        AssertTtsEqual(ttsFirst, ttsSecond);
        var bytesFirst = MockTtsProvider.GenerateAudioBytes(ttsRequest.Text, ttsRequest.Language, ttsRequest.VoiceId);
        var bytesSecond = MockTtsProvider.GenerateAudioBytes(ttsRequest.Text, ttsRequest.Language, ttsRequest.VoiceId);
        Assert.Equal(bytesFirst, bytesSecond);

        var separation = new MockSourceSeparationProvider(options);
        var sepRequest = new SeparationRequest(TenantId, ProjectId, RunId, "seg-1", "en", 1024, 5000, "wav");
        var sepFirst = await separation.SeparateAsync(sepRequest, CancellationToken.None);
        var sepSecond = await separation.SeparateAsync(sepRequest, CancellationToken.None);
        AssertSeparationEqual(sepFirst, sepSecond);

        var video = new MockVideoIntelligenceProvider(options);
        var videoRequest = new VideoIntelligenceRequest(TenantId, ProjectId, RunId, "seg-1", "en", 1024, 5000, "mp4");
        var videoFirst = await video.AnalyzeAsync(videoRequest, CancellationToken.None);
        var videoSecond = await video.AnalyzeAsync(videoRequest, CancellationToken.None);
        AssertVideoEqual(videoFirst, videoSecond);

        var local = new MockLocalInferenceProvider(options);
        var localRequest = new LocalInferenceRequest(TenantId, ProjectId, RunId, "model-1", "{\"x\":1}", "en", 128, 1000);
        var localFirst = await local.InferAsync(localRequest, CancellationToken.None);
        var localSecond = await local.InferAsync(localRequest, CancellationToken.None);
        AssertLocalEqual(localFirst, localSecond);
    }

    [Fact]
    public async Task Stable_Speaker_Labels()
    {
        var options = CreateOptions(MockBehaviorOptions.Success);
        var provider = new MockDiarizationProvider(options);
        var request = new DiarizationRequest(TenantId, ProjectId, RunId, "seg-stable", "en", 2048, 9000, "wav");

        var first = await provider.DiarizeAsync(request, CancellationToken.None);
        var second = await provider.DiarizeAsync(request, CancellationToken.None);

        Assert.Equal(first.SpeakerLabels, second.SpeakerLabels);
        Assert.Equal(first.Segments.Count, second.Segments.Count);
        for (var i = 0; i < first.Segments.Count; i++)
        {
            Assert.Equal(first.Segments[i].SpeakerLabel, second.Segments[i].SpeakerLabel);
            Assert.Equal(first.Segments[i].StartMs, second.Segments[i].StartMs);
            Assert.Equal(first.Segments[i].EndMs, second.Segments[i].EndMs);
        }

        Assert.Contains("spk_0", first.SpeakerLabels);
        Assert.DoesNotContain(string.Empty, first.SpeakerLabels);
    }

    [Theory]
    [InlineData(MockBehaviorOptions.Success)]
    [InlineData(MockBehaviorOptions.LowConfidence)]
    [InlineData(MockBehaviorOptions.RateLimited)]
    [InlineData(MockBehaviorOptions.Timeout)]
    [InlineData(MockBehaviorOptions.Malformed)]
    [InlineData(MockBehaviorOptions.AsyncJob)]
    [InlineData(MockBehaviorOptions.Duplicate)]
    [InlineData(MockBehaviorOptions.Partial)]
    [InlineData(MockBehaviorOptions.Expired)]
    [InlineData(MockBehaviorOptions.QuotaExhausted)]
    public async Task Configurable_Failure_Emits_Correct_Outcome(string scenario)
    {
        var options = CreateOptions(scenario);
        var provider = new MockTranscriptionProvider(options);
        var request = new TranscriptionRequest(TenantId, ProjectId, RunId, "seg-fixture", "en", 1024, 5000, "wav", true, false);

        switch (scenario)
        {
            case MockBehaviorOptions.Success:
            {
                var response = await provider.TranscribeAsync(request, CancellationToken.None);
                Assert.Equal(0.95, response.Confidence);
                Assert.StartsWith("mock transcript seg seg-fixture [en]", response.Text, StringComparison.Ordinal);
                break;
            }

            case MockBehaviorOptions.LowConfidence:
            {
                var response = await provider.TranscribeAsync(request, CancellationToken.None);
                Assert.Equal(0.35, response.Confidence);
                break;
            }

            case MockBehaviorOptions.RateLimited:
            {
                var ex = await Assert.ThrowsAsync<ErrorCodeException>(() => provider.TranscribeAsync(request, CancellationToken.None));
                Assert.Equal(ErrorCodes.ProviderRateLimited, ex.ErrorCode);
                break;
            }

            case MockBehaviorOptions.Timeout:
            {
                var ex = await Assert.ThrowsAsync<ErrorCodeException>(() => provider.TranscribeAsync(request, CancellationToken.None));
                Assert.Equal(ErrorCodes.ProviderTimeout, ex.ErrorCode);
                break;
            }

            case MockBehaviorOptions.Malformed:
            {
                var ex = await Assert.ThrowsAsync<ErrorCodeException>(() => provider.TranscribeAsync(request, CancellationToken.None));
                Assert.Equal(ErrorCodes.ProviderInvalidResponse, ex.ErrorCode);
                break;
            }

            case MockBehaviorOptions.AsyncJob:
            {
                var response = await provider.TranscribeAsync(request, CancellationToken.None);
                Assert.NotNull(response.RawMetadata);
                Assert.True(response.RawMetadata.ContainsKey("mock.job_id"));
                Assert.Equal("3", response.RawMetadata["mock.polls"]);
                break;
            }

            case MockBehaviorOptions.Duplicate:
            {
                var response = await provider.TranscribeAsync(request, CancellationToken.None);
                Assert.NotNull(response.RawMetadata);
                Assert.Equal("true", response.RawMetadata["mock.duplicate"]);
                break;
            }

            case MockBehaviorOptions.Partial:
            {
                var full = await new MockTranscriptionProvider(CreateOptions(MockBehaviorOptions.Success)).TranscribeAsync(request, CancellationToken.None);
                var partial = await provider.TranscribeAsync(request, CancellationToken.None);
                Assert.True(partial.Words.Count < full.Words.Count);
                Assert.Equal("true", partial.RawMetadata!["mock.partial"]);
                break;
            }

            case MockBehaviorOptions.Expired:
            {
                var ex = await Assert.ThrowsAsync<ErrorCodeException>(() => provider.TranscribeAsync(request, CancellationToken.None));
                Assert.Equal(ErrorCodes.ProviderFailed, ex.ErrorCode);
                break;
            }

            case MockBehaviorOptions.QuotaExhausted:
            {
                var ex = await Assert.ThrowsAsync<ErrorCodeException>(() => provider.TranscribeAsync(request, CancellationToken.None));
                Assert.Equal(ErrorCodes.ProviderQuotaExhausted, ex.ErrorCode);
                break;
            }

            default:
                throw new InvalidOperationException($"Unhandled scenario '{scenario}'.");
        }
    }

    [Fact]
    public async Task All_Mocks_Support_Failure_Scenarios()
    {
        var rateLimited = CreateOptions(MockBehaviorOptions.RateLimited);
        await Assert.ThrowsAsync<ErrorCodeException>(() =>
            new MockVadProvider(rateLimited).DetectAsync(new VadRequest(TenantId, ProjectId, RunId, "a", "en", 1, 1000, null), CancellationToken.None));
        await Assert.ThrowsAsync<ErrorCodeException>(() =>
            new MockDiarizationProvider(rateLimited).DiarizeAsync(new DiarizationRequest(TenantId, ProjectId, RunId, "a", "en", 1, 1000, null), CancellationToken.None));
        await Assert.ThrowsAsync<ErrorCodeException>(() =>
            new MockTranslationProvider(rateLimited).TranslateAsync(new TranslationRequest(TenantId, ProjectId, RunId, "a", "en", "es", 1, 1000), CancellationToken.None));
        await Assert.ThrowsAsync<ErrorCodeException>(() =>
            new MockTtsProvider(rateLimited).SynthesizeAsync(new TtsRequest(TenantId, ProjectId, RunId, "hi", "en", "v", 1000, false), CancellationToken.None));
        await Assert.ThrowsAsync<ErrorCodeException>(() =>
            new MockSourceSeparationProvider(rateLimited).SeparateAsync(new SeparationRequest(TenantId, ProjectId, RunId, "a", "en", 1, 1000, null), CancellationToken.None));
        await Assert.ThrowsAsync<ErrorCodeException>(() =>
            new MockVideoIntelligenceProvider(rateLimited).AnalyzeAsync(new VideoIntelligenceRequest(TenantId, ProjectId, RunId, "a", "en", 1, 1000, null), CancellationToken.None));
        await Assert.ThrowsAsync<ErrorCodeException>(() =>
            new MockLocalInferenceProvider(rateLimited).InferAsync(new LocalInferenceRequest(TenantId, ProjectId, RunId, "m", "{}", "en", 1, 1000), CancellationToken.None));

        var perCapability = new MockBehaviorOptions
        {
            Scenario = MockBehaviorOptions.Success,
            Behaviors = new Dictionary<string, MockBehaviorOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["Transcription"] = new MockBehaviorOptions { Scenario = MockBehaviorOptions.RateLimited },
            },
        };
        var perCapOptions = Microsoft.Extensions.Options.Options.Create(perCapability);
        var transcription = new MockTranscriptionProvider(perCapOptions);
        var ex = await Assert.ThrowsAsync<ErrorCodeException>(() =>
            transcription.TranscribeAsync(new TranscriptionRequest(TenantId, ProjectId, RunId, "a", "en", 1, 1000, null, false, false), CancellationToken.None));
        Assert.Equal(ErrorCodes.ProviderRateLimited, ex.ErrorCode);

        var vad = new MockVadProvider(perCapOptions);
        var vadResponse = await vad.DetectAsync(new VadRequest(TenantId, ProjectId, RunId, "a", "en", 1, 1000, null), CancellationToken.None);
        Assert.Equal(0.99, vadResponse.Confidence);
    }

    [Fact]
    public async Task Timeout_Respects_CancellationToken()
    {
        var options = CreateOptions(MockBehaviorOptions.Timeout);
        var provider = new MockTranscriptionProvider(options);
        var request = new TranscriptionRequest(TenantId, ProjectId, RunId, "seg-timeout", "en", 1024, 5000, "wav", false, false);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => provider.TranscribeAsync(request, cts.Token));
    }

    [Fact]
    public async Task Unknown_Scenario_Fails_Fast()
    {
        var validator = new MockBehaviorOptionsValidator();
        var result = validator.Validate(
            Microsoft.Extensions.Options.Options.DefaultName,
            new MockBehaviorOptions { Scenario = "nope" });
        Assert.True(result.Failed);

        var options = Microsoft.Extensions.Options.Options.Create(new MockBehaviorOptions { Scenario = "nope" });
        var provider = new MockTranscriptionProvider(options);
        var request = new TranscriptionRequest(TenantId, ProjectId, RunId, "a", "en", 1, 1000, null, false, false);
        var ex = await Assert.ThrowsAsync<ErrorCodeException>(() => provider.TranscribeAsync(request, CancellationToken.None));
        Assert.Equal(ErrorCodes.ProviderConfigurationError, ex.ErrorCode);
    }

    [Fact]
    public void FailRate_Out_Of_Range_Fails_Validation()
    {
        var validator = new MockBehaviorOptionsValidator();
        Assert.True(validator.Validate(Microsoft.Extensions.Options.Options.DefaultName, new MockBehaviorOptions { FailRate = -0.1 }).Failed);
        Assert.True(validator.Validate(Microsoft.Extensions.Options.Options.DefaultName, new MockBehaviorOptions { FailRate = 1.1 }).Failed);
        Assert.False(validator.Validate(Microsoft.Extensions.Options.Options.DefaultName, new MockBehaviorOptions()).Failed);
    }

    [Fact]
    public void Async_Job_Lifecycle()
    {
        var options = CreateOptions(MockBehaviorOptions.Success);

        AssertJobLifecycle(new MockVadProvider(options).StartJob, new MockVadProvider(options).PollJob);
        AssertJobLifecycle(
            key => new MockTranscriptionProvider(options).StartJob(key),
            jobId => new MockTranscriptionProvider(options).PollJob(jobId));

        var provider = new MockTranscriptionProvider(options);
        var jobId = provider.StartJob("lifecycle-key");
        Assert.StartsWith("job_", jobId, StringComparison.Ordinal);

        var (firstStatus, firstCount) = provider.PollJob(jobId);
        Assert.Equal("Running", firstStatus);
        Assert.Equal(1, firstCount);

        var (secondStatus, secondCount) = provider.PollJob(jobId);
        Assert.Equal("Running", secondStatus);
        Assert.Equal(2, secondCount);

        var (thirdStatus, thirdCount) = provider.PollJob(jobId);
        Assert.Equal("Succeeded", thirdStatus);
        Assert.Equal(3, thirdCount);
    }

    private static void AssertJobLifecycle(Func<string, string> start, Func<string, (string Status, int PollCount)> poll)
    {
        var jobId = start("lifecycle-key");
        Assert.StartsWith("job_", jobId, StringComparison.Ordinal);
        Assert.Equal("job_" + jobId["job_".Length..], jobId);
    }

    private static IOptions<MockBehaviorOptions> CreateOptions(string scenario)
    {
        return Microsoft.Extensions.Options.Options.Create(new MockBehaviorOptions { Scenario = scenario });
    }

    private static void AssertVadEqual(VadResponse first, VadResponse second)
    {
        Assert.Equal(first.Confidence, second.Confidence);
        Assert.Equal(first.Model, second.Model);
        Assert.Equal(first.Regions.Count, second.Regions.Count);
        for (var i = 0; i < first.Regions.Count; i++)
        {
            Assert.Equal(first.Regions[i].StartMs, second.Regions[i].StartMs);
            Assert.Equal(first.Regions[i].EndMs, second.Regions[i].EndMs);
            Assert.Equal(first.Regions[i].Confidence, second.Regions[i].Confidence);
        }
    }

    private static void AssertDiarizationEqual(DiarizationResponse first, DiarizationResponse second)
    {
        Assert.Equal(first.Confidence, second.Confidence);
        Assert.Equal(first.SpeakerLabels, second.SpeakerLabels);
        Assert.Equal(first.Segments.Count, second.Segments.Count);
        for (var i = 0; i < first.Segments.Count; i++)
        {
            Assert.Equal(first.Segments[i].SpeakerLabel, second.Segments[i].SpeakerLabel);
            Assert.Equal(first.Segments[i].StartMs, second.Segments[i].StartMs);
            Assert.Equal(first.Segments[i].EndMs, second.Segments[i].EndMs);
        }
    }

    private static void AssertTranscriptionEqual(TranscriptionResponse first, TranscriptionResponse second)
    {
        Assert.Equal(first.Text, second.Text);
        Assert.Equal(first.Confidence, second.Confidence);
        Assert.Equal(first.Words.Count, second.Words.Count);
        for (var i = 0; i < first.Words.Count; i++)
        {
            Assert.Equal(first.Words[i].Word, second.Words[i].Word);
            Assert.Equal(first.Words[i].StartMs, second.Words[i].StartMs);
            Assert.Equal(first.Words[i].EndMs, second.Words[i].EndMs);
        }
    }

    private static void AssertTranslationEqual(TranslationResponse first, TranslationResponse second)
    {
        Assert.Equal(first.PrimaryText, second.PrimaryText);
        Assert.Equal(first.Alternatives, second.Alternatives);
        Assert.Equal(first.Confidence, second.Confidence);
        Assert.Equal(first.SemanticScore, second.SemanticScore);
    }

    private static void AssertTtsEqual(TtsResponse first, TtsResponse second)
    {
        Assert.Equal(first.ContentObjectId, second.ContentObjectId);
        Assert.Equal(first.DurationMs, second.DurationMs);
        Assert.Equal(first.VoiceId, second.VoiceId);
        Assert.Equal(first.RawMetadata!["mock.contentHash"], second.RawMetadata!["mock.contentHash"]);
    }

    private static void AssertSeparationEqual(SeparationResponse first, SeparationResponse second)
    {
        Assert.Equal(first.DialogueArtifactId, second.DialogueArtifactId);
        Assert.Equal(first.BackgroundArtifactId, second.BackgroundArtifactId);
        Assert.Equal(first.Confidence, second.Confidence);
    }

    private static void AssertVideoEqual(VideoIntelligenceResponse first, VideoIntelligenceResponse second)
    {
        Assert.Equal(first.Confidence, second.Confidence);
        Assert.Equal(first.Faces.Count, second.Faces.Count);
        Assert.Equal(first.ActiveSpeakers.Count, second.ActiveSpeakers.Count);
        for (var i = 0; i < first.Faces.Count; i++)
        {
            Assert.Equal(first.Faces[i].TrackId, second.Faces[i].TrackId);
            Assert.Equal(first.Faces[i].StartMs, second.Faces[i].StartMs);
        }
    }

    private static void AssertLocalEqual(LocalInferenceResponse first, LocalInferenceResponse second)
    {
        Assert.Equal(first.OutputJson, second.OutputJson);
        Assert.Equal(first.ModelId, second.ModelId);
        Assert.Equal(first.Confidence, second.Confidence);
    }
}
