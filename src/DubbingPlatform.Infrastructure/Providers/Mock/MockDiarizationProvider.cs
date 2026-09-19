using DubbingPlatform.Application.Abstractions.Providers;
using DubbingPlatform.Application.Abstractions.Providers.Dtos;
using DubbingPlatform.Application.Options;
using Microsoft.Extensions.Options;

namespace DubbingPlatform.Infrastructure.Providers.Mock;

/// <summary>
/// Deterministic diarization mock. Splits the duration into 2-3 segments
/// (seeded by TenantId+ArtifactId) with round-robin <c>spk_0/spk_1</c> labels.
/// Same input always yields identical labels and boundaries.
/// </summary>
public sealed class MockDiarizationProvider : IDiarizationProvider
{
    public const string CapabilityName = "Diarization";

    private const string ModelName = "mock-1";

    private readonly MockBehaviorOptions _root;
    private readonly MockAsyncJobStore _jobs = new();

    public MockDiarizationProvider(IOptions<MockBehaviorOptions> options)
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

    public async Task<DiarizationResponse> DiarizeAsync(DiarizationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var behavior = MockBehaviorEvaluator.Resolve(_root, CapabilityName);
        var seedKey = string.Join("|", request.TenantId.ToString("N"), request.ProjectId.ToString("N"), request.RunId.ToString("N"), request.ArtifactId, request.Language);
        var scenario = MockBehaviorEvaluator.EffectiveScenario(behavior, seedKey);
        await MockBehaviorEvaluator.ThrowIfFailingAsync(scenario, cancellationToken).ConfigureAwait(false);

        var durationMs = Math.Max(0, request.DurationMs);
        double confidence = MockBehaviorEvaluator.IsLowConfidence(scenario) ? 0.35 : 0.92;

        List<DiarizationSegment> segments;
        if (MockBehaviorEvaluator.IsPartial(scenario))
        {
            segments = durationMs == 0
                ? []
                : [new DiarizationSegment("spk_0", 0, durationMs / 2, confidence)];
        }
        else
        {
            segments = BuildSegments(seedKey, durationMs, confidence);
        }

        var labels = segments.Select(s => s.SpeakerLabel).Distinct(StringComparer.Ordinal).OrderBy(l => l, StringComparer.Ordinal).ToList();

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

        return new DiarizationResponse(
            segments,
            labels,
            confidence,
            ModelName,
            "1",
            "mock",
            new ProviderUsage(null, null, durationMs / 1000.0, 0),
            metadata);
    }

    internal static List<DiarizationSegment> BuildSegments(string seedKey, int durationMs, double confidence)
    {
        if (durationMs <= 0)
        {
            return [];
        }

        var random = MockDeterminism.SeededRandom(seedKey, "diarization");
        var count = 2 + random.Next(2);
        var segments = new List<DiarizationSegment>(count);
        for (var i = 0; i < count; i++)
        {
            var start = (int)((long)durationMs * i / count);
            var end = (int)((long)durationMs * (i + 1) / count);
            var label = string.Concat("spk_", (i % 2).ToString(System.Globalization.CultureInfo.InvariantCulture));
            segments.Add(new DiarizationSegment(label, start, end, confidence));
        }

        return segments;
    }
}
