using DubbingPlatform.Application.Abstractions.Providers;
using DubbingPlatform.Application.Abstractions.Providers.Dtos;
using DubbingPlatform.Application.Options;
using Microsoft.Extensions.Options;

namespace DubbingPlatform.Infrastructure.Providers.Mock;

/// <summary>
/// Deterministic local-inference mock. Echoes <c>PayloadJson</c> as
/// <c>OutputJson</c> so pipelines can exercise the sidecar boundary without
/// GPU dependencies.
/// </summary>
public sealed class MockLocalInferenceProvider : ILocalInferenceProvider
{
    public const string CapabilityName = "LocalInference";

    private const string ModelName = "mock-1";

    private readonly MockBehaviorOptions _root;
    private readonly MockAsyncJobStore _jobs = new();

    public MockLocalInferenceProvider(IOptions<MockBehaviorOptions> options)
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

    public async Task<LocalInferenceResponse> InferAsync(LocalInferenceRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var behavior = MockBehaviorEvaluator.Resolve(_root, CapabilityName);
        var seedKey = string.Join("|", request.TenantId.ToString("N"), request.ProjectId.ToString("N"), request.RunId.ToString("N"), request.ModelId, request.PayloadJson, request.Language);
        var scenario = MockBehaviorEvaluator.EffectiveScenario(behavior, seedKey);
        await MockBehaviorEvaluator.ThrowIfFailingAsync(scenario, cancellationToken).ConfigureAwait(false);

        double confidence = MockBehaviorEvaluator.IsLowConfidence(scenario) ? 0.35 : 0.99;
        var output = MockBehaviorEvaluator.IsPartial(scenario) && request.PayloadJson.Length > 1
            ? request.PayloadJson[..(request.PayloadJson.Length / 2)]
            : request.PayloadJson;

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

        return new LocalInferenceResponse(
            request.ModelId,
            output,
            confidence,
            ModelName,
            "1",
            "mock",
            new ProviderUsage(request.PayloadJson.Length / 4, output.Length / 4, null, 0),
            metadata);
    }
}
