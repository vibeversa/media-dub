using DubbingPlatform.Application.Abstractions.Providers;
using DubbingPlatform.Application.Abstractions.Providers.Dtos;
using DubbingPlatform.Application.Options;
using Microsoft.Extensions.Options;

namespace DubbingPlatform.Infrastructure.Providers.Mock;

/// <summary>
/// Deterministic source-separation mock. Dialogue/background artifact ids
/// derive stably from the input; confidence is 0.85 on success.
/// </summary>
public sealed class MockSourceSeparationProvider : ISourceSeparationProvider
{
    public const string CapabilityName = "SourceSeparation";

    private const string ModelName = "mock-1";

    private readonly MockBehaviorOptions _root;
    private readonly MockAsyncJobStore _jobs = new();

    public MockSourceSeparationProvider(IOptions<MockBehaviorOptions> options)
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

    public async Task<SeparationResponse> SeparateAsync(SeparationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var behavior = MockBehaviorEvaluator.Resolve(_root, CapabilityName);
        var seedKey = string.Join("|", request.TenantId.ToString("N"), request.ProjectId.ToString("N"), request.RunId.ToString("N"), request.ArtifactId, request.Language);
        var scenario = MockBehaviorEvaluator.EffectiveScenario(behavior, seedKey);
        await MockBehaviorEvaluator.ThrowIfFailingAsync(scenario, cancellationToken).ConfigureAwait(false);

        double confidence = MockBehaviorEvaluator.IsLowConfidence(scenario) ? 0.35 : 0.85;
        var dialogueId = string.Concat("mock-dialogue-", MockDeterminism.StableHex(16, seedKey, "dialogue"));
        string? backgroundId = string.Concat("mock-bg-", MockDeterminism.StableHex(16, seedKey, "background"));
        if (MockBehaviorEvaluator.IsPartial(scenario))
        {
            backgroundId = null;
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

        return new SeparationResponse(
            dialogueId,
            backgroundId,
            confidence,
            ModelName,
            "1",
            "mock",
            new ProviderUsage(null, null, Math.Max(0, request.DurationMs) / 1000.0, 0),
            metadata);
    }
}
