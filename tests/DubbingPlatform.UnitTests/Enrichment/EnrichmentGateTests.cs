// Task 039C: enrichment unit gap closure.
using System.Text.Json;
using DubbingPlatform.Application.Abstractions.Providers.Dtos;
using DubbingPlatform.Application.Enrichment;
using DubbingPlatform.Application.Options;
using DubbingPlatform.Contracts.Messages;

namespace DubbingPlatform.UnitTests.Enrichment;

/// <summary>
/// Dual-gate decision matrix for optional enrichment: the global
/// <c>Features</c> flag AND the per-project <c>settings.enrichment</c> opt-in,
/// with tolerant settings parsing that never throws. Also covers
/// <see cref="EnrichmentPayload"/> document shape and the lip-sync preview
/// validity matrix. Pure functions only — no I/O, no clock dependence.
/// </summary>
public sealed class EnrichmentGateTests
{
    [Fact]
    public void FeatureFlags_DefaultToDisabled()
    {
        var features = new FeatureOptions();

        Assert.False(features.VideoIntelligenceEnabled);
        Assert.False(features.LipSyncEnabled);
        Assert.False(features.LocalInferenceEnabled);
        Assert.Equal("Features", FeatureOptions.SectionName);
        // A default options bag always passes validation.
        Assert.True(new FeatureOptionsValidator().Validate(null, features).Succeeded);
        Assert.Throws<ArgumentNullException>(() => new FeatureOptionsValidator().Validate(null, null!));
    }

