using DubbingPlatform.Application.Abstractions.Providers;
using DubbingPlatform.Application.Abstractions.Providers.Dtos;
using DubbingPlatform.Application.Options;
using Microsoft.Extensions.Options;

namespace DubbingPlatform.Infrastructure.Providers.Mock;

/// <summary>
/// Deterministic VAD mock. Returns a single region covering the full duration.
/// No network calls; all outputs derive from stable hashes of the input.
/// </summary>
public sealed class MockVadProvider : IVadProvider
{
    public const string CapabilityName = "Vad";

    private const string ModelName = "mock-1";

    private readonly MockBehaviorOptions _root;
    private readonly MockAsyncJobStore _jobs = new();

    public MockVadProvider(IOptions<MockBehaviorOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _root = options.Value;
    }

    /// <summary>
    /// Starts a deterministic async job for the given key.
    /// </summary>
    public string StartJob(string key)
    {
        return _jobs.StartJob(key);
    }

    /// <summary>
    /// Polls a job: <c>Running</c> twice then <c>Succeeded</c>.
    /// </summary>
    public (string Status, int PollCount) PollJob(string jobId)
    {
        return _jobs.PollJob(jobId);
    }

    public async Task<VadResponse> DetectAsync(VadRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var behavior = MockBehaviorEvaluator.Resolve(_root, CapabilityName);
        var seedKey = string.Join("|", request.TenantId.ToString("N"), request.ProjectId.ToString("N"), request.RunId.ToString("N"), request.ArtifactId, request.Language);
        var scenario = MockBehaviorEvaluator.EffectiveScenario(behavior, seedKey);
        await MockBehaviorEvaluator.ThrowIfFailingAsync(scenario, cancellationToken).ConfigureAwait(false);

        var durationMs = Math.Max(0, request.DurationMs);
        List<VadRegion> regions;
        double confidence = 0.99;

        if (MockBehaviorEvaluator.IsLowConfidence(scenario))
        {
            confidence = 0.35;
        }

        if (MockBehaviorEvaluator.IsPartial(scenario))
        {
            regions = [new VadRegion(0, durationMs / 2, confidence)];
        }
        else
        {
            regions = [new VadRegion(0, durationMs, confidence)];
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

        return new VadResponse(
            regions,
            confidence,
            ModelName,
            "1",
            "mock",
            new ProviderUsage(null, null, durationMs / 1000.0, 0),
            metadata);
    }
}
