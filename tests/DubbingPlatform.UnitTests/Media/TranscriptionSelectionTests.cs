using DubbingPlatform.Application.Errors;
using DubbingPlatform.Application.Options;
using DubbingPlatform.Application.Services;

namespace DubbingPlatform.UnitTests.Media;

/// <summary>
/// Hermetic transcription-selection tests (no Docker): deterministic
/// best-version ordering, provider-priority tiebreaks, options defaults, and
/// the retryable error contract use the pure
/// <see cref="TranscriptionService.PickBest"/> planner directly. DB
/// persistence lives in the service/worker (PG, covered in CI).
/// </summary>
public sealed class TranscriptionSelectionTests
{
    private static Dictionary<string, int> Priority(params string[] providers)
    {
        var options = new ProviderOptions
        {
            RoutePriority = new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["Transcription"] = providers,
            },
        };
        return TranscriptionService.BuildPriorityIndex(options, "Transcription");
    }

    private static TranscriptCandidate Candidate(
        string provider,
        double confidence,
        int textLength = 10,
        int wordCount = 3,
        string? versionId = null,
        DateTimeOffset? createdAt = null)
    {
        return new TranscriptCandidate(
            string.IsNullOrWhiteSpace(versionId) ? Guid.NewGuid() : Guid.Parse(versionId),
            provider, confidence, textLength, wordCount,
            createdAt ?? DateTimeOffset.UtcNow);
    }

    [Fact]
    public void Confidence_Wins()
    {
        var priority = Priority("mock");
        var low = Candidate("mock", 0.35);
        var high = Candidate("mock", 0.95);

        var best = TranscriptionService.PickBest([low, high], priority);

        Assert.NotNull(best);
        Assert.Equal(high.VersionId, best.VersionId);
    }

    [Fact]
    public void Priority_Breaks_Ties()
    {
        var priority = Priority("mock", "azure");
        var azure = Candidate("azure", 0.9);
        var mock = Candidate("mock", 0.9);

        var best = TranscriptionService.PickBest([azure, mock], priority);

        Assert.NotNull(best);
        Assert.Equal(mock.VersionId, best.VersionId);
    }

    [Fact]
    public void Completeness_Then_Words()
    {
        var priority = Priority("mock");
        var empty = Candidate("mock", 0.9, textLength: 0, wordCount: 0);
        var full = Candidate("mock", 0.9, textLength: 12, wordCount: 0);

        Assert.Equal(full.VersionId, TranscriptionService.PickBest([empty, full], priority)!.VersionId);

        var fewWords = Candidate("mock", 0.9, textLength: 12, wordCount: 1);
        var manyWords = Candidate("mock", 0.9, textLength: 12, wordCount: 5);

        Assert.Equal(manyWords.VersionId, TranscriptionService.PickBest([fewWords, manyWords], priority)!.VersionId);
    }

    [Fact]
    public void Earliest_Created_Breaks_Full_Ties()
    {
        var priority = Priority("mock");
        var first = Candidate("mock", 0.9, createdAt: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var second = Candidate("mock", 0.9, createdAt: new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero));

        Assert.Equal(first.VersionId, TranscriptionService.PickBest([second, first], priority)!.VersionId);
        Assert.Null(TranscriptionService.PickBest([], priority));
    }

    [Fact]
    public void Threshold_Defaults()
    {
        var options = new TranscriptionOptions();
        Assert.Equal(0.70, options.ConfidenceThreshold);
        Assert.Equal(3, options.MaxAttempts);
        Assert.Equal(50, options.BatchSize);

        var validator = new TranscriptionOptionsValidator();
        Assert.True(validator.Validate(null, options).Succeeded);
        Assert.False(validator.Validate(null, new TranscriptionOptions { ConfidenceThreshold = 1.5 }).Succeeded);
        Assert.False(validator.Validate(null, new TranscriptionOptions { MaxAttempts = 0 }).Succeeded);
        Assert.False(validator.Validate(null, new TranscriptionOptions { BatchSize = 0 }).Succeeded);
    }

    [Fact]
    public void Retryable_Carries_ProviderFailed()
    {
        var segmentId = Guid.NewGuid();
        var exception = new TranscriptionRetryableException(segmentId, 0.35, 0.70);

        Assert.Equal(ErrorCodes.ProviderFailed, exception.ErrorCode);
        Assert.Equal(segmentId, exception.SegmentId);
        Assert.Equal(TranscriptionService.ReviewReason, "LOW_CONFIDENCE");
    }
}
