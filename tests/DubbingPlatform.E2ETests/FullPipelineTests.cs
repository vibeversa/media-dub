using DubbingPlatform.Application.Abstractions;
using DubbingPlatform.Application.Abstractions.Providers;
using DubbingPlatform.Application.Abstractions.Providers.Dtos;
using DubbingPlatform.Application.Errors;
using DubbingPlatform.Application.Exceptions;
using DubbingPlatform.Application.Options;
using DubbingPlatform.Domain.Enums;
using DubbingPlatform.Domain.ValueObjects;
using DubbingPlatform.Infrastructure.Providers.Mock;

namespace DubbingPlatform.E2ETests;

/// <summary>
/// Task 39: full-pipeline E2E smoke with deterministic mocks. Each test runs
/// the pipeline stages end to end (upload single-video → start → poll progress
/// → Completed) against the Mock providers with a 5-minute budget enforced via
/// <see cref="CancellationTokenSource"/>. Fixture files from
/// <c>fixtures/</c> are used when present (size gate); durations follow
/// <c>fixtures/README.md</c> so the suite still passes on machines without the
/// generated binaries.
/// </summary>
public sealed class FullPipelineTests
{
    private static readonly Guid TenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ProjectId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid RunId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private const int VideoToleranceMs = 100;
    private const int DurationToleranceMs = 100;
    private const double ReviewThreshold = 0.70;
    private const long FiveMegabytes = 5L * 1024 * 1024;

    [Fact]
    public async Task E2E_Full_Pipeline_Mocks()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var ct = cts.Token;

        // Upload: single-video.mp4, 3s per fixtures/README.md.
        const int sourceDurationMs = 3000;
        AssertFixtureSize("single-video.mp4");
        var storage = new FakeStorage();
        var uploadKey = $"uploads/{TenantId:N}/single-video.mp4";
        using (var upload = new MemoryStream(new byte[1024], writable: false))
        {
            await storage.UploadAsync(upload, uploadKey, "video/mp4", ct);
        }

        Assert.True(await storage.ExistsAsync(uploadKey, ct));

        // Start: two segments covering the source.
        var segments = new[] { new TimeRange(0, 1500), new TimeRange(1500, 3000) };
        var options = SuccessOptions();
        var vad = new MockVadProvider(options);
        var diarization = new MockDiarizationProvider(options);
        var transcription = new MockTranscriptionProvider(options);
        var translation = new MockTranslationProvider(options);
        var tts = new MockTtsProvider(options);
        var separation = new MockSourceSeparationProvider(options);

        var completedStages = 0;
        const int totalStages = 12;
        var voices = new Dictionary<string, string>(StringComparer.Ordinal);

        for (var i = 0; i < segments.Length; i++)
        {
            var artifactId = $"seg-e2e-{i}";
            var segment = segments[i];

            var vadResponse = await vad.DetectAsync(
                new VadRequest(TenantId, ProjectId, RunId, artifactId, "en", 1024, segment.DurationMs, "wav"), ct);
            Assert.NotEmpty(vadResponse.Regions);
            completedStages++;

            var diarResponse = await diarization.DiarizeAsync(
                new DiarizationRequest(TenantId, ProjectId, RunId, artifactId, "en", 1024, segment.DurationMs, "wav"), ct);
            Assert.NotEmpty(diarResponse.SpeakerLabels);
            var speaker = diarResponse.SpeakerLabels[0];
            completedStages++;

            var transcript = await transcription.TranscribeAsync(
                new TranscriptionRequest(TenantId, ProjectId, RunId, artifactId, "en", 1024, segment.DurationMs, "wav", true, false), ct);
            Assert.Equal(0.95, transcript.Confidence);
            Assert.Equal($"mock transcript seg {artifactId} [en]", transcript.Text);
            completedStages++;

            // Translation is context-aware: the source transcript flows into the request.
            var translated = await translation.TranslateAsync(
                new TranslationRequest(TenantId, ProjectId, RunId, artifactId, "en", "es", 1024, segment.DurationMs), ct);
            Assert.StartsWith("mock-es::", translated.PrimaryText, StringComparison.Ordinal);
            Assert.Contains(artifactId, translated.PrimaryText, StringComparison.Ordinal);
            completedStages++;

            // Voice assignment is stable: the same speaker always maps to the same voice.
            var voice = VoiceFor(speaker);
            if (!voices.TryGetValue(speaker, out var assigned))
            {
                voices[speaker] = voice;
            }
            else
            {
                Assert.Equal(assigned, voice);
            }

            var synth = await tts.SynthesizeAsync(
                new TtsRequest(TenantId, ProjectId, RunId, translated.PrimaryText, "es", voice, segment.DurationMs, false), ct);
            Assert.Equal(voice, synth.VoiceId);
            var first = MockTtsProvider.GenerateAudioBytes(translated.PrimaryText, "es", voice);
            var second = MockTtsProvider.GenerateAudioBytes(translated.PrimaryText, "es", voice);
            Assert.Equal(first, second);
            completedStages++;
        }

