using DubbingPlatform.Application.Abstractions.Providers;
using DubbingPlatform.Application.Abstractions.Providers.Dtos;
using DubbingPlatform.Application.Errors;
using DubbingPlatform.Application.Exceptions;
using DubbingPlatform.Application.Options;

namespace DubbingPlatform.Infrastructure.Providers.Mock;

/// <summary>
/// Deterministic lip-sync mock. Returns score 0.85 on success (0.35 on
/// low-confidence) covering the full duration. Failure scenarios mirror the
/// other mocks via <see cref="MockBehaviorEvaluator"/>.
/// </summary>
public sealed class MockLipSyncProvider : ILipSyncProvider
{
    public const string CapabilityName = "LipSync";

    private const string ModelName = "mock-lipsync-1";

    private const double SuccessScore = 0.85;

    private const double LowConfidenceScore = 0.35;

    private readonly MockBehaviorOptions _root;

    public MockLipSyncProvider(Microsoft.Extensions.Options.IOptions<MockBehaviorOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _root = options.Value;
    }

    public async Task<LipSyncResponse> AnalyzeAsync(LipSyncRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var behavior = MockBehaviorEvaluator.Resolve(_root, CapabilityName);
        var seedKey = string.Join("|", request.TenantId.ToString("N"), request.ProjectId.ToString("N"), request.RunId.ToString("N"), request.ArtifactId, request.Language);
        var scenario = MockBehaviorEvaluator.EffectiveScenario(behavior, seedKey);
        await MockBehaviorEvaluator.ThrowIfFailingAsync(scenario, cancellationToken).ConfigureAwait(false);

        var durationMs = Math.Max(0, request.DurationMs);
        var score = MockBehaviorEvaluator.IsLowConfidence(scenario) ? LowConfidenceScore : SuccessScore;

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["mock.scenario"] = MockBehaviorOptions.NormalizeScenario(scenario),
            ["mock.capability"] = CapabilityName,
            ["lipSyncScore"] = score.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        if (MockBehaviorEvaluator.IsDuplicate(scenario))
        {
            metadata["mock.duplicate"] = "true";
        }

        if (MockBehaviorEvaluator.IsPartial(scenario))
        {
            metadata["mock.partial"] = "true";
        }

        return new LipSyncResponse(
            score,
            durationMs,
            ModelName,
            "1",
            "mock",
            new ProviderUsage(null, null, durationMs / 1000.0, 0),
            metadata);
    }
}
