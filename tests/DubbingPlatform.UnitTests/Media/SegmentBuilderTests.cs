using DubbingPlatform.Application.Abstractions.Providers.Dtos;
using DubbingPlatform.Application.Errors;
using DubbingPlatform.Application.Exceptions;
using DubbingPlatform.Application.Options;
using DubbingPlatform.Application.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace DubbingPlatform.UnitTests.Media;

/// <summary>
/// Hermetic segmentation tests (no Docker): merge, split, overlap relations,
/// timeline rejection, quota, and deterministic ordering use
/// <see cref="SegmentBuilderService"/> directly with fixed options.
/// </summary>
public sealed class SegmentBuilderTests
{
    private static SegmentBuilderService CreateService(
        int mergePauseMs = 300,
        int maxSegmentMs = 30000,
        int maxSegments = 2000,
        int maxSegmentCount = 2000)
    {
        return new SegmentBuilderService(
            global::Microsoft.Extensions.Options.Options.Create(new SegmentOptions
            {
                MergePauseMs = mergePauseMs,
                MaxSegmentMs = maxSegmentMs,
                MaxSegments = maxSegments,
            }),
            global::Microsoft.Extensions.Options.Options.Create(new QuotaOptions
            {
                MaxSegmentCount = maxSegmentCount,
            }),
            NullLogger<SegmentBuilderService>.Instance);
    }

    private static VadRegion Region(int startMs, int endMs, double confidence = 0.9)
    {
        return new VadRegion(startMs, endMs, confidence);
    }

    [Fact]
    public async Task Merges_Short_Pauses()
    {
        var service = CreateService();
        var runId = Guid.NewGuid();
        var result = await service.BuildAsync(
            runId,
            [Region(0, 1000), Region(1200, 2000)],
            5000).ConfigureAwait(true);

        var single = Assert.Single(result.Segments);
        Assert.Equal(0, single.StartMs);
        Assert.Equal(2000, single.EndMs);
        Assert.Equal(2000, single.DurationMs);
        Assert.Equal(0, single.Sequence);
        Assert.Empty(result.Groups);
        Assert.Empty(result.Overlaps);
    }

    [Fact]
    public async Task Splits_Long_Speech()
    {
        var service = CreateService();
        var runId = Guid.NewGuid();
        var result = await service.BuildAsync(
            runId,
            [Region(0, 65000)],
            70000).ConfigureAwait(true);

        Assert.True(result.Segments.Count > 1);
        foreach (var segment in result.Segments)
        {
            Assert.True(segment.DurationMs <= 30000);
            Assert.True(segment.EndMs > segment.StartMs);
            Assert.True(segment.EndMs <= 70000);
        }

        for (var i = 1; i < result.Segments.Count; i++)
        {
            Assert.Equal(result.Segments[i - 1].EndMs, result.Segments[i].StartMs);
            Assert.Equal(i, result.Segments[i].Sequence);
        }

        Assert.Equal(0, result.Segments[0].StartMs);
        Assert.Equal(65000, result.Segments[^1].EndMs);
    }

    [Fact]
    public async Task Overlap_Creates_Relations()
    {
        var service = CreateService();
        var runId = Guid.NewGuid();
        var result = await service.BuildAsync(
            runId,
            [Region(0, 2000), Region(1500, 3000)],
            5000).ConfigureAwait(true);

        Assert.Equal(2, result.Segments.Count);
        var group = Assert.Single(result.Groups);
        Assert.Equal(1500, group.StartMs);
        Assert.Equal(2000, group.EndMs);
        Assert.Equal(2, result.Overlaps.Count);

        foreach (var overlap in result.Overlaps)
        {
            Assert.Equal(group.Id, overlap.OverlapGroupId);
            Assert.Contains(overlap.RelationType, new[] { "Overlap", "Contains", "ContainedBy" });
            Assert.True(overlap.OverlapEndMs > overlap.OverlapStartMs);
            Assert.True(overlap.OverlapStartMs >= 0);
            Assert.True(overlap.OverlapEndMs <= 5000);
        }

        var ordered = result.Overlaps.OrderBy(o => o.Order).ToList();
        Assert.Equal(0, ordered[0].Order);
        Assert.Equal(1, ordered[1].Order);
        Assert.NotEqual(ordered[0].SegmentId, ordered[1].SegmentId);
    }

    [Fact]
    public async Task Invalid_Timeline_Rejected()
    {
        var service = CreateService();
        var runId = Guid.NewGuid();

        var negative = await Assert.ThrowsAsync<ErrorCodeException>(() => service.BuildAsync(
            runId, [Region(-10, 100)], 5000)).ConfigureAwait(true);
        Assert.Equal(ErrorCodes.PipelineInvariantViolation, negative.ErrorCode);

        var inverted = await Assert.ThrowsAsync<ErrorCodeException>(() => service.BuildAsync(
            runId, [Region(2000, 1000)], 5000)).ConfigureAwait(true);
        Assert.Equal(ErrorCodes.PipelineInvariantViolation, inverted.ErrorCode);

        var beyond = await Assert.ThrowsAsync<ErrorCodeException>(() => service.BuildAsync(
            runId, [Region(0, 99999)], 5000)).ConfigureAwait(true);
        Assert.Equal(ErrorCodes.PipelineInvariantViolation, beyond.ErrorCode);
    }

    [Fact]
    public async Task Quota_Enforced()
    {
        var service = CreateService(mergePauseMs: 0, maxSegments: 2);
        var runId = Guid.NewGuid();
        var regions = new List<VadRegion>
        {
            Region(0, 100),
            Region(500, 600),
            Region(1000, 1100),
            Region(1500, 1600),
            Region(2000, 2100),
        };

        var ex = await Assert.ThrowsAsync<ErrorCodeException>(() => service.BuildAsync(
            runId, regions, 5000)).ConfigureAwait(true);
        Assert.Equal(ErrorCodes.QuotaExceeded, ex.ErrorCode);
    }

    [Fact]
    public async Task Sequence_Ordered_Deterministic()
    {
        var service = CreateService();
        var runId = Guid.NewGuid();
        var regions = new List<VadRegion>
        {
            Region(3000, 4000),
            Region(0, 1000),
            Region(1500, 2000),
        };

        var first = await service.BuildAsync(runId, regions, 5000).ConfigureAwait(true);
        var second = await service.BuildAsync(runId, regions, 5000).ConfigureAwait(true);

        Assert.Equal(3, first.Segments.Count);
        for (var i = 1; i < first.Segments.Count; i++)
        {
            Assert.True(first.Segments[i].StartMs >= first.Segments[i - 1].StartMs);
            Assert.Equal(i, first.Segments[i].Sequence);
        }

        Assert.Equal(
            first.Segments.Select(s => s.Id),
            second.Segments.Select(s => s.Id));
        Assert.Equal(
            first.Segments.Select(s => s.Sequence),
            second.Segments.Select(s => s.Sequence));

        var other = await service.BuildAsync(Guid.NewGuid(), regions, 5000).ConfigureAwait(true);
        Assert.NotEqual(first.Segments[0].Id, other.Segments[0].Id);
    }
}