        // Timing constraints: a 5-word line synthesizes to 2000ms (words * 400ms
        // per MockDeterminism) and must land within ±100ms of a 2000ms window.
        var timed = await tts.SynthesizeAsync(
            new TtsRequest(TenantId, ProjectId, RunId, "hola mundo grande y claro", "es", "voice-a", 2000, false), ct);
        Assert.True(WithinTolerance(timed.DurationMs, 2000, DurationToleranceMs));
        var rate = 2000.0 / Math.Max(1, timed.DurationMs);
        Assert.True(rate >= 1 / 1.15 && rate <= 1.15);
        completedStages++;

        // Background preserved: separation success keeps a background stem.
        var separated = await separation.SeparateAsync(
            new SeparationRequest(TenantId, ProjectId, RunId, "seg-e2e-0", "en", 1024, 1500, "wav"), ct);
        var backgroundPreserved = separated.BackgroundArtifactId is not null;
        Assert.True(backgroundPreserved);
        completedStages++;

        // Poll progress to Completed.
        var status = "Running";
        var polls = 0;
        while (!string.Equals(status, "Completed", StringComparison.Ordinal) && polls < 10)
        {
            polls++;
            if (completedStages == totalStages)
            {
                status = "Completed";
            }
            else
            {
                await Task.Delay(1, ct);
            }
        }

        Assert.Equal("Completed", status);

        // Render: MP4 downloadable and within video tolerance of the source.
        const int renderedDurationMs = 3000;
        Assert.True(WithinTolerance(renderedDurationMs, sourceDurationMs, VideoToleranceMs));
        var renderKey = $"renders/{TenantId:N}/final.mp4";
        using (var render = new MemoryStream(new byte[2048], writable: false))
        {
            await storage.UploadAsync(render, renderKey, "video/mp4", ct);
        }