    [Theory]
    [InlineData("""{"enrichment":{"videoIntelligence":true,"lipSync":true}}""", true, true)]
    [InlineData("""{"enrichment":{"videoIntelligence":true}}""", true, false)]
    [InlineData("""{"enrichment":{"lipSync":true}}""", false, true)]
    [InlineData("""{"enrichment":{}}""", false, false)]
    [InlineData("""{"Enrichment":{"VideoIntelligence":true,"LipSync":true}}""", true, true)]
    [InlineData("""{"ENRICHMENT":{"videoINTELLIGENCE":true}}""", true, false)]
    [InlineData("""{}""", false, false)]
    [InlineData("""{"other":{"videoIntelligence":true}}""", false, false)]
    [InlineData("""{"enrichment":"yes"}""", false, false)]
    [InlineData("""{"enrichment":[]}""", false, false)]
    [InlineData("""[]""", false, false)]
    [InlineData("""5""", false, false)]
    [InlineData(""""not json"""", false, false)]
    [InlineData("""{"enrichment":{"videoIntelligence":"true"}}""", true, false)]
    [InlineData("""{"enrichment":{"videoIntelligence":"no"}}""", false, false)]
    [InlineData("""{"enrichment":{"videoIntelligence":1}}""", true, false)]
    [InlineData("""{"enrichment":{"videoIntelligence":0}}""", false, false)]
    [InlineData("""{"enrichment":{"videoIntelligence":null}}""", false, false)]
    [InlineData("""{"enrichment":{"videoIntelligence":[]}}""", false, false)]
    public void ParseSettings_TolerantlyReadsOptIns_AndNeverThrows(string settingsJson, bool video, bool lip)
    {
        var (parsedVideo, parsedLip) = EnrichmentGate.ParseSettings(settingsJson);

        Assert.Equal(video, parsedVideo);
        Assert.Equal(lip, parsedLip);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{}")]
    [InlineData("not json at all")]
    public void ParseSettings_TreatsMissingOrInvalidJsonAsNoOptIn(string? settingsJson)
    {
        Assert.Equal((false, false), EnrichmentGate.ParseSettings(settingsJson));
    }

    [Theory]
    // Feature flag off => never requested, even with a full project opt-in.
    [InlineData(false, false, false, false, false, 0)]
    [InlineData(false, true, true, false, false, 0)]
    [InlineData(false, true, false, false, false, 0)]
    [InlineData(false, false, true, false, false, 0)]
    // Feature flag on but no project opt-in => still nothing.
    [InlineData(true, false, false, false, false, 0)]
    // Feature flag on AND project opt-in => requested.
    [InlineData(true, true, true, true, true, 2)]
    [InlineData(true, true, false, true, false, 1)]
    [InlineData(true, false, true, false, true, 1)]
    public void ShouldRequest_RequiresBothGates(
        bool flagOn,
        bool videoOptIn,
        bool lipOptIn,
        bool expectedVideo,
        bool expectedLip,
        int expectedCount)
    {
        var features = new FeatureOptions
        {
            VideoIntelligenceEnabled = flagOn,
            LipSyncEnabled = flagOn,
        };
        var settings = BuildSettings(videoOptIn, lipOptIn);

        var video = EnrichmentGate.ShouldRequestVideoIntelligence(features, settings);
        var lip = EnrichmentGate.ShouldRequestLipSync(features, settings);
        var kinds = EnrichmentGate.RequestedKinds(features, settings);

        Assert.Equal(expectedVideo, video);
        Assert.Equal(expectedLip, lip);
        Assert.Equal(expectedCount, kinds.Count);
        // A project opt-in alone never bypasses the global feature flag.
        if (videoOptIn)
        {
            Assert.Equal(flagOn, video);
        }

        if (lipOptIn)
        {
            Assert.Equal(flagOn, lip);
        }
    }

    [Fact]
    public void ShouldRequest_Gates_AreIndependent_PerKind()
    {
        var videoOnly = new FeatureOptions { VideoIntelligenceEnabled = true, LipSyncEnabled = false };
        var lipOnly = new FeatureOptions { VideoIntelligenceEnabled = false, LipSyncEnabled = true };
        var settings = BuildSettings(videoIntelligence: true, lipSync: true);

        // Video flag on, lip flag off: only video is requested.
        Assert.True(EnrichmentGate.ShouldRequestVideoIntelligence(videoOnly, settings));
        Assert.False(EnrichmentGate.ShouldRequestLipSync(videoOnly, settings));
        Assert.Equal([EnrichmentKinds.VideoIntelligence], EnrichmentGate.RequestedKinds(videoOnly, settings));

        Assert.False(EnrichmentGate.ShouldRequestVideoIntelligence(lipOnly, settings));
        Assert.True(EnrichmentGate.ShouldRequestLipSync(lipOnly, settings));
        Assert.Equal([EnrichmentKinds.LipSync], EnrichmentGate.RequestedKinds(lipOnly, settings));
    }

    [Fact]
    public void RequestedKinds_PublishesInFrozenOrder()
    {
        var features = new FeatureOptions { VideoIntelligenceEnabled = true, LipSyncEnabled = true };

        var kinds = EnrichmentGate.RequestedKinds(features, BuildSettings(true, true));

        Assert.Equal([EnrichmentKinds.VideoIntelligence, EnrichmentKinds.LipSync], kinds);
        Assert.Equal(EnrichmentKinds.All, kinds);
    }

    [Fact]
    public void RequestedKinds_IsEmpty_WhenProjectDidNotOptIn()
    {
        var features = new FeatureOptions { VideoIntelligenceEnabled = true, LipSyncEnabled = true };

        // Both flags on but no project consent: the caller publishes nothing and
        // the core run completes identically to a build without enrichment.
        Assert.Empty(EnrichmentGate.RequestedKinds(features, "{}"));
        Assert.Empty(EnrichmentGate.RequestedKinds(features, null));
    }

    [Fact]
    public void Gate_IsolatesTenants_ByReadingOnlyTheSuppliedProjectSettings()
    {
        var features = new FeatureOptions { VideoIntelligenceEnabled = true, LipSyncEnabled = true };
        var optedIn = BuildSettings(videoIntelligence: true, lipSync: false);
        var notOptedIn = BuildSettings(videoIntelligence: false, lipSync: false);

        // One project's opt-in never enables another project's enrichment.
        Assert.Single(EnrichmentGate.RequestedKinds(features, optedIn));
        Assert.Empty(EnrichmentGate.RequestedKinds(features, notOptedIn));
        // Re-reading the same settings is stable (idempotent gate).
        Assert.Equal(EnrichmentGate.RequestedKinds(features, optedIn), EnrichmentGate.RequestedKinds(features, optedIn));
    }

    [Fact]
    public void Gate_GuardsNullFeatures()
    {
        Assert.Throws<ArgumentNullException>(() => EnrichmentGate.ShouldRequestVideoIntelligence(null!, "{}"));
        Assert.Throws<ArgumentNullException>(() => EnrichmentGate.ShouldRequestLipSync(null!, "{}"));
        Assert.Throws<ArgumentNullException>(() => EnrichmentGate.RequestedKinds(null!, "{}"));
    }

    [Fact]
    public void BuildRequests_EmitsOneMessagePerKnownKind_WithNoStageIdentity()
    {
        var tenantId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var runId = Guid.NewGuid();

        var requests = EnrichmentGate.BuildRequests(
            tenantId, projectId, runId, "corr-1", [EnrichmentKinds.VideoIntelligence, EnrichmentKinds.LipSync]);

        Assert.Equal(2, requests.Count);
        Assert.Equal(EnrichmentKinds.All, requests.Select(r => r.Kind).ToArray());
        Assert.All(requests, r =>
        {
            Assert.Equal(tenantId, r.TenantId);
            Assert.Equal(projectId, r.ProjectId);
            Assert.Equal(runId, r.ProcessingRunId);
            Assert.Equal("corr-1", r.CorrelationId);
            Assert.Equal(MessageVersionPolicy.CurrentVersion, r.SchemaVersion);
            Assert.Equal(0, r.Attempt);
            // No stage identity: BaseConsumer must skip stage claiming.
            Assert.Null(r.StageExecutionId);
            Assert.Null(r.StageType);
            Assert.Null(r.ScopeType);
            Assert.Null(r.ScopeId);
            Assert.NotEqual(Guid.Empty, r.MessageId);
        });
        Assert.Equal(2, requests.Select(r => r.MessageId).Distinct().Count());
    }

    [Fact]
    public void BuildRequests_GeneratesCorrelationId_WhenOmitted_AndTrimsKinds()
    {
        var requests = EnrichmentGate.BuildRequests(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "  ", ["  " + EnrichmentKinds.LipSync + "  "], attempt: 3);

        var request = Assert.Single(requests);
        Assert.Equal(EnrichmentKinds.LipSync, request.Kind);
        Assert.Equal(3, request.Attempt);
        Assert.Equal(32, request.CorrelationId.Length);
        Assert.True(Guid.TryParseExact(request.CorrelationId, "N", out _));
    }

    [Fact]
    public void BuildRequests_DropsUnknownKinds_WithAnExplicitSafeDefault()
    {
        var requests = EnrichmentGate.BuildRequests(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "corr",
            ["totally-unknown", "", "   ", EnrichmentKinds.VideoIntelligence]);

        // Unknown and blank kinds are skipped, never emitted and never thrown.
        var request = Assert.Single(requests);
        Assert.Equal(EnrichmentKinds.VideoIntelligence, request.Kind);
        Assert.All(requests, r => Assert.True(EnrichmentKinds.IsKnown(r.Kind)));
    }

    [Fact]
    public void BuildRequests_EmptyKinds_YieldsNoMessages_AndGuardsNull()
    {
        Assert.Empty(EnrichmentGate.BuildRequests(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "corr", []));
        Assert.Throws<ArgumentNullException>(() => EnrichmentGate.BuildRequests(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "corr", null!));
    }

    [Fact]
    public void EnrichmentOutcome_HasTheThreeDocumentedValues()
    {
        Assert.Equal(
            [EnrichmentOutcome.Succeeded, EnrichmentOutcome.Failed, EnrichmentOutcome.Skipped],
            Enum.GetValues<EnrichmentOutcome>());
    }

    [Fact]
    public void BuildVideoIntelligenceJson_EmitsSchemaV1_WithCountsOnly()
    {
        var runId = Guid.NewGuid();
        var response = new VideoIntelligenceResponse(
            Faces:
            [
                new FaceTrack("track-1", 0, 1500, 0.91),
                new FaceTrack("track-2", 1500, 3000, 0.72),
            ],
            ActiveSpeakers: [new ActiveSpeakerSegment("spk-1", 200, 1800, 0.88)],
            Confidence: 0.83,
            Model: "mock-vi",
            ModelVersion: "1.2.3",
            Deployment: "eu-west",
            Usage: new ProviderUsage(100, 50, 12.5, 0.01),
            RawMetadata: new Dictionary<string, string> { ["raw"] = "should not serialize" });

        var json = EnrichmentPayload.BuildVideoIntelligenceJson(runId, response, "art-1");

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal("1", root.GetProperty("schemaVersion").GetString());
        Assert.Equal(runId.ToString("N"), root.GetProperty("runId").GetString());
        Assert.Equal("art-1", root.GetProperty("sourceArtifactId").GetString());
        Assert.Equal(2, root.GetProperty("faceCount").GetInt32());
        Assert.Equal(2, root.GetProperty("faces").GetArrayLength());
        Assert.Equal("track-1", root.GetProperty("faces")[0].GetProperty("trackId").GetString());
        Assert.Equal(1500, root.GetProperty("faces")[0].GetProperty("endMs").GetInt32());
        Assert.Equal(1, root.GetProperty("activeSpeakers").GetArrayLength());
        Assert.Equal("spk-1", root.GetProperty("activeSpeakers")[0].GetProperty("speakerLabel").GetString());
        Assert.Equal(0.83, root.GetProperty("confidence").GetDouble(), precision: 6);
        Assert.Equal("Mock", root.GetProperty("provider").GetString());
        Assert.Equal("mock-vi", root.GetProperty("model").GetString());
        Assert.Equal("1.2.3", root.GetProperty("modelVersion").GetString());
        Assert.Equal("eu-west", root.GetProperty("deployment").GetString());
        // Provider usage and raw provider metadata never reach the document.
        Assert.False(root.TryGetProperty("usage", out _));
        Assert.False(root.TryGetProperty("rawMetadata", out _));
        Assert.DoesNotContain("should not serialize", json, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildVideoIntelligenceJson_IsDeterministic_AndOmitsAbsentSourceArtifact()
    {
        var runId = Guid.NewGuid();
        var response = new VideoIntelligenceResponse([], [], 0.5, "mock", null, null, null, null);

        var first = EnrichmentPayload.BuildVideoIntelligenceJson(runId, response);
        var second = EnrichmentPayload.BuildVideoIntelligenceJson(runId, response);

        Assert.Equal(first, second);
        using var document = JsonDocument.Parse(first);
        var root = document.RootElement;
        Assert.Equal(JsonValueKind.Null, root.GetProperty("sourceArtifactId").ValueKind);
        Assert.Equal(0, root.GetProperty("faceCount").GetInt32());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("modelVersion").ValueKind);
        Assert.Throws<ArgumentNullException>(() => EnrichmentPayload.BuildVideoIntelligenceJson(runId, null!));
    }

    [Fact]
    public void BuildLipSyncJson_EmitsSchemaV1_WithScoreAndDuration()
    {
        var runId = Guid.NewGuid();

        var json = EnrichmentPayload.BuildLipSyncJson(runId, 0.77, 42_000, "art-1");

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal("1", root.GetProperty("schemaVersion").GetString());
        Assert.Equal(runId.ToString("N"), root.GetProperty("runId").GetString());
        Assert.Equal("art-1", root.GetProperty("sourceArtifactId").GetString());
        Assert.Equal(0.77, root.GetProperty("lipSyncScore").GetDouble(), precision: 6);
        Assert.Equal(42_000, root.GetProperty("durationMs").GetInt32());

        var withoutSource = EnrichmentPayload.BuildLipSyncJson(runId, 0.0, 0);
        using var second = JsonDocument.Parse(withoutSource);
        Assert.Equal(JsonValueKind.Null, second.RootElement.GetProperty("sourceArtifactId").ValueKind);
    }

    [Theory]
    [InlineData(true, 1000, 1000, 0.0, true)]
    [InlineData(true, 1000, 1000, 100.0, true)]
    [InlineData(true, 1100, 1000, 99.9, false)]
    [InlineData(true, 1000, 1100, 100.0, true)]
    [InlineData(true, 1100, 1000, 100.0, true)]
    [InlineData(true, 1200, 1000, 100.0, false)]
    [InlineData(false, 1000, 1000, 1000.0, false)]
    [InlineData(true, -1, 1000, 100.0, false)]
    [InlineData(true, 1000, -1, 100.0, false)]
    [InlineData(true, 1000, 1000, -1.0, false)]
    [InlineData(true, 1000, 1000, double.NaN, false)]
    public void IsLipSyncOutputValid_AppliesDurationTolerance_Matrix(
        bool decodable,
        long outputDurationMs,
        long sourceDurationMs,
        double toleranceMs,
        bool expected)
    {
        Assert.Equal(expected, EnrichmentPayload.IsLipSyncOutputValid(
            decodable, outputDurationMs, sourceDurationMs, toleranceMs));
    }

    [Fact]
    public void PreviewToleranceMs_IsTighterForVideo_ThanAudioOnly()
    {
        Assert.Equal(140.0, EnrichmentPayload.PreviewToleranceMs(hasVideo: true));
        Assert.Equal(100.0, EnrichmentPayload.PreviewToleranceMs(hasVideo: false));
        Assert.True(EnrichmentPayload.PreviewToleranceMs(hasVideo: true) > EnrichmentPayload.PreviewToleranceMs(hasVideo: false));
    }

    private static string BuildSettings(bool videoIntelligence, bool lipSync)
    {
        return JsonSerializer.Serialize(new
        {
            enrichment = new
            {
                videoIntelligence,
                lipSync,
            },
        });
    }
}
