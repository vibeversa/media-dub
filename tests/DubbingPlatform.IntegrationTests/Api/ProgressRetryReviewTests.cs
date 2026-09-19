using DubbingPlatform.Api.Controllers;
using DubbingPlatform.Application.Orchestration;
using DubbingPlatform.Application.Services;
using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Domain.Enums;
using DubbingPlatform.Domain.Exceptions;

namespace DubbingPlatform.IntegrationTests.Api;

/// <summary>
/// Task 035: durable progress, cancellation, dependency-aware retry, and
/// actionable review resolution. Fully hermetic (pure service helpers + state
/// machines, no database, no broker, no Docker) so these run everywhere
/// including CI without containers. DB-backed paths reuse the same pure
/// helpers plus the guarded-SQL patterns covered by existing pipeline suites.
/// </summary>
public sealed class ProgressRetryReviewTests
{
    private static ProcessingRun BuildRun(ProcessingRunStatus status)
    {
        var now = new DateTimeOffset(2026, 9, 17, 12, 0, 0, TimeSpan.Zero);
        return new ProcessingRun(
            Guid.Parse("22222222-2222-2222-2222-222222222222"),
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            Guid.Parse("33333333-3333-3333-3333-333333333333"),
            0, status, "1.0.0", "config-hash", "route-hash", "snapshot-hash",
            now, now, now, null);
    }

    private static List<RunStageSummary> BuildSummaries()
    {
        var now = new DateTimeOffset(2026, 9, 17, 12, 0, 0, TimeSpan.Zero);
        var tenant = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var run = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var summaries = new List<RunStageSummary>();
        foreach (var node in StageGraph.Nodes)
        {
            var expected = node.Scope is ScopeType.Project or ScopeType.Run ? 1 : 4;
            var completed = node.StageType is StageType.MediaValidation or StageType.MediaAnalysis ? expected : 0;
            summaries.Add(new RunStageSummary(
                Guid.NewGuid(), tenant, run, node.StageType, expected,
                completed, 0, 0, 0, 0, now, now));
        }

        return summaries;
    }

    private static ReviewItem BuildReview(string reason, ScopeType scope)
    {
        var now = new DateTimeOffset(2026, 9, 17, 12, 0, 0, TimeSpan.Zero);
        return new ReviewItem(
            Guid.NewGuid(),
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            Guid.Parse("33333333-3333-3333-3333-333333333333"),
            Guid.Parse("22222222-2222-2222-2222-222222222222"),
            scope, scope == ScopeType.Segment ? Guid.NewGuid().ToString("D") : Guid.NewGuid().ToString("D"),
            scope == ScopeType.Segment ? Guid.NewGuid() : null,
            ReviewStatus.Open, reason, null, now, now, null);
    }

    [Fact]
    public void Progress_Reflects_Units()
    {
        var projectId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var run = BuildRun(ProcessingRunStatus.Running);
        var summaries = BuildSummaries();

        var snapshot = ProgressService.BuildSnapshot(projectId, run, summaries, retryingUnits: 2, openReviews: []);

        Assert.Equal("speech", snapshot.Phase);
        Assert.Equal(nameof(StageType.AudioPreparation), snapshot.CurrentStage);
        Assert.Equal(2, snapshot.CompletedUnits);
        Assert.Equal(0, snapshot.FailedUnits);
        Assert.Equal(2, snapshot.RetryingUnits);
        Assert.Equal(0, snapshot.ReviewUnits);
        var expected = summaries.Sum(s => s.ExpectedUnits);
        Assert.Equal(expected, snapshot.ExpectedUnits);
        Assert.Equal(ProgressService.ComputePercentageIndicator(2, expected), snapshot.PercentageIndicator);
        Assert.True(snapshot.NotEta);
        Assert.Equal(expected - 2, snapshot.EstimatedRemaining);
        Assert.Empty(snapshot.Warnings);
    }

    [Fact]
    public void Cancel_Blocks_Work()
    {
        Assert.True(CancellationService.IsCancellableStatus(ProcessingRunStatus.Running));
        Assert.True(CancellationService.IsCancellableStatus(ProcessingRunStatus.ManualReviewRequired));
        Assert.True(CancellationService.IsCancellableStatus(ProcessingRunStatus.Cancelling));
        Assert.False(CancellationService.IsCancellableStatus(ProcessingRunStatus.Completed));
        Assert.False(CancellationService.IsCancellableStatus(ProcessingRunStatus.Failed));
        Assert.False(CancellationService.IsCancellableStatus(ProcessingRunStatus.Cancelled));

        var projectId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var cancelling = BuildRun(ProcessingRunStatus.Cancelling);
        var snapshot = ProgressService.BuildSnapshot(projectId, cancelling, BuildSummaries(), 0, []);
        Assert.Contains(snapshot.Warnings, w => w.Contains("cancel-pending", StringComparison.Ordinal));
    }

