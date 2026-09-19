using DubbingPlatform.Application.Services;
using DubbingPlatform.Domain.Entities;

namespace DubbingPlatform.UnitTests.Media;

/// <summary>
/// Hermetic diarization-mapping tests (no Docker): stable speaker identity,
/// provenance, fallback shape, and rerun history use the pure
/// <see cref="DiarizationService"/> planners directly. DB persistence lives in
/// the worker (PG, covered in CI).
/// </summary>
public sealed class DiarizationTests
{
    private static SpeechSegment Segment(Guid id, Guid tenantId, Guid projectId, Guid runId, int sequence, int startMs, int endMs)
    {
        return new SpeechSegment(
            id, tenantId, projectId, runId, sequence, startMs, endMs,
            "Pending", null, DateTimeOffset.UtcNow);
    }

    [Fact]
    public void Same_Label_Same_Speaker()
    {
        var tenantId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var segments = new List<SpeechSegment>
        {
            Segment(Guid.NewGuid(), tenantId, projectId, runId, 0, 0, 1000),
            Segment(Guid.NewGuid(), tenantId, projectId, runId, 1, 1000, 2000),
        };
        var labels = new List<DiarLabel>
        {
            new(segments[0].Id, "spk_0", 0.9),
            new(segments[1].Id, "spk_0", 0.8),
        };

        var result = DiarizationService.Plan(projectId, segments, labels, "Mock");

        var single = Assert.Single(result.Speakers);
        Assert.False(result.IsFallback);
        Assert.Equal(single.SpeakerId, result.SegmentToSpeaker[segments[0].Id]);
        Assert.Equal(single.SpeakerId, result.SegmentToSpeaker[segments[1].Id]);
        Assert.Equal(0, single.FirstAppearanceMs);
        Assert.Equal(2000, single.LastAppearanceMs);
    }

    [Fact]
    public void Distinct_Labels_Distinct()
    {
        var tenantId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var segments = new List<SpeechSegment>
        {
            Segment(Guid.NewGuid(), tenantId, projectId, runId, 0, 0, 1000),
            Segment(Guid.NewGuid(), tenantId, projectId, runId, 1, 1000, 2000),
        };
        var labels = new List<DiarLabel>
        {
            new(segments[0].Id, "spk_0", 0.9),
            new(segments[1].Id, "spk_1", 0.9),
        };

        var result = DiarizationService.Plan(projectId, segments, labels, "Mock");

        Assert.Equal(2, result.Speakers.Count);
        var first = result.SegmentToSpeaker[segments[0].Id];
        var second = result.SegmentToSpeaker[segments[1].Id];
        Assert.NotEqual(first, second);
        Assert.Equal(2, result.SegmentToSpeaker.Values.Distinct().Count());
    }

    [Fact]
    public void Fallback_Warning()
    {
        Assert.Equal("DIARIZATION_FALLBACK", DiarizationService.FallbackCode);

        var tenantId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var segments = new List<SpeechSegment>
        {
            Segment(Guid.NewGuid(), tenantId, projectId, runId, 0, 0, 1000),
            Segment(Guid.NewGuid(), tenantId, projectId, runId, 1, 1000, 2000),
        };

        var result = DiarizationService.PlanFallback(projectId, segments, "provider exploded");

        Assert.True(result.IsFallback);
        Assert.Equal("provider exploded", result.FallbackReason);
        var single = Assert.Single(result.Speakers);
        Assert.Equal(DiarizationService.FallbackSpeakerKey, single.SpeakerKey);
        Assert.Equal(DiarizationService.FallbackDisplayName, single.DisplayName);
        Assert.Equal(2, result.SegmentToSpeaker.Count);
        Assert.Equal(single.SpeakerId, result.SegmentToSpeaker[segments[0].Id]);
        Assert.Equal(single.SpeakerId, result.SegmentToSpeaker[segments[1].Id]);
    }

    [Fact]
    public void Provenance_Stored()
    {
        var tenantId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var segments = new List<SpeechSegment>
        {
            Segment(Guid.NewGuid(), tenantId, projectId, runId, 0, 500, 1500),
            Segment(Guid.NewGuid(), tenantId, projectId, runId, 1, 1500, 2500),
        };
        var labels = new List<DiarLabel>
        {
            new(segments[0].Id, "spk_0", 0.8),
            new(segments[1].Id, "spk_0", 0.6),
        };

        var result = DiarizationService.Plan(projectId, segments, labels, "Mock");

        var single = Assert.Single(result.Speakers);
        Assert.Equal("provider:Mock", single.MappingMethod);
        Assert.Equal("1", single.MappingVersion);
        Assert.Equal("spk_0", single.ProviderLabel);
        Assert.Equal(0.7, single.Confidence, precision: 9);
        Assert.Equal(500, single.FirstAppearanceMs);
        Assert.Equal(2500, single.LastAppearanceMs);
        Assert.Equal(
            string.Concat(projectId.ToString("N"), ":spk_0"),
            single.SpeakerKey);
    }

    [Fact]
    public void History_Preserved_On_Rerun()
    {
        var tenantId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var firstRun = Guid.NewGuid();
        var secondRun = Guid.NewGuid();
        var firstSegments = new List<SpeechSegment>
        {
            Segment(Guid.NewGuid(), tenantId, projectId, firstRun, 0, 0, 1000),
            Segment(Guid.NewGuid(), tenantId, projectId, firstRun, 1, 1000, 2000),
        };
        var firstLabels = new List<DiarLabel>
        {
            new(firstSegments[0].Id, "spk_0", 0.9),
            new(firstSegments[1].Id, "spk_1", 0.9),
        };
        var first = DiarizationService.Plan(projectId, firstSegments, firstLabels, "Mock");

        var secondSegments = new List<SpeechSegment>
        {
            Segment(Guid.NewGuid(), tenantId, projectId, secondRun, 0, 0, 1000),
            Segment(Guid.NewGuid(), tenantId, projectId, secondRun, 1, 1000, 2000),
        };
        var secondLabels = new List<DiarLabel>
        {
            new(secondSegments[0].Id, "spk_0", 0.9),
            new(secondSegments[1].Id, "spk_1", 0.9),
        };
        var second = DiarizationService.Plan(projectId, secondSegments, secondLabels, "Mock");

        var firstByLabel = first.Speakers.ToDictionary(s => s.ProviderLabel, s => s.SpeakerId, StringComparer.Ordinal);
        var secondByLabel = second.Speakers.ToDictionary(s => s.ProviderLabel, s => s.SpeakerId, StringComparer.Ordinal);
        Assert.Equal(firstByLabel["spk_0"], secondByLabel["spk_0"]);
        Assert.Equal(firstByLabel["spk_1"], secondByLabel["spk_1"]);

        // Conflicting retry label reassigns the segment but the old speaker id
        // remains computable (rows are never deleted; history via segments).
        var conflictSegments = new List<SpeechSegment>
        {
            Segment(Guid.NewGuid(), tenantId, projectId, secondRun, 0, 0, 1000),
        };
        var conflict = DiarizationService.Plan(
            projectId, conflictSegments, [new DiarLabel(conflictSegments[0].Id, "spk_2", 0.9)], "Mock");
        var conflictId = Assert.Single(conflict.Speakers).SpeakerId;
        Assert.NotEqual(firstByLabel["spk_0"], conflictId);
        Assert.Equal(
            DiarizationService.SpeakerIdFor(projectId, DiarizationService.BuildSpeakerKey(projectId, "spk_0")),
            firstByLabel["spk_0"]);
    }
}
