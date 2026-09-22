using System;
using System.Collections.Generic;
using System.Linq;
using DubbingPlatform.Domain.Enums;
using DubbingPlatform.Domain.ValueObjects;

namespace DubbingPlatform.UnitTests.Domain;

public sealed class EnumAndValueObjectTests
{
    [Fact]
    public void All_Enums_Contain_Expected_Members()
    {
        var expectations = new Dictionary<Type, string[]>
        {
            [typeof(ProjectStatus)] = new[] { "Created", "Uploading", "MediaReady", "MediaRejected", "Processing", "Cancelling", "Cancelled", "Completed", "Failed", "ManualReviewRequired" },
            [typeof(ProcessingRunStatus)] = new[] { "Pending", "Running", "Completed", "Failed", "Cancelling", "Cancelled", "ManualReviewRequired" },
            [typeof(StageStatus)] = new[] { "Pending", "Scheduled", "Running", "Completed", "Failed", "RetryPending", "Cancelled", "ManualReviewRequired", "Skipped" },
            [typeof(StageType)] = new[] { "MediaValidation", "MediaAnalysis", "AudioPreparation", "SourceSeparation", "Vad", "SegmentBuild", "Diarization", "Transcription", "ContextBuild", "Translation", "VoiceAssignment", "VoiceGeneration", "TimingOptimization", "TimelineAssembly", "AudioMixing", "QualityControl", "Render" },
            [typeof(ScopeType)] = new[] { "Run", "Project", "Speaker", "Window", "Segment" },
            [typeof(OutcomeClass)] = new[] { "Success", "ProviderUnavailable", "ProviderRateLimited", "ProviderTransientFailure", "ProviderPermanentFailure", "ProviderInvalidResponse", "ProviderTimeout", "QualityBelowThreshold", "PolicyRejected", "UnsupportedCapability", "Cancelled" },
            [typeof(FailureCategory)] = new[] { "Validation", "MediaUnsupported", "MediaCorrupt", "ProviderTransient", "ProviderPermanent", "ProviderRateLimited", "ProviderTimeout", "ProviderInvalidResponse", "QuotaExceeded", "RateLimited", "LeaseLost", "Cancelled", "PolicyDenied", "ConsentRequired", "ConfigurationError", "StorageUnavailable", "ChecksumMismatch", "InvariantViolation", "Unknown" },
            [typeof(AssetType)] = new[] { "SourceOriginal", "CanonicalAudio", "WorkingAudio", "DialogueStem", "BackgroundStem", "VadRegions", "Segments", "DiarizationMap", "Transcript", "Translation", "GeneratedAudioPreview", "GeneratedAudioFinal", "Timeline", "MixedAudio", "QcReport", "RenderedOutput", "Export", "FfprobeAnalysis", "ContextWindow" },
            [typeof(ArtifactType)] = new[] { "SourceOriginal", "CanonicalAudio", "WorkingAudio", "DialogueStem", "BackgroundStem", "VadRegions", "Segments", "DiarizationMap", "Transcript", "Translation", "GeneratedAudioPreview", "GeneratedAudioFinal", "Timeline", "MixedAudio", "QcReport", "RenderedOutput", "Export", "FfprobeAnalysis", "ContextWindow", "Enrichment", "MediaPreviewAudio", "WaveformPeaks", "VideoPreview", "VoicePreviewAudio", "QcEvidenceArtifact" },
            [typeof(ArtifactStatus)] = new[] { "Pending", "Committed", "Deleted" },
            [typeof(ContentObjectStatus)] = new[] { "Pending", "Committed", "Orphaned", "Deleted" },
            [typeof(UploadStatus)] = new[] { "Created", "InProgress", "Completed", "Aborted", "Expired", "Duplicate" },
            [typeof(MediaAssetStatus)] = new[] { "Pending", "Valid", "Invalid" },
            [typeof(QualityStatus)] = new[] { "Pass", "PassWithWarnings", "RetryRequired", "ManualReviewRequired", "Blocked" },
            [typeof(SyncStatus)] = new[] { "SyncAcceptable", "SyncAcceptableWithWarning", "SyncRetryable", "ManualReviewRequired" },
            [typeof(ProviderType)] = new[] { "Mock", "Azure", "OpenAI", "Google", "LocalInference" },
            [typeof(ProviderCapability)] = new[] { "Vad", "Diarization", "Transcription", "Translation", "Tts", "SourceSeparation", "VideoIntelligence", "LocalInference" },
            [typeof(VoiceType)] = new[] { "Stock", "Cloned", "Synthetic" },
            [typeof(ExportFormat)] = new[] { "Srt", "WebVtt", "JsonTimeline", "SpeakerMetadataJson", "TranscriptJson", "TranslationJson", "QualityReportJson" },
            [typeof(ExportJobStatus)] = new[] { "Pending", "Running", "Completed", "Failed", "Cancelled" },
            [typeof(ReviewStatus)] = new[] { "Open", "Approved", "Rejected", "Requeued", "ResolvedWithEdit" },
            [typeof(ReviewDecisionType)] = new[] { "Approve", "Reject", "Requeue", "ResolveWithEdit" },
            [typeof(AudioMixPolicy)] = new[] { "DuckBackground", "KeepBackground", "MuteBackground" },
            [typeof(SourceSeparationPolicy)] = new[] { "Disabled", "Enabled", "Auto" },
            [typeof(ConsentStatus)] = new[] { "Granted", "Revoked", "Expired", "Pending" },
            [typeof(VoicePreviewStatus)] = new[] { "Pending", "Running", "Completed", "Failed", "Cancelled" },
            [typeof(VoicePreviewQuotaCheck)] = new[] { "Allowed", "Denied" },
            [typeof(VoicePreviewConsentState)] = new[] { "Verified", "Blocked" },
        };

        Assert.Equal(28, expectations.Count);

        foreach (var (enumType, expectedMembers) in expectations)
        {
            Assert.True(enumType.IsEnum, $"{enumType.Name} must be an enum.");
            var actual = Enum.GetNames(enumType).ToHashSet(StringComparer.Ordinal);
            foreach (var member in expectedMembers)
            {
                Assert.True(actual.Contains(member), $"{enumType.Name} must contain member {member}.");
            }

            Assert.Equal(expectedMembers.Length, actual.Count);
        }
    }

