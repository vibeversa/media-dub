using DubbingPlatform.Application.Options;
using DubbingPlatform.Application.Services;
using DubbingPlatform.Domain.Enums;
using DubbingPlatform.Domain.Exceptions;

namespace DubbingPlatform.UnitTests.Pipeline;

/// <summary>
/// Hermetic timing-optimization tests (no Docker): hard-cap enforcement,
/// bounded loop termination, cap-skip without violation, SyncResult
/// persistence shape, and exhausted-to-review routing use the pure
/// <see cref="TimingOptimizationService"/> planners directly with mock TTS
/// durations. DB persistence and FFmpeg stretching live in the
/// service/worker (PG, covered in CI).
/// </summary>
public sealed class TimingTests
{
    private static TimingOptions Defaults()
    {
        return new TimingOptions();
    }

    [Fact]
    public void Respects_Limits()
    {
        var options = Defaults();
        Assert.Equal(50, options.PreferredToleranceMs);
        Assert.Equal(100, options.MaxToleranceMs);
        Assert.Equal(15.0, options.MaxRateChangePercent);
        Assert.Equal(1.15, options.MaxStretchFactor);
        Assert.Equal(3, options.MaxCandidates);
        Assert.Equal(3, options.MaxTtsPreviewAttempts);
        Assert.Equal(2, options.MaxRewrites);

        foreach (var rate in new[] { 0.1, 0.5, 0.85, 1.0, 1.15, 1.5, 3.0, double.NaN })
        {
            var clamped = TimingOptimizationService.ClampRate(rate, options.MaxRateChangePercent);
            Assert.InRange(clamped, 0.85, 1.15);
        }

        foreach (var factor in new[] { 0.1, 0.5, 1.0 / 1.15, 1.0, 1.15, 1.5, 3.0, double.NaN })
        {
            var clamped = TimingOptimizationService.ClampStretch(factor, options.MaxStretchFactor);
            Assert.InRange(clamped, 1.0 / 1.15, 1.15);
        }

        // Mock TTS durations: far-too-long rendering into a 2000ms window.
        var plan = TimingOptimizationService.Plan(
            2000, 4000, [3500, 3000, 2900, 2800], [2700, 2600, 2500], options);

        Assert.True(plan.CandidateCount <= 3);
        Assert.True(plan.PreviewCount <= 3);
        Assert.True(plan.RewriteCount <= 2);
        foreach (var attempt in plan.Attempts)
        {
            Assert.InRange(attempt.Rate, 0.85, 1.15);
            Assert.InRange(attempt.StretchFactor, 1.0 / 1.15, 1.15);
            Assert.InRange(attempt.SyncScore, 0.0, 100.0);
        }
    }

    [Fact]
    public void Stops_After_Attempts()
    {
        var options = Defaults();
        var alternates = Enumerable.Repeat(5000, 10).ToList();
        var rewrites = Enumerable.Repeat(4900, 10).ToList();

        var plan = TimingOptimizationService.Plan(1000, 5000, alternates, rewrites, options);

        Assert.True(plan.CandidateCount <= options.MaxCandidates);
        Assert.True(plan.PreviewCount <= options.MaxTtsPreviewAttempts);
        Assert.True(plan.RewriteCount <= options.MaxRewrites);
        Assert.True(plan.Attempts.Count <= options.MaxCandidates + options.MaxTtsPreviewAttempts + options.MaxRewrites);
        Assert.Equal(SyncStatus.ManualReviewRequired, plan.FinalStatus);

        // Deterministic: same mock durations yield the same plan.
        var repeat = TimingOptimizationService.Plan(1000, 5000, alternates, rewrites, options);
        Assert.Equal(plan.Attempts.Count, repeat.Attempts.Count);
        Assert.Equal(plan.ChosenIndex, repeat.ChosenIndex);
        for (var i = 0; i < plan.Attempts.Count; i++)
        {
            Assert.Equal(plan.Attempts[i].DurationMs, repeat.Attempts[i].DurationMs);
            Assert.Equal(plan.Attempts[i].Kind, repeat.Attempts[i].Kind);
        }
    }

