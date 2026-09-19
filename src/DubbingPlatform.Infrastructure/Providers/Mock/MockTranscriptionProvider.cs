using DubbingPlatform.Application.Abstractions.Providers;
using DubbingPlatform.Application.Abstractions.Providers.Dtos;
using DubbingPlatform.Application.Options;
using DubbingPlatform.Infrastructure.Observability;
using Microsoft.Extensions.Options;

namespace DubbingPlatform.Infrastructure.Providers.Mock;

/// <summary>
/// Deterministic transcription mock. Text is
/// <c>mock transcript seg {ArtifactId} [{Language}]</c> with word timestamps
/// every 300ms. Same input yields byte-identical text and timestamps.
/// </summary>
public sealed class MockTranscriptionProvider : ITranscriptionProvider
{
    public const string CapabilityName = "Transcription";

    private const string ModelName = "mock-1";

    private readonly MockBehaviorOptions _root;
    private readonly MockAsyncJobStore _jobs = new();

    public MockTranscriptionProvider(IOptions<MockBehaviorOptions> options)
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

    public async Task<TranscriptionResponse> TranscribeAsync(TranscriptionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        TraceEnricher.SetCurrent(request.TenantId, request.ProjectId, request.RunId, CapabilityName, "Mock", ModelName, null);
        PlatformMetrics.ProviderCall(request.TenantId, "Mock", ModelName);
        PlatformMetrics.ObserveSegmentDuration(Math.Max(0, request.DurationMs), CapabilityName);

        var behavior = MockBehaviorEvaluator.Resolve(_root, CapabilityName);
        var seedKey = string.Join("|", request.TenantId.ToString("N"), request.ProjectId.ToString("N"), request.RunId.ToString("N"), request.ArtifactId, request.Language);
        var scenario = MockBehaviorEvaluator.EffectiveScenario(behavior, seedKey);
        try
        {
            await MockBehaviorEvaluator.ThrowIfFailingAsync(scenario, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            PlatformMetrics.ProviderError(request.TenantId, "Mock", ModelName);
            throw;
        }

        var text = string.Concat("mock transcript seg ", request.ArtifactId, " [", request.Language, "]");
        double confidence = MockBehaviorEvaluator.IsLowConfidence(scenario) ? 0.35 : 0.95;
        var durationMs = Math.Max(0, request.DurationMs);

        var words = BuildWords(text, durationMs, confidence);
        if (MockBehaviorEvaluator.IsPartial(scenario))
        {
            var half = words.Count / 2;
            words = words.Take(Math.Max(1, half)).ToList();
            text = string.Join(" ", words.Select(w => w.Word));
        }

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

        return new TranscriptionResponse(
            text,
            confidence,
            words,
            ModelName,
            "1",
            "mock",
            new ProviderUsage(text.Length / 4, words.Count, durationMs / 1000.0, 0),
            metadata);
    }

    internal static List<WordTimestamp> BuildWords(string text, int durationMs, double confidence)
    {
        var tokens = MockDeterminism.SplitWords(text);
        var words = new List<WordTimestamp>(tokens.Count);
        for (var i = 0; i < tokens.Count; i++)
        {
            var start = i * 300;
            var end = durationMs > 0 ? Math.Min((i + 1) * 300, durationMs) : (i + 1) * 300;
            words.Add(new WordTimestamp(tokens[i], start, end, confidence));
        }

        return words;
    }
}