    [Fact]
    public void Retry_Invalidates_Dependents_Only()
    {
        var dependents = RetryService.GetTransitiveDependents(StageType.Transcription);

        Assert.Contains(StageType.Translation, dependents);
        Assert.Contains(StageType.VoiceGeneration, dependents);
        Assert.Contains(StageType.TimingOptimization, dependents);
        Assert.Contains(StageType.TimelineAssembly, dependents);
        Assert.Contains(StageType.AudioMixing, dependents);
        Assert.Contains(StageType.QualityControl, dependents);
        Assert.Contains(StageType.Render, dependents);
        Assert.DoesNotContain(StageType.Diarization, dependents);
        Assert.DoesNotContain(StageType.MediaValidation, dependents);
        Assert.DoesNotContain(StageType.Transcription, dependents);

        var order = StageGraph.Nodes.Select(n => n.StageType).ToList();
        var positions = dependents.Select(d => order.IndexOf(d)).ToList();
        Assert.Equal(positions.OrderBy(p => p).ToList(), positions);

        Assert.Equal(RetryService.RetryScope.Stage, RetryService.ParseScope("stage"));
        Assert.Equal(RetryService.RetryScope.Segment, RetryService.ParseScope("SEGMENT"));
        Assert.Equal(StageType.Translation, RetryService.ParseStage("translation"));
        Assert.Throws<DomainException>(() => RetryService.ParseScope("run"));
        Assert.Throws<DomainException>(() => RetryService.ParseStage("NotAStage"));
    }

    [Fact]
    public void Approve_Resumes()
    {
        Assert.True(ReviewService.ResumesRun(ReviewDecisionType.Approve, 0));
        Assert.True(ReviewService.ResumesRun(ReviewDecisionType.Requeue, 0));
        Assert.True(ReviewService.ResumesRun(ReviewDecisionType.ResolveWithEdit, 0));
        Assert.False(ReviewService.ResumesRun(ReviewDecisionType.Approve, 1));

        Assert.Equal(nameof(StageType.Translation), ReviewsController.ResolveResumeStage("TRANSLATION_QUALITY", ScopeType.Segment));
        Assert.Equal(nameof(StageType.Transcription), ReviewsController.ResolveResumeStage("LOW_CONFIDENCE", ScopeType.Segment));
        Assert.Equal(nameof(StageType.QualityControl), ReviewsController.ResolveResumeStage("QC_BLOCKED", ScopeType.Project));
        Assert.Equal("review.approve", ReviewService.AuditActionFor(ReviewDecisionType.Approve));
    }

    [Fact]
    public void Reject_Blocks()
    {
        Assert.False(ReviewService.ResumesRun(ReviewDecisionType.Reject, 0));
        Assert.False(ReviewService.ResumesRun(ReviewDecisionType.Reject, 2));

        var projectId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var run = BuildRun(ProcessingRunStatus.ManualReviewRequired);
        var open = new List<ReviewItem> { BuildReview("QC_BLOCKED", ScopeType.Project) };
        var snapshot = ProgressService.BuildSnapshot(projectId, run, BuildSummaries(), 0, open);

        Assert.Single(snapshot.Warnings, w => w.StartsWith("review:", StringComparison.Ordinal));
        Assert.Contains(snapshot.Warnings, w => w.Contains("unresolved-required-reviews-block-completion", StringComparison.Ordinal));
        Assert.Equal("speech", snapshot.Phase);
    }

    [Fact]
    public void Audit_Recorded()
    {
        Assert.Equal("processing.cancel", CancellationService.AuditAction);
        Assert.Equal("processing.retry", RetryService.AuditAction);
        Assert.Equal("review.approve", ReviewService.AuditActionFor(ReviewDecisionType.Approve));
        Assert.Equal("review.reject", ReviewService.AuditActionFor(ReviewDecisionType.Reject));
        Assert.Equal("review.requeue", ReviewService.AuditActionFor(ReviewDecisionType.Requeue));
        Assert.Equal("review.resolvewithedit", ReviewService.AuditActionFor(ReviewDecisionType.ResolveWithEdit));
    }

    [Fact]
    public void Resolve_Creates_Manual_Version()
    {
        Assert.Equal(ManualVersionKind.Translation, ReviewService.ResolveVersionKind("TRANSLATION_QUALITY"));
        Assert.Equal(ManualVersionKind.Transcript, ReviewService.ResolveVersionKind("LOW_CONFIDENCE"));
        Assert.Equal(ManualVersionKind.Transcript, ReviewService.ResolveVersionKind("QC_BLOCKED"));
        Assert.Equal(ManualVersionKind.Transcript, ReviewService.ResolveVersionKind(null));

        Assert.True(ReviewService.IsManualVersion("manual"));
        Assert.False(ReviewService.IsManualVersion("mock"));
        Assert.False(ReviewService.IsManualVersion(null));
        Assert.Equal("manual", ReviewService.ManualProvider);
        Assert.Equal("manual-review-v1", ReviewService.ManualModel);

        Assert.Equal("fixed text", ReviewService.RequireEditedText("  fixed text  "));
        Assert.Throws<DomainException>(() => ReviewService.RequireEditedText(null));
        Assert.Throws<DomainException>(() => ReviewService.RequireEditedText("   "));
    }
}