    [Fact]
    public void No_Violation()
    {
        var options = Defaults();

        // Tight window: 2000ms of audio into 1000ms needs factor 2.0 > 1.15x cap.
        var plan = TimingOptimizationService.Plan(1000, 2000, [], [], options);

        Assert.True(plan.StretchSkippedForCap);
        foreach (var attempt in plan.Attempts)
        {
            Assert.InRange(attempt.Rate, 0.85, 1.15);
            Assert.InRange(attempt.StretchFactor, 1.0 / 1.15, 1.15);
        }

        Assert.Equal(SyncStatus.ManualReviewRequired, plan.FinalStatus);

        // Out-of-filter-range tempo fails fast instead of reaching FFmpeg
        // (the 1.15x timing cap is enforced upstream by ClampStretch/Plan).
        Assert.Throws<DomainException>(() =>
            TimingOptimizationService.BuildStretchArgs("in.wav", "out.wav", 200.0, 4));
        var args = TimingOptimizationService.BuildStretchArgs("in.wav", "out.wav", 1.15, 4);
        Assert.Contains("atempo=1.150", string.Join(" ", args), StringComparison.Ordinal);
    }

    [Fact]
    public void SyncResult_Stored()
    {
        var tenantId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var segmentId = Guid.NewGuid();

        var row = TimingOptimizationService.BuildSyncResult(
            tenantId, projectId, runId, segmentId,
            2000, 2075, 1.0, 1.0, 92.5, SyncStatus.SyncAcceptableWithWarning);

        Assert.Equal(tenantId, row.TenantId);
        Assert.Equal(projectId, row.ProjectId);
        Assert.Equal(runId, row.RunId);
        Assert.Equal(segmentId, row.SegmentId);
        Assert.Equal(2000, row.TargetWindowMs);
        Assert.Equal(2075, row.ActualDurationMs);
        Assert.Equal(0.925, row.SyncScore, precision: 5);
        Assert.Equal(SyncStatus.SyncAcceptableWithWarning, row.Status);
        Assert.Equal(1.0, row.RateDelta, precision: 5);
        Assert.Equal(1.0, row.StretchFactor, precision: 5);
        row.Validate();

        // Score normalization clamps to the 0..1 entity range.
        var high = TimingOptimizationService.BuildSyncResult(
            tenantId, projectId, runId, segmentId, 2000, 2000, 1.0, 1.0, 150.0, SyncStatus.SyncAcceptable);
        Assert.Equal(1.0, high.SyncScore, precision: 5);

        var low = TimingOptimizationService.BuildSyncResult(
            tenantId, projectId, runId, segmentId, 2000, 9000, 1.0, 1.0, -10.0, SyncStatus.ManualReviewRequired);
        Assert.Equal(0.0, low.SyncScore, precision: 5);

        // Documented formula: perfect fit scores 100; 500ms error costs 50.
        Assert.Equal(100.0, TimingOptimizationService.ComputeSyncScore(0, 0, 0, 0, 0), precision: 5);
        Assert.Equal(50.0, TimingOptimizationService.ComputeSyncScore(500, 0, 0, 0, 0), precision: 5);
        Assert.Equal(
            SyncStatus.SyncAcceptable,
            TimingOptimizationService.Classify(50, 50, 100, false));
        Assert.Equal(
            SyncStatus.SyncAcceptableWithWarning,
            TimingOptimizationService.Classify(100, 50, 100, false));
    }

    [Fact]
    public void Exhausted_Creates_Review()
    {
        var options = Defaults();
        var segmentId = Guid.NewGuid();

        // Mock TTS durations that can never fit: every candidate stays 4000ms
        // away from a 1000ms window even after capped prosody/stretch.
        var plan = TimingOptimizationService.Plan(1000, 5000, [5050, 4950], [4980, 5020], options);

        Assert.Equal(SyncStatus.ManualReviewRequired, plan.FinalStatus);

        var payload = TimingOptimizationService.BuildReviewPayload(
            segmentId, 3, 1000, plan.Attempts[plan.ChosenIndex].DurationMs,
            plan.FinalScore, plan.FinalStatus, plan.Attempts.Count);

        Assert.Contains(TimingOptimizationService.ReviewReason, payload, StringComparison.Ordinal);
        Assert.Contains(segmentId.ToString("N"), payload, StringComparison.Ordinal);
        Assert.Contains(SyncStatus.ManualReviewRequired.ToString(), payload, StringComparison.Ordinal);

        // Classification matrix: tolerance first, then attempt budget.
        Assert.Equal(
            SyncStatus.SyncAcceptable,
            TimingOptimizationService.Classify(0, 50, 100, false));
        Assert.Equal(
            SyncStatus.SyncRetryable,
            TimingOptimizationService.Classify(101, 50, 100, true));
        Assert.Equal(
            SyncStatus.ManualReviewRequired,
            TimingOptimizationService.Classify(101, 50, 100, false));
        Assert.Equal("TIMING_SYNC", TimingOptimizationService.ReviewReason);
        Assert.Equal("1", TimingOptimizationService.SchemaVersion);
    }
}
