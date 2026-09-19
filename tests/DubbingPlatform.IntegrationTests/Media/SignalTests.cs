using DubbingPlatform.Application.Abstractions;
using DubbingPlatform.Application.Options;
using DubbingPlatform.Domain.ValueObjects;
using DubbingPlatform.Infrastructure.Media;
using DubbingPlatform.Infrastructure.Processes;
using DubbingPlatform.IntegrationTests.Fixtures;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;

namespace DubbingPlatform.IntegrationTests.Media;

/// <summary>
/// Task 39: signal/golden tier. Hermetic facts pin the loudness and timing
/// golden thresholds (tolerances, never exact float equality); live facts
/// measure the repo fixtures with FFmpeg (skip without ffmpeg/fixtures, live
/// in CI). Expected fixture signals are documented in
/// <c>fixtures/README.md</c>.
/// </summary>
public sealed class SignalTests : TestFixtureBase
{
    private readonly ITestOutputHelper _output;

    public SignalTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void Loudness_Golden_Web_And_Broadcast()
    {
        Assert.True(Math.Abs(LoudnessTarget.WebDefault.IntegratedLufs - -16.0) <= 1.0);
        Assert.True(Math.Abs(LoudnessTarget.WebDefault.TruePeakDbtp - -1.0) <= 0.1);
        Assert.True(Math.Abs(LoudnessTarget.Broadcast.IntegratedLufs - -23.0) <= 1.0);
        Assert.True(Math.Abs(LoudnessTarget.Broadcast.TruePeakDbtp - -1.0) <= 0.1);
    }

    [Fact]
    public void Timing_Golden_Tolerances()
    {
        var window = new TimingWindow();
        Assert.Equal(50, window.PreferredToleranceMs);
        Assert.Equal(100, window.MaxToleranceMs);
        Assert.True(Math.Abs(window.MaxRateChangePercent - 15.0) <= 0.001);
        Assert.True(Math.Abs(window.MaxStretchFactor - 1.15) <= 0.001);
    }

    [Fact]
    public void Canonical_Audio_Defaults()
    {
        // Fixtures are mastered at the canonical 48kHz stereo form; the same
        // defaults backstop probes with missing stream metadata.
        var probe = new FfprobeResult(
            "wav",
            4000,
            [new FfprobeStream("audio", "pcm_s16le", null, null, null, null, null, null)]);
        var outcome = DubbingPlatform.Application.Services.MediaValidator.Validate(probe, 768078, new MediaOptions());
        Assert.True(outcome.IsValid);
        Assert.Equal(48000, outcome.SampleRate);
        Assert.Equal(2, outcome.Channels);
        Assert.Equal("stereo", outcome.ChannelLayout);
    }

    [SkippableFact]
    public async Task Fixture_Silence_Measures_Silent()
    {
        await SkipUnlessFfmpegAsync(_output).ConfigureAwait(true);
        var silence = RequireFixture("silence.wav", _output);
        var ffmpeg = CreateFfmpeg();
        var workDir = ProcessRunner.CreateTempWorkingDir();
        try
        {
            var maxSilence = await ffmpeg.MeasureMaxSilenceSecAsync(silence, workDir, -60.0, 10.0, CancellationToken.None).ConfigureAwait(true);
            Assert.True(maxSilence >= 8.0, $"silence.wav must measure >= 8s of silence (measured {maxSilence:F1}s).");

            var dialogue = await ffmpeg.MeasureChannelRmsDbAsync(silence, workDir, 10000, 1500, CancellationToken.None).ConfigureAwait(true);
            Assert.All(dialogue, level => Assert.True(level > -70.0, "Trailing 2s tone must be audible."));
        }
        finally
        {
            DeleteDirQuietly(workDir);
        }
    }

    [SkippableFact]
    public async Task Fixture_SingleSpeaker_Loudness_Sane()
    {
        await SkipUnlessFfmpegAsync(_output).ConfigureAwait(true);
        var single = RequireFixture("single-speaker.wav", _output);
        var ffmpeg = CreateFfmpeg();
        var workDir = ProcessRunner.CreateTempWorkingDir();
        try
        {
            var loudness = await ffmpeg.MeasureSignalLoudnessAsync(single, workDir, CancellationToken.None).ConfigureAwait(true);
            Assert.True(
                loudness.IntegratedLufs < 0.0 && loudness.IntegratedLufs > -70.0,
                $"Integrated {loudness.IntegratedLufs:F2} LUFS must be sane.");
            Assert.True(
                loudness.TruePeakDbtp < 0.0,
                $"True-peak {loudness.TruePeakDbtp:F2} dBTP must be below full scale.");
            _output.WriteLine($"single-speaker.wav: {loudness.IntegratedLufs:F2} LUFS, peak {loudness.TruePeakDbtp:F2} dBTP.");
        }
        finally
        {
            DeleteDirQuietly(workDir);
        }
    }

    [SkippableFact]
    public async Task Fixture_Noisy_Still_Audible()
    {
        await SkipUnlessFfmpegAsync(_output).ConfigureAwait(true);
        var noisy = RequireFixture("noisy.wav", _output);
        var ffmpeg = CreateFfmpeg();
        var workDir = ProcessRunner.CreateTempWorkingDir();
        try
        {
            var levels = await ffmpeg.MeasureChannelRmsDbAsync(noisy, workDir, null, null, CancellationToken.None).ConfigureAwait(true);
            Assert.Equal(2, levels.Count);
            Assert.All(levels, level => Assert.True(level > -70.0 && level < 0.0, $"Noisy fixture RMS {level:F2}dB must be audible and below full scale."));
        }
        finally
        {
            DeleteDirQuietly(workDir);
        }
    }

    private static FFmpegService CreateFfmpeg()
    {
        var runner = new ProcessRunner(NullLogger<ProcessRunner>.Instance);
        var storageMock = new Moq.Mock<IArtifactStorage>(Moq.MockBehavior.Strict);
        var mediaOptions = Microsoft.Extensions.Options.Options.Create(new MediaOptions());
        var ffprobe = new FFprobeService(
            storageMock.Object, runner, mediaOptions, NullLogger<FFprobeService>.Instance);
        return new FFmpegService(
            runner, ffprobe, mediaOptions, NullLogger<FFmpegService>.Instance);
    }
}
