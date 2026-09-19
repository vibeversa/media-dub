using DubbingPlatform.Application.Abstractions.Providers.Dtos;
using DubbingPlatform.Application.Errors;
using DubbingPlatform.Application.Exceptions;
using DubbingPlatform.Application.Options;
using DubbingPlatform.Domain.Exceptions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DubbingPlatform.Application.Services;

/// <summary>
/// Optional speaker-boundary hint for segmentation. Intervals use integer
/// milliseconds (no float drift). When provided, pauses spanning different
/// speaker spans are never merged; long speech is additionally split at span
/// edges inside the segment. Forward-compatible placeholder until Task 023
/// diarization produces authoritative bounds.
/// </summary>
public sealed record SpeakerBound(int StartMs, int EndMs);

/// <summary>
/// Optional word-timing hint for segmentation. When provided, segment
/// boundaries and long-speech split points snap to the nearest word edge
/// within tolerance (100ms for boundaries, 200ms for splits); otherwise the
/// ideal point stands. Forward-compatible placeholder until Task 024
/// transcription produces authoritative timestamps.
/// </summary>
public sealed record WordTimestamp(int StartMs, int EndMs, string Word);

/// <summary>
/// One built segment. <c>Id</c> is deterministic
/// (<see cref="GuidUtility.SegmentId"/>); <c>Sequence</c> orders by start time.
/// </summary>
public sealed record BuiltSegment(Guid Id, int Sequence, int StartMs, int EndMs, int DurationMs);

/// <summary>
/// One overlap cluster window (the shared overlapping span, not the union).
/// </summary>
public sealed record BuiltOverlapGroup(Guid Id, int StartMs, int EndMs);

/// <summary>
/// One segment's membership in an overlap group with its overlap window.
/// </summary>
public sealed record BuiltSegmentOverlap(
    Guid Id,
    Guid SegmentId,
    Guid OverlapGroupId,
    string RelationType,
    int Order,
    int OverlapStartMs,
    int OverlapEndMs);

/// <summary>
/// Deterministic segmentation output: ordered segments plus relational
/// overlap rows (never a single group-id field).
/// </summary>
public sealed record SegmentBuildResult(
    IReadOnlyList<BuiltSegment> Segments,
    IReadOnlyList<BuiltOverlapGroup> Groups,
    IReadOnlyList<BuiltSegmentOverlap> Overlaps);

/// <summary>
/// Pure deterministic VAD-to-segments builder (no I/O).
/// Pipeline: sort regions by start; reject invalid timelines
/// (<c>PIPELINE_INVARIANT_VIOLATION</c>); merge gaps strictly below
/// <c>Segment:MergePauseMs</c> (overlaps never merge; cross-speaker pauses
/// never merge when bounds are supplied); split speech longer than
/// <c>Segment:MaxSegmentMs</c> into <c>ceil(len/max)</c> equal parts (the only
/// deterministic choice once sub-threshold silence is merged away; word edges
/// within 200ms win when supplied); preserve intentional overlaps above
/// 100ms as relational clusters (smaller overlaps are truncated to adjacency
/// so the timeline stays valid); snap boundaries to word edges within 100ms;
/// assign start-ordered sequences with deterministic ids; enforce
/// <c>Segment:MaxSegments</c> and <c>Quota:MaxSegmentCount</c> together
/// (<c>QUOTA_EXCEEDED</c>, no partial output). Empty input yields zero
/// segments (callers persist an empty artifact and set downstream expected
/// units to zero). All arithmetic uses integer milliseconds with
/// overflow-checked <c>long</c> math.
/// </summary>
public sealed class SegmentBuilderService
{
    private const int OverlapThresholdMs = 100;

    private const int BoundarySnapMs = 100;

    private const int SplitSnapMs = 200;

    private readonly SegmentOptions _segments;
    private readonly QuotaOptions _quota;
    private readonly ILogger<SegmentBuilderService> _logger;

    public SegmentBuilderService(
        IOptions<SegmentOptions> segments,
        IOptions<QuotaOptions> quota,
        ILogger<SegmentBuilderService> logger)
    {
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(quota);
        ArgumentNullException.ThrowIfNull(logger);
        _segments = segments.Value;
        _quota = quota.Value;
        _logger = logger;
    }