    [Fact]
    public void TimeRange_Rejects_Negative_Start()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TimeRange(-1, 100));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TimeRange(100, 100));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TimeRange(200, 100));
    }

    [Fact]
    public void ContentHash_Rejects_Invalid_Hex()
    {
        Assert.Throws<ArgumentException>(() => new ContentHash("not-hex"));
        Assert.Throws<ArgumentException>(() => new ContentHash(new string('g', 64)));
        Assert.Throws<ArgumentException>(() => new ContentHash(new string('A', 64)));
        Assert.Throws<ArgumentException>(() => new ContentHash(new string('a', 63)));
    }

    [Fact]
    public void LoudnessTarget_Defaults_Are_Correct()
    {
        Assert.Equal(-16.0, LoudnessTarget.WebDefault.IntegratedLufs);
        Assert.Equal(-1.0, LoudnessTarget.WebDefault.TruePeakDbtp);
        Assert.Equal(-23.0, LoudnessTarget.Broadcast.IntegratedLufs);
        Assert.Equal(-1.0, LoudnessTarget.Broadcast.TruePeakDbtp);

        var @default = new LoudnessTarget();
        Assert.Equal(LoudnessTarget.WebDefault, @default);
    }

    [Fact]
    public void TimingWindow_Defaults_Are_Correct()
    {
        var window = new TimingWindow();

        Assert.Equal(50, window.PreferredToleranceMs);
        Assert.Equal(100, window.MaxToleranceMs);
        Assert.Equal(15.0, window.MaxRateChangePercent);
        Assert.Equal(1.15, window.MaxStretchFactor);
        Assert.Equal(50, window.AllowableLeadMs);
        Assert.Equal(50, window.AllowableLagMs);
    }

    [Fact]
    public void Money_Rejects_Negative()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Money(-0.01m, "USD"));

        var defaulted = new Money(1.0m, null);
        Assert.Equal("USD", defaulted.Currency);
    }
}
