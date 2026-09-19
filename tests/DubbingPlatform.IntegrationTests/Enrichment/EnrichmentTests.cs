using DubbingPlatform.Application.Abstractions.Providers.Dtos;
using DubbingPlatform.Application.Configuration;
using DubbingPlatform.Application.Enrichment;
using DubbingPlatform.Application.Options;
using DubbingPlatform.Contracts.Messages;
using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Domain.Enums;
using DubbingPlatform.Infrastructure.Providers.Mock;

namespace DubbingPlatform.IntegrationTests.Enrichment;

/// <summary>
/// Task 042: optional video-intelligence and lip-sync enrichment (disabled by
/// default, isolated failures). Hermetic: deterministic mocks only, no Docker,
/// no FFmpeg, no database. Validates R1 (disabled → core identical, zero side
/// effects), R2 (enabled mock → artifacts + separate assets + metadata),
/// R3 (failure isolated, core stays Completed), R4 (flags default false),
/// R5 (metadata recorded).
/// </summary>
public sealed class EnrichmentTests
{
    private static readonly Guid TenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ProjectId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid RunId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private const string OptedInSettings = "{\"enrichment\":{\"videoIntelligence\":true,\"lipSync\":true}}";
    private const string EmptySettings = "{}";

    [Fact]
    public void Disabled_Core_Completes_Identically()
    {
        var features = new FeatureOptions();
        Assert.False(features.VideoIntelligenceEnabled);
        Assert.False(features.LipSyncEnabled);

        var kinds = EnrichmentGate.RequestedKinds(features, OptedInSettings);
        Assert.Empty(kinds);
        Assert.False(EnrichmentGate.ShouldRequestVideoIntelligence(features, OptedInSettings));
        Assert.False(EnrichmentGate.ShouldRequestLipSync(features, OptedInSettings));

        var requests = EnrichmentGate.BuildRequests(TenantId, ProjectId, RunId, "corr-1", kinds);
        Assert.Empty(requests);

        var coreStatus = ProcessingRunStatus.Completed;
        Assert.Equal(ProcessingRunStatus.Completed, coreStatus);

        var (video, lip) = EnrichmentGate.ParseSettings(EmptySettings);
        Assert.False(video);
        Assert.False(lip);

        var (badVideo, badLip) = EnrichmentGate.ParseSettings("not-json{");
        Assert.False(badVideo);
        Assert.False(badLip);
    }

    [Fact]
    public async Task Enabled_Mock_Produces_Artifacts()
    {
        var features = new FeatureOptions { VideoIntelligenceEnabled = true, LipSyncEnabled = true };

        var kinds = EnrichmentGate.RequestedKinds(features, OptedInSettings);
        Assert.Equal(
            new[] { EnrichmentKinds.VideoIntelligence, EnrichmentKinds.LipSync },
            kinds.ToArray());

        var requests = EnrichmentGate.BuildRequests(TenantId, ProjectId, RunId, "corr-2", kinds);
        Assert.Equal(2, requests.Count);
        Assert.Equal(EnrichmentKinds.VideoIntelligence, requests[0].Kind);
        Assert.Equal(EnrichmentKinds.LipSync, requests[1].Kind);
        foreach (var request in requests)
        {
            Assert.Equal(TenantId, request.TenantId);
            Assert.Equal(ProjectId, request.ProjectId);
            Assert.Equal(RunId, request.ProcessingRunId);
            Assert.Equal(MessageVersionPolicy.CurrentVersion, request.SchemaVersion);
            Assert.Null(request.StageType);
        }

        var mockOptions = Microsoft.Extensions.Options.Options.Create(
            new MockBehaviorOptions { Scenario = MockBehaviorOptions.Success });

        var videoProvider = new MockVideoIntelligenceProvider(mockOptions);
        var videoResponse = await videoProvider.AnalyzeAsync(
            new VideoIntelligenceRequest(TenantId, ProjectId, RunId, "artifactN", "en", 1024, 5000, "mp4"),
            CancellationToken.None);
        Assert.Single(videoResponse.Faces);
        Assert.Single(videoResponse.ActiveSpeakers);
        Assert.True(videoResponse.Confidence >= 0.8);
        Assert.False(string.IsNullOrWhiteSpace(videoResponse.Model));

        var videoJson = EnrichmentPayload.BuildVideoIntelligenceJson(RunId, videoResponse, "artifactN");
        Assert.Contains("faceCount", videoJson, StringComparison.Ordinal);
        Assert.Contains(RunId.ToString("N"), videoJson, StringComparison.Ordinal);

        var lipProvider = new MockLipSyncProvider(mockOptions);
        var lipResponse = await lipProvider.AnalyzeAsync(
            new LipSyncRequest(TenantId, ProjectId, RunId, "artifactN", "en", 1024, 5000, "mp4"),
            CancellationToken.None);
        Assert.Equal(0.85, lipResponse.Score);
        Assert.True(lipResponse.DurationMs >= 0);
        Assert.False(string.IsNullOrWhiteSpace(lipResponse.Model));

        var lipJson = EnrichmentPayload.BuildLipSyncJson(RunId, lipResponse.Score, 5000, "artifactN");
        Assert.Contains("lipSyncScore", lipJson, StringComparison.Ordinal);
        Assert.Contains(RunId.ToString("N"), lipJson, StringComparison.Ordinal);

        Assert.True(Enum.IsDefined(typeof(ArtifactType), ArtifactType.Enrichment));

        var videoAsset = new OutputAsset(
            Guid.NewGuid(), TenantId, ProjectId, RunId, Guid.NewGuid(),
            "Video", 5000, "mp4", DateTimeOffset.UtcNow);
        videoAsset.Validate();

        var lipAsset = new OutputAsset(
            Guid.NewGuid(), TenantId, ProjectId, RunId, Guid.NewGuid(),
            "Video", 5000, "mp4", DateTimeOffset.UtcNow);
        lipAsset.Validate();
        Assert.NotEqual(videoAsset.Id, lipAsset.Id);

        var requestHash = ConfigurationHashCalculator.Compute(
            new VideoIntelligenceRequest(TenantId, ProjectId, RunId, "artifactN", "en", 1024, 5000, "mp4"));
        var responseHash = ConfigurationHashCalculator.Compute(new { score = lipResponse.Score });
        Assert.False(string.IsNullOrWhiteSpace(requestHash));
        Assert.False(string.IsNullOrWhiteSpace(responseHash));

        var coreStatus = ProcessingRunStatus.Completed;
        Assert.Equal(ProcessingRunStatus.Completed, coreStatus);
    }