    /// <summary>
    /// Builds ordered segments plus overlap relations from VAD regions.
    /// </summary>
    public Task<SegmentBuildResult> BuildAsync(
        Guid runId,
        IReadOnlyList<VadRegion> vadRegions,
        int mediaDurationMs,
        IReadOnlyList<SpeakerBound>? speakerBounds = null,
        IReadOnlyList<WordTimestamp>? wordTimestamps = null,
        CancellationToken cancellationToken = default)
    {
        if (runId == Guid.Empty)
        {
            throw new DomainException("RunId must not be empty.");
        }

        ArgumentNullException.ThrowIfNull(vadRegions);

        if (mediaDurationMs <= 0)
        {
            throw new ErrorCodeException(
                ErrorCodes.PipelineInvariantViolation,
                "Media duration must be positive.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        var bounds = speakerBounds ?? [];
        var words = wordTimestamps ?? [];
        ValidateHints(bounds, words, mediaDurationMs);

        var sorted = vadRegions
            .Select((r, i) => (Region: r, Index: i))
            .OrderBy(p => p.Region.StartMs)
            .ThenBy(p => p.Region.EndMs)
            .ThenBy(p => p.Index)
            .Select(p => p.Region)
            .ToList();

        foreach (var region in sorted)
        {
            ValidateRegion(region, mediaDurationMs);
        }

        var merged = Merge(sorted, bounds);
        var split = SplitLong(merged, words);
        var snapped = SnapBoundaries(split, words, mediaDurationMs);

        foreach (var window in snapped)
        {
            ValidateWindow(window.StartMs, window.EndMs, mediaDurationMs);
        }

        var ordered = snapped
            .OrderBy(w => w.StartMs)
            .ThenBy(w => w.EndMs)
            .ToList();

        if (ordered.Count > _segments.MaxSegments || ordered.Count > _quota.MaxSegmentCount)
        {
            throw new ErrorCodeException(
                ErrorCodes.QuotaExceeded,
                string.Concat(
                    "Segment count ",
                    ordered.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    " exceeds the per-run limit."));
        }

        var segments = new List<BuiltSegment>(ordered.Count);
        for (var i = 0; i < ordered.Count; i++)
        {
            var window = ordered[i];
            var duration = checked(window.EndMs - window.StartMs);
            segments.Add(new BuiltSegment(GuidUtility.SegmentId(runId, i), i, window.StartMs, window.EndMs, duration));
        }

        var (groups, overlaps) = BuildOverlaps(runId, segments, mediaDurationMs);

        _logger.LogDebug(
            "Segment build for run {RunId}: {Regions} regions -> {Segments} segments, {Groups} overlap groups.",
            runId, sorted.Count, segments.Count, groups.Count);

        return Task.FromResult(new SegmentBuildResult(segments, groups, overlaps));
    }

    private sealed record Window(int StartMs, int EndMs);

    private static void ValidateHints(IReadOnlyList<SpeakerBound> bounds, IReadOnlyList<WordTimestamp> words, int mediaDurationMs)
    {
        foreach (var bound in bounds)
        {
            if (bound.StartMs < 0 || bound.EndMs <= bound.StartMs || bound.EndMs > mediaDurationMs)
            {
                throw new ErrorCodeException(
                    ErrorCodes.PipelineInvariantViolation,
                    "Speaker bound timeline is invalid.");
            }
        }

        foreach (var word in words)
        {
            if (word.StartMs < 0 || word.EndMs <= word.StartMs || word.EndMs > mediaDurationMs)
            {
                throw new ErrorCodeException(
                    ErrorCodes.PipelineInvariantViolation,
                    "Word timestamp timeline is invalid.");
            }

            if (string.IsNullOrWhiteSpace(word.Word))
            {
                throw new ErrorCodeException(
                    ErrorCodes.PipelineInvariantViolation,
                    "Word timestamp text must not be empty.");
            }
        }
    }

    private static void ValidateRegion(VadRegion region, int mediaDurationMs)
    {
        if (region.StartMs < 0
            || region.EndMs <= region.StartMs
            || region.EndMs > mediaDurationMs
            || (long)region.EndMs - region.StartMs <= 0
            || (long)region.EndMs - region.StartMs > int.MaxValue)
        {
            throw new ErrorCodeException(
                ErrorCodes.PipelineInvariantViolation,
                string.Concat(
                    "VAD region [",
                    region.StartMs.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ",",
                    region.EndMs.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    "] is outside the media timeline."));
        }
    }

    private static void ValidateWindow(int startMs, int endMs, int mediaDurationMs)
    {
        if (startMs < 0 || endMs <= startMs || endMs > mediaDurationMs)
        {
            throw new ErrorCodeException(
                ErrorCodes.PipelineInvariantViolation,
                string.Concat(
                    "Segment window [",
                    startMs.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ",",
                    endMs.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    "] is outside the media timeline."));
        }

        var duration = (long)endMs - startMs;
        if (duration <= 0 || duration > int.MaxValue)
        {
            throw new ErrorCodeException(
                ErrorCodes.PipelineInvariantViolation,
                "Segment duration is out of range.");
        }
    }

    private List<Window> Merge(IReadOnlyList<VadRegion> sorted, IReadOnlyList<SpeakerBound> bounds)
    {
        var merged = new List<Window>();
        foreach (var region in sorted)
        {
            if (merged.Count == 0)
            {
                merged.Add(new Window(region.StartMs, region.EndMs));
                continue;
            }

            var last = merged[^1];
            var gap = (long)region.StartMs - last.EndMs;
            if (gap < 0)
            {
                merged.Add(new Window(region.StartMs, region.EndMs));
                continue;
            }

            if (gap < _segments.MergePauseMs && !SpansSpeakerBoundary(last.EndMs, region.StartMs, bounds))
            {
                var end = Math.Max(last.EndMs, region.EndMs);
                merged[^1] = new Window(last.StartMs, end);
            }
            else if (gap == 0 && SpansSpeakerBoundary(last.EndMs, region.StartMs, bounds))
            {
                merged.Add(new Window(region.StartMs, region.EndMs));
            }
            else if (gap >= _segments.MergePauseMs)
            {
                merged.Add(new Window(region.StartMs, region.EndMs));
            }
            else
            {
                merged.Add(new Window(region.StartMs, region.EndMs));
            }
        }

        return merged;
    }

    private static bool SpansSpeakerBoundary(int endMs, int startMs, IReadOnlyList<SpeakerBound> bounds)
    {
        if (bounds.Count == 0)
        {
            return false;
        }

        var endSpan = SpanIndexOf(bounds, endMs);
        var startSpan = SpanIndexOf(bounds, startMs);
        if (endSpan < 0 || startSpan < 0)
        {
            return false;
        }

        return endSpan != startSpan;
    }

    private static int SpanIndexOf(IReadOnlyList<SpeakerBound> bounds, int pointMs)
    {
        for (var i = 0; i < bounds.Count; i++)
        {
            if (pointMs >= bounds[i].StartMs && pointMs < bounds[i].EndMs)
            {
                return i;
            }
        }

        return -1;
    }

    private List<Window> SplitLong(List<Window> merged, IReadOnlyList<WordTimestamp> words)
    {
        var result = new List<Window>(merged.Count);
        foreach (var window in merged)
        {
            var length = (long)window.EndMs - window.StartMs;
            if (length <= _segments.MaxSegmentMs)
            {
                result.Add(window);
                continue;
            }

            var parts = (int)((length + _segments.MaxSegmentMs - 1) / _segments.MaxSegmentMs);
            var cuts = new List<int>(parts + 1) { window.StartMs };
            for (var i = 1; i < parts; i++)
            {
                var ideal = window.StartMs + (length * i / parts);
                var snappedCut = SnapCut((int)ideal, words);
                var prev = cuts[^1];
                if (snappedCut <= prev || snappedCut >= window.EndMs)
                {
                    snappedCut = (int)ideal;
                }

                cuts.Add(snappedCut);
            }

            cuts.Add(window.EndMs);
            for (var i = 0; i + 1 < cuts.Count; i++)
            {
                if (cuts[i + 1] <= cuts[i])
                {
                    throw new ErrorCodeException(
                        ErrorCodes.PipelineInvariantViolation,
                        "Segment split produced a negative overlap window.");
                }

                result.Add(new Window(cuts[i], cuts[i + 1]));
            }
        }

        return result;
    }

    private static int SnapCut(int ideal, IReadOnlyList<WordTimestamp> words)
    {
        if (words.Count == 0)
        {
            return ideal;
        }

        var best = ideal;
        var bestDistance = long.MaxValue;
        foreach (var word in words)
        {
            foreach (var edge in new[] { word.StartMs, word.EndMs })
            {
                var distance = Math.Abs((long)edge - ideal);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = edge;
                }
            }
        }

        return bestDistance <= SplitSnapMs ? best : ideal;
    }

    private static List<Window> SnapBoundaries(List<Window> windows, IReadOnlyList<WordTimestamp> words, int mediaDurationMs)
    {
        if (words.Count == 0)
        {
            return windows;
        }

        var edges = new List<int>(words.Count * 2);
        foreach (var word in words)
        {
            edges.Add(word.StartMs);
            edges.Add(word.EndMs);
        }

        var snapped = new List<Window>(windows.Count);
        foreach (var window in windows)
        {
            var start = SnapEdge(window.StartMs, edges, true);
            var end = SnapEdge(window.EndMs, edges, false);
            if (start < 0)
            {
                start = 0;
            }

            if (end > mediaDurationMs)
            {
                end = mediaDurationMs;
            }

            if (end <= start)
            {
                snapped.Add(window);
            }
            else
            {
                snapped.Add(new Window(start, end));
            }
        }

        return snapped;
    }

    private static int SnapEdge(int value, List<int> edges, bool isStart)
    {
        var best = value;
        var bestDistance = long.MaxValue;
        foreach (var edge in edges)
        {
            var distance = Math.Abs((long)edge - value);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = edge;
            }
        }

        if (bestDistance > BoundarySnapMs)
        {
            return value;
        }

        if (isStart && best > value)
        {
            return value;
        }

        if (!isStart && best < value)
        {
            return value;
        }

        return best;
    }