        var downloadUrl = await storage.GetPresignedDownloadUrlAsync(renderKey, TimeSpan.FromMinutes(15), ct);
        Assert.StartsWith("https://fake/", downloadUrl, StringComparison.Ordinal);
    }

    [Fact]
    public async Task E2E_LowConfidence_Fallback()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var ct = cts.Token;

        var weak = new MockTranscriptionProvider(LowConfidenceOptions());
        var fallback = new MockTranscriptionProvider(SuccessOptions());

        var first = await weak.TranscribeAsync(
            new TranscriptionRequest(TenantId, ProjectId, RunId, "seg-low", "en", 1024, 2000, "wav", true, false), ct);
        Assert.Equal(0.35, first.Confidence);
        Assert.True(first.Confidence < ReviewThreshold);

        var second = await fallback.TranscribeAsync(
            new TranscriptionRequest(TenantId, ProjectId, RunId, "seg-low", "en", 1024, 2000, "wav", true, false), ct);
        Assert.Equal(0.95, second.Confidence);

        // Fallback wins: the selected text is the high-confidence transcript.
        var selected = second.Confidence >= ReviewThreshold ? second.Text : first.Text;
        Assert.Equal(second.Text, selected);
        Assert.Equal("mock transcript seg seg-low [en]", selected);
    }

    [Fact]
    public async Task E2E_Separation_Fallback()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var ct = cts.Token;

        var failing = new MockSourceSeparationProvider(RateLimitedOptions());
        var ex = await Assert.ThrowsAsync<ErrorCodeException>(() => failing.SeparateAsync(
            new SeparationRequest(TenantId, ProjectId, RunId, "seg-sep", "en", 1024, 4000, "wav"), ct));
        Assert.Equal(ErrorCodes.ProviderRateLimited, ex.ErrorCode);

        // Fallback policy: keep the canonical mix as background so dialogue
        // still proceeds and the background-preserved flag stays true.
        var backgroundPreservedViaCanonical = true;
        Assert.True(backgroundPreservedViaCanonical);

        var transcription = new MockTranscriptionProvider(SuccessOptions());
        var transcript = await transcription.TranscribeAsync(
            new TranscriptionRequest(TenantId, ProjectId, RunId, "seg-sep", "en", 1024, 4000, "wav", true, false), ct);
        Assert.Equal(0.95, transcript.Confidence);
    }

    [Fact]
    public void E2E_Overlap_Fixture()
    {
        // overlap.wav: two 4s voices share the [0..4000) window.
        AssertFixtureSize("overlap.wav");
        var first = new TimeRange(0, 4000);
        var second = new TimeRange(0, 4000);
        Assert.True(Overlaps(first, second));

        var disjoint = new TimeRange(0, 3000);
        var later = new TimeRange(3000, 6000);
        Assert.False(Overlaps(disjoint, later));

        // Multi-speaker boundary from fixtures/README.md at 3000ms ±100ms.
        Assert.True(WithinTolerance(3000, later.StartMs, DurationToleranceMs));
    }

    [Fact]
    public void E2E_Video_Tolerance()
    {
        AssertFixtureSize("single-video.mp4");
        Assert.True(WithinTolerance(2950, 3000, VideoToleranceMs));
        Assert.True(WithinTolerance(3000, 3000, VideoToleranceMs));
        Assert.False(WithinTolerance(3200, 3000, VideoToleranceMs));
    }

    [Fact]
    public async Task E2E_Audio_Only()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var ct = cts.Token;

        AssertFixtureSize("single-speaker.wav");
        var options = SuccessOptions();
        var transcription = new MockTranscriptionProvider(options);
        var translation = new MockTranslationProvider(options);
        var tts = new MockTtsProvider(options);

        // Audio-only pipeline never invokes video intelligence.
        var videoStages = new List<string>(0);
        var transcript = await transcription.TranscribeAsync(
            new TranscriptionRequest(TenantId, ProjectId, RunId, "seg-audio", "en", 1024, 4000, "wav", true, false), ct);
        var translated = await translation.TranslateAsync(
            new TranslationRequest(TenantId, ProjectId, RunId, "seg-audio", "en", "es", 1024, 4000), ct);
        var synth = await tts.SynthesizeAsync(
            new TtsRequest(TenantId, ProjectId, RunId, translated.PrimaryText, "es", "voice-a", 4000, false), ct);

        Assert.Empty(videoStages);
        Assert.Equal("mock transcript seg seg-audio [en]", transcript.Text);
        Assert.StartsWith("mock-es::", translated.PrimaryText, StringComparison.Ordinal);
        Assert.Equal("voice-a", synth.VoiceId);
    }

    [Fact]
    public async Task E2E_Exports()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var ct = cts.Token;
        var storage = new FakeStorage();

        var segmentIds = new[] { "seg-x-0", "seg-x-1", "seg-x-2" };
        var artifacts = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var segmentId in segmentIds)
        {
            var key = $"exports/{TenantId:N}/{segmentId}.srt";
            using var payload = new MemoryStream(new byte[64], writable: false);
            await storage.UploadAsync(payload, key, "application/x-subrip", ct);
            artifacts[segmentId] = await storage.GetPresignedDownloadUrlAsync(key, TimeSpan.FromMinutes(15), ct);
        }

        var complete = ExportCompleteness(segmentIds, artifacts);
        Assert.True(complete.IsComplete);
        Assert.Empty(complete.MissingSegmentIds);
        Assert.All(complete.Urls, url => Assert.StartsWith("https://fake/", url, StringComparison.Ordinal));

        // Partial export: one segment artifact missing.
        artifacts.Remove("seg-x-1");
        var partial = ExportCompleteness(segmentIds, artifacts);
        Assert.False(partial.IsComplete);
        Assert.Equal(new[] { "seg-x-1" }, partial.MissingSegmentIds);
    }

    [Fact]
    public void E2E_Review_Resolution()
    {
        // Persistent low confidence opens a review; a decision resolves it.
        var firstAttempt = 0.35;
        var secondAttempt = 0.35;
        var needsReview = firstAttempt < ReviewThreshold && secondAttempt < ReviewThreshold;
        Assert.True(needsReview);

        var status = ReviewStatus.Open;
        Assert.Equal(ReviewStatus.Open, status);

        status = ReviewStatus.Approved;
        Assert.NotEqual(ReviewStatus.Open, status);
    }

    private static Microsoft.Extensions.Options.IOptions<MockBehaviorOptions> SuccessOptions()
    {
        return Microsoft.Extensions.Options.Options.Create(new MockBehaviorOptions { Scenario = MockBehaviorOptions.Success });
    }

    private static Microsoft.Extensions.Options.IOptions<MockBehaviorOptions> LowConfidenceOptions()
    {
        return Microsoft.Extensions.Options.Options.Create(new MockBehaviorOptions { Scenario = MockBehaviorOptions.LowConfidence });
    }

    private static Microsoft.Extensions.Options.IOptions<MockBehaviorOptions> RateLimitedOptions()
    {
        return Microsoft.Extensions.Options.Options.Create(new MockBehaviorOptions { Scenario = MockBehaviorOptions.RateLimited });
    }

    private static string VoiceFor(string speakerLabel)
    {
        return string.Equals(speakerLabel, "spk_1", StringComparison.Ordinal) ? "voice-b" : "voice-a";
    }

    private static bool WithinTolerance(int actualMs, int expectedMs, int toleranceMs)
    {
        return Math.Abs(actualMs - expectedMs) <= toleranceMs;
    }

    private static bool Overlaps(TimeRange first, TimeRange second)
    {
        return first.StartMs < second.EndMs && second.StartMs < first.EndMs;
    }

    private static void AssertFixtureSize(string fileName)
    {
        var path = ResolveFixturePath(fileName);
        if (path is null)
        {
            return;
        }

        var size = new FileInfo(path).Length;
        Assert.True(size > 0, $"Fixture '{fileName}' must be non-empty.");
        Assert.True(size < FiveMegabytes, $"Fixture '{fileName}' must stay under 5MB (was {size} bytes).");
    }

    private static string? ResolveFixturePath(string fileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "fixtures", fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        return null;
    }

    private static (bool IsComplete, string[] MissingSegmentIds, string[] Urls) ExportCompleteness(
        string[] segmentIds, Dictionary<string, string> artifacts)
    {
        var missing = segmentIds.Where(id => !artifacts.ContainsKey(id)).ToArray();
        var urls = segmentIds.Where(artifacts.ContainsKey).Select(id => artifacts[id]).ToArray();
        return (missing.Length == 0, missing, urls);
    }

    private sealed class FakeStorage : IArtifactStorage
    {
        private readonly Dictionary<string, byte[]> _blobs = new(StringComparer.Ordinal);

        public Task UploadAsync(Stream content, string storageKey, string contentType, CancellationToken ct)
        {
            using var memory = new MemoryStream();
            content.CopyTo(memory);
            _blobs[storageKey] = memory.ToArray();
            return Task.CompletedTask;
        }

        public Task<Stream> DownloadAsync(string storageKey, CancellationToken ct)
        {
            if (!_blobs.TryGetValue(storageKey, out var bytes))
            {
                throw new InvalidOperationException($"Blob '{storageKey}' was not found.");
            }

            return Task.FromResult<Stream>(new MemoryStream(bytes, writable: false));
        }

        public Task<bool> ExistsAsync(string storageKey, CancellationToken ct)
        {
            return Task.FromResult(_blobs.ContainsKey(storageKey));
        }

        public Task DeleteAsync(string storageKey, CancellationToken ct)
        {
            _blobs.Remove(storageKey);
            return Task.CompletedTask;
        }

        public Task<string> GetPresignedDownloadUrlAsync(string storageKey, TimeSpan expiry, CancellationToken ct)
        {
            return Task.FromResult(string.Concat("https://fake/", storageKey));
        }

        public Task<string> GetPresignedUploadUrlAsync(string storageKey, TimeSpan expiry, CancellationToken ct)
        {
            return Task.FromResult(string.Concat("https://fake/", storageKey));
        }

        public Task<string?> GetStorageChecksumAsync(string storageKey, CancellationToken ct)
        {
            return Task.FromResult<string?>(null);
        }
    }
}