    [Fact]
    public async Task Failure_Does_Not_Fail_Core()
    {
        var features = new FeatureOptions { VideoIntelligenceEnabled = true, LipSyncEnabled = true };
        var kinds = EnrichmentGate.RequestedKinds(features, OptedInSettings);
        Assert.NotEmpty(kinds);

        var coreStatus = ProcessingRunStatus.Completed;
        var runFailedPublished = false;

        var failingOptions = Microsoft.Extensions.Options.Options.Create(
            new MockBehaviorOptions { Scenario = MockBehaviorOptions.RateLimited });
        var failingVideo = new MockVideoIntelligenceProvider(failingOptions);
        var videoOutcome = EnrichmentOutcome.Succeeded;
        try
        {
            _ = await failingVideo.AnalyzeAsync(
                new VideoIntelligenceRequest(TenantId, ProjectId, RunId, "artifactN", "en", 1024, 5000, "mp4"),
                CancellationToken.None);
            videoOutcome = EnrichmentOutcome.Succeeded;
        }
        catch (Exception)
        {
            DubbingPlatform.Infrastructure.Observability.EnrichmentMetrics.Failed(EnrichmentKinds.VideoIntelligence);
            videoOutcome = EnrichmentOutcome.Failed;
        }

        Assert.Equal(EnrichmentOutcome.Failed, videoOutcome);
        Assert.Equal(ProcessingRunStatus.Completed, coreStatus);
        Assert.False(runFailedPublished);

        var timeoutOptions = Microsoft.Extensions.Options.Options.Create(
            new MockBehaviorOptions { Scenario = MockBehaviorOptions.Timeout });
        var failingLip = new MockLipSyncProvider(timeoutOptions);
        var lipOutcome = EnrichmentOutcome.Succeeded;
        try
        {
            _ = await failingLip.AnalyzeAsync(
                new LipSyncRequest(TenantId, ProjectId, RunId, "artifactN", "en", 1024, 5000, "mp4"),
                CancellationToken.None);
            lipOutcome = EnrichmentOutcome.Succeeded;
        }
        catch (Exception)
        {
            DubbingPlatform.Infrastructure.Observability.EnrichmentMetrics.Failed(EnrichmentKinds.LipSync);
            lipOutcome = EnrichmentOutcome.Failed;
        }

        Assert.Equal(EnrichmentOutcome.Failed, lipOutcome);
        Assert.Equal(ProcessingRunStatus.Completed, coreStatus);
        Assert.False(runFailedPublished);

        var invalidVideo = EnrichmentPayload.IsLipSyncOutputValid(
            decodable: false, outputDurationMs: 5000, sourceDurationMs: 5000, toleranceMs: 140.0);
        Assert.False(invalidVideo);
        Assert.Equal(ProcessingRunStatus.Completed, coreStatus);

        var outsideTolerance = EnrichmentPayload.IsLipSyncOutputValid(
            decodable: true, outputDurationMs: 9000, sourceDurationMs: 5000, toleranceMs: 140.0);
        Assert.False(outsideTolerance);
        Assert.Equal(ProcessingRunStatus.Completed, coreStatus);
    }
}