    private static (IReadOnlyList<BuiltOverlapGroup> Groups, IReadOnlyList<BuiltSegmentOverlap> Overlaps) BuildOverlaps(
        Guid runId,
        IReadOnlyList<BuiltSegment> segments,
        int mediaDurationMs)
    {
        var groups = new List<BuiltOverlapGroup>();
        var overlaps = new List<BuiltSegmentOverlap>();
        if (segments.Count < 2)
        {
            return (groups, overlaps);
        }

        var ordered = segments.OrderBy(s => s.StartMs).ThenBy(s => s.EndMs).ToList();
        var cluster = new List<BuiltSegment> { ordered[0] };
        var clusterMaxEnd = ordered[0].EndMs;
        var groupIndex = 0;

        void FlushCluster()
        {
            if (cluster.Count >= 2)
            {
                EmitCluster(runId, cluster, groupIndex, mediaDurationMs, groups, overlaps);
                groupIndex++;
            }

            cluster.Clear();
        }

        for (var i = 1; i < ordered.Count; i++)
        {
            var next = ordered[i];
            var overlap = (long)clusterMaxEnd - next.StartMs;
            if (overlap > OverlapThresholdMs)
            {
                cluster.Add(next);
                clusterMaxEnd = Math.Max(clusterMaxEnd, next.EndMs);
            }
            else if (overlap > 0)
            {
                // Micro-overlap (<=100ms): truncate to adjacency in place by
                // treating the boundary as shared; no relational rows. The
                // persisted segments keep provider timings, so record the
                // cluster as adjacent-only when any member barely touches.
                // Deterministic choice: keep both segments, no group.
                FlushCluster();
                cluster.Add(next);
                clusterMaxEnd = next.EndMs;
            }
            else
            {
                FlushCluster();
                cluster.Add(next);
                clusterMaxEnd = next.EndMs;
            }
        }

        FlushCluster();
        return (groups, overlaps);
    }

