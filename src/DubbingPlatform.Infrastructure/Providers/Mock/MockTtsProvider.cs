using DubbingPlatform.Application.Abstractions.Providers;
using DubbingPlatform.Application.Abstractions.Providers.Dtos;
using DubbingPlatform.Application.Options;
using Microsoft.Extensions.Options;

namespace DubbingPlatform.Infrastructure.Providers.Mock;

/// <summary>
/// Deterministic TTS mock. Duration is wordCount*400ms clamped 500..5000ms;
/// audio is a generated 16kHz mono sine WAV (byte-identical per input).
/// Later tasks persist via <c>IArtifactStorage</c>; here the content id and
/// SHA-256 are returned in the DTO metadata alongside the in-memory bytes
/// available via <see cref="GenerateAudioBytes"/>.
/// </summary>
public sealed class MockTtsProvider : ITtsProvider
{
    public const string CapabilityName = "Tts";

    private const string ModelName = "mock-1";

    private readonly MockBehaviorOptions _root;
    private readonly MockAsyncJobStore _jobs = new();

    public MockTtsProvider(IOptions<MockBehaviorOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _root = options.Value;
    }

    public string StartJob(string key)
    {
        return _jobs.StartJob(key);
    }

    public (string Status, int PollCount) PollJob(string jobId)
    {
        return _jobs.PollJob(jobId);
    }

    /// <summary>
    /// Generates the deterministic WAV bytes for the given synthesis input.
    /// </summary>
    public static byte[] GenerateAudioBytes(string text, string language, string voiceId)
    {
        var durationMs = MockDeterminism.TtsDurationMs(text);
        return MockDeterminism.GenerateSineWav(durationMs);
    }

    public async Task<TtsResponse> SynthesizeAsync(TtsRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var behavior = MockBehaviorEvaluator.Resolve(_root, CapabilityName);
        var seedKey = string.Join("|", request.TenantId.ToString("N"), request.ProjectId.ToString("N"), request.RunId.ToString("N"), request.Text, request.Language, request.VoiceId);
        var scenario = MockBehaviorEvaluator.EffectiveScenario(behavior, seedKey);
        await MockBehaviorEvaluator.ThrowIfFailingAsync(scenario, cancellationToken).ConfigureAwait(false);

        var durationMs = MockDeterminism.TtsDurationMs(request.Text);
        if (MockBehaviorEvaluator.IsPartial(scenario))
        {
            durationMs = Math.Max(500, durationMs / 2);
        }

        double confidence = MockBehaviorEvaluator.IsLowConfidence(scenario) ? 0.35 : 0.95;

        var audio = MockDeterminism.GenerateSineWav(durationMs);
        var contentHash = MockDeterminism.ContentHash(audio);
        var contentId = string.Concat("mock-tts-", MockDeterminism.StableHex(16, seedKey, contentHash));

        string? jobId = null;
        if (string.Equals(MockBehaviorOptions.NormalizeScenario(scenario), MockBehaviorOptions.AsyncJob, StringComparison.Ordinal))
        {
            jobId = _jobs.StartJob(seedKey);
            for (var i = 0; i < 3; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (status, _) = _jobs.PollJob(jobId);
                if (string.Equals(status, MockAsyncJobStore.Succeeded, StringComparison.Ordinal))
                {
                    break;
                }
            }
        }

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["mock.scenario"] = MockBehaviorOptions.NormalizeScenario(scenario),
            ["mock.capability"] = CapabilityName,
            ["mock.contentHash"] = contentHash,
            ["mock.audioFormat"] = "wav",
            ["mock.sampleRateHz"] = MockDeterminism.SampleRateHz.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        if (MockBehaviorEvaluator.IsDuplicate(scenario))
        {
            metadata["mock.duplicate"] = "true";
        }

        if (MockBehaviorEvaluator.IsPartial(scenario))
        {
            metadata["mock.partial"] = "true";
        }

        if (jobId is not null)
        {
            metadata["mock.job_id"] = jobId;
            metadata["mock.polls"] = "3";
        }

        return new TtsResponse(
            contentId,
            durationMs,
            request.VoiceId,
            confidence,
            ModelName,
            "1",
            "mock",
            new ProviderUsage(request.Text.Length / 4, audio.Length, durationMs / 1000.0, 0),
            metadata);
    }
}
