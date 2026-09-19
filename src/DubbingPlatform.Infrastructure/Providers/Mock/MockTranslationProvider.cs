using DubbingPlatform.Application.Abstractions.Providers;
using DubbingPlatform.Application.Abstractions.Providers.Dtos;
using DubbingPlatform.Application.Options;
using Microsoft.Extensions.Options;

namespace DubbingPlatform.Infrastructure.Providers.Mock;

/// <summary>
/// Deterministic translation mock. Primary is
/// <c>mock-{target}::{sourceText}</c> where source text mirrors the
/// transcription fixture, plus two deterministic alternatives.
/// </summary>
public sealed class MockTranslationProvider : ITranslationProvider
{
    public const string CapabilityName = "Translation";

    private const string ModelName = "mock-1";

    private readonly MockBehaviorOptions _root;
    private readonly MockAsyncJobStore _jobs = new();

    public MockTranslationProvider(IOptions<MockBehaviorOptions> options)
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

    public async Task<TranslationResponse> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var behavior = MockBehaviorEvaluator.Resolve(_root, CapabilityName);
        var seedKey = string.Join("|", request.TenantId.ToString("N"), request.ProjectId.ToString("N"), request.RunId.ToString("N"), request.ArtifactId, request.SourceLanguage, request.TargetLanguage);
        var scenario = MockBehaviorEvaluator.EffectiveScenario(behavior, seedKey);
        await MockBehaviorEvaluator.ThrowIfFailingAsync(scenario, cancellationToken).ConfigureAwait(false);

        var sourceText = string.Concat("mock transcript seg ", request.ArtifactId, " [", request.SourceLanguage, "]");
        var primary = string.Concat("mock-", request.TargetLanguage, "::", sourceText);

        double score = MockBehaviorEvaluator.IsLowConfidence(scenario) ? 0.35 : 0.90;
        List<string> alternatives;
        if (MockBehaviorEvaluator.IsPartial(scenario))
        {
            alternatives = [];
            score = 0.60;
        }
        else
        {
            alternatives = [string.Concat(primary, " alt1"), string.Concat(primary, " alt2")];
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

        return new TranslationResponse(
            primary,
            alternatives,
            score,
            score,
            score,
            score,
            ModelName,
            "1",
            "mock",
            new ProviderUsage(sourceText.Length / 4, primary.Length / 4, null, 0),
            metadata);
    }
}