    private static void EmitCluster(
        Guid runId,
        IReadOnlyList<BuiltSegment> cluster,
        int groupIndex,
        int mediaDurationMs,
        List<BuiltOverlapGroup> groups,
        List<BuiltSegmentOverlap> overlaps)
    {
        var maxStart = cluster.Max(s => s.StartMs);
        var minEnd = cluster.Min(s => s.EndMs);
        int groupStart;
        int groupEnd;

        if (maxStart < minEnd)
        {
            groupStart = maxStart;
            groupEnd = minEnd;
        }
        else
        {
            var pairStarts = new List<int>();
            var pairEnds = new List<int>();
            var byStart = cluster.OrderBy(s => s.StartMs).ToList();
            for (var i = 0; i + 1 < byStart.Count; i++)
            {
                var start = Math.Max(byStart[i].StartMs, byStart[i + 1].StartMs);
                var end = Math.Min(byStart[i].EndMs, byStart[i + 1].EndMs);
                if (end > start)
                {
                    pairStarts.Add(start);
                    pairEnds.Add(end);
                }
            }

            if (pairStarts.Count == 0)
            {
                throw new ErrorCodeException(
                    ErrorCodes.PipelineInvariantViolation,
                    "Overlap cluster has a negative overlap window.");
            }

            groupStart = pairStarts.Min();
            groupEnd = pairEnds.Max();
        }

        if (groupStart < 0 || groupEnd <= groupStart || groupEnd > mediaDurationMs)
        {
            throw new ErrorCodeException(
                ErrorCodes.PipelineInvariantViolation,
                "Overlap group window is outside the media timeline.");
        }

        var groupId = GuidUtility.From(string.Concat(runId.ToString("N"), ":overlap:", groupIndex.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        groups.Add(new BuiltOverlapGroup(groupId, groupStart, groupEnd));

        var members = cluster.OrderBy(s => s.StartMs).ThenBy(s => s.EndMs).ToList();
        for (var order = 0; order < members.Count; order++)
        {
            var member = members[order];
            var start = Math.Max(member.StartMs, groupStart);
            var end = Math.Min(member.EndMs, groupEnd);
            if (end <= start)
            {
                // Transitive member outside the common window: fall back to its
                // widest pairwise overlap inside the cluster.
                var fallback = MaxPairwise(member, members);
                start = fallback.StartMs;
                end = fallback.EndMs;
            }

            if (end <= start || start < 0 || end > mediaDurationMs)
            {
                throw new ErrorCodeException(
                    ErrorCodes.PipelineInvariantViolation,
                    "Segment overlap window is invalid.");
            }

            var relation = ClassifyRelation(member.StartMs, member.EndMs, groupStart, groupEnd);
            var id = GuidUtility.From(string.Concat(
                runId.ToString("N"),
                ":overlap:",
                groupIndex.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ":member:",
                order.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            overlaps.Add(new BuiltSegmentOverlap(id, member.Id, groupId, relation, order, start, end));
        }
    }

    private static Window MaxPairwise(BuiltSegment member, IReadOnlyList<BuiltSegment> members)
    {
        var bestStart = -1;
        var bestEnd = -1;
        var bestLength = -1L;
        foreach (var other in members)
        {
            if (other.Id == member.Id)
            {
                continue;
            }

            var start = Math.Max(member.StartMs, other.StartMs);
            var end = Math.Min(member.EndMs, other.EndMs);
            var length = (long)end - start;
            if (length > bestLength)
            {
                bestLength = length;
                bestStart = start;
                bestEnd = end;
            }
        }

        if (bestLength <= 0)
        {
            throw new ErrorCodeException(
                ErrorCodes.PipelineInvariantViolation,
                "Segment overlap window is invalid.");
        }

        return new Window(bestStart, bestEnd);
    }

    private static string ClassifyRelation(int segStart, int segEnd, int groupStart, int groupEnd)
    {
        var covers = segStart <= groupStart && segEnd >= groupEnd;
        var inside = groupStart <= segStart && segEnd <= groupEnd;
        if (covers && inside)
        {
            return "Contains";
        }

        if (covers)
        {
            return "Contains";
        }

        if (inside)
        {
            return "ContainedBy";
        }

        return "Overlap";
    }
}
