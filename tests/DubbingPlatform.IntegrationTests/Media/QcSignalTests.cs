using System.Globalization;
using DubbingPlatform.Application.Abstractions;
using DubbingPlatform.Application.Options;
using DubbingPlatform.Infrastructure.Media;
using DubbingPlatform.Infrastructure.Processes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;

namespace DubbingPlatform.IntegrationTests.Media;

/// <summary>
/// Task 032: FFmpeg signal probes used by quality control over sine fixtures
/// (requires ffmpeg/ffprobe). Skips with an explicit message when ffmpeg is
/// unavailable (CI runs live). No database is needed.
/// </summary>
public sealed class QcSignalTests
{
    private readonly ITestOutputHelper _output;

    public QcSignalTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [SkippableFact]
    public async Task Channel_Levels_Stereo()
    {
        await EnsureFfmpegAsync().ConfigureAwait(true);
        var ffmpeg = CreateFfmpeg();
        var workDir = ProcessRunner.CreateTempWorkingDir();
        try
        {
            var sine = Path.Combine(workDir, "sine.wav");
            await GenerateSineAsync(440, 2, sine).ConfigureAwait(true);

            var levels = await ffmpeg.MeasureChannelRmsDbAsync(sine, workDir, null, null, CancellationToken.None).ConfigureAwait(true);

            Assert.Equal(2, levels.Count);
            Assert.All(levels, l => Assert.True(l > -70.0 && l < 0.0, $"Channel RMS {l:F2}dB must be audible and below full scale."));
            _output.WriteLine($"Stereo RMS: {levels[0]:F2}dB / {levels[1]:F2}dB.");

            var window = await ffmpeg.MeasureChannelRmsDbAsync(sine, workDir, 0, 500, CancellationToken.None).ConfigureAwait(true);
            Assert.Equal(2, window.Count);
            Assert.All(window, l => Assert.True(l > -70.0, "Dialogue window must be audible."));
        }
        finally
        {
            DeleteDirQuietly(workDir);
        }
    }

    [SkippableFact]
    public async Task Silence_Detected()
    {
        await EnsureFfmpegAsync().ConfigureAwait(true);
        var ffmpeg = CreateFfmpeg();
        var workDir = ProcessRunner.CreateTempWorkingDir();
        try
        {
            var sine = Path.Combine(workDir, "sine.wav");
            await GenerateSineAsync(440, 2, sine).ConfigureAwait(true);
            var busy = await ffmpeg.MeasureMaxSilenceSecAsync(sine, workDir, -60.0, 10.0, CancellationToken.None).ConfigureAwait(true);
            Assert.True(busy < 10.0, $"2s sine must not contain 10s of silence (measured {busy:F1}s).");

            var silent = Path.Combine(workDir, "silent.wav");
            await GenerateSilenceAsync(12, silent).ConfigureAwait(true);
            var quiet = await ffmpeg.MeasureMaxSilenceSecAsync(silent, workDir, -60.0, 10.0, CancellationToken.None).ConfigureAwait(true);
            Assert.True(quiet >= 10.0, $"12s silence must measure >= 10s (measured {quiet:F1}s).");
        }
        finally
        {
            DeleteDirQuietly(workDir);
        }
    }

    [SkippableFact]
    public async Task Loudness_Measured()
    {
        await EnsureFfmpegAsync().ConfigureAwait(true);
        var ffmpeg = CreateFfmpeg();
        var workDir = ProcessRunner.CreateTempWorkingDir();
        try
        {
            var sine = Path.Combine(workDir, "sine.wav");
            await GenerateSineAsync(440, 2, sine).ConfigureAwait(true);

            var loudness = await ffmpeg.MeasureSignalLoudnessAsync(sine, workDir, CancellationToken.None).ConfigureAwait(true);

            Assert.True(loudness.IntegratedLufs < 0.0 && loudness.IntegratedLufs > -70.0, $"Integrated {loudness.IntegratedLufs:F2} LUFS must be sane.");
            Assert.True(loudness.TruePeakDbtp < 0.0, $"True-peak {loudness.TruePeakDbtp:F2} dBTP must be below full scale for a sine fixture.");
            _output.WriteLine($"Sine loudness: {loudness.IntegratedLufs:F2} LUFS, peak {loudness.TruePeakDbtp:F2} dBTP.");
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

    private static async Task GenerateSineAsync(int frequencyHz, int durationSec, string destPath)
    {
        var runner = new ProcessRunner(NullLogger<ProcessRunner>.Instance);
        var workDir = ProcessRunner.CreateTempWorkingDir();
        try
        {
            var args = new List<string>
            {
                "-y", "-f", "lavfi", "-i",
                string.Concat("sine=frequency=", frequencyHz.ToString(CultureInfo.InvariantCulture), ":duration=", durationSec.ToString(CultureInfo.InvariantCulture), ":sample_rate=48000"),
                "-c:a", "pcm_s16le", "-ac", "2", destPath,
            };

            var result = await runner.RunAsync(
                "ffmpeg", args, workDir, TimeSpan.FromSeconds(90), CancellationToken.None).ConfigureAwait(true);
            if (result.TimedOut || result.ExitCode != 0)
            {
                throw new InvalidOperationException($"ffmpeg sine fixture failed (exit {result.ExitCode}, timedOut={result.TimedOut}): {result.StdErr}");
            }
        }
        finally
        {
            DeleteDirQuietly(workDir);
        }
    }

    private static async Task GenerateSilenceAsync(int durationSec, string destPath)
    {
        var runner = new ProcessRunner(NullLogger<ProcessRunner>.Instance);
        var workDir = ProcessRunner.CreateTempWorkingDir();
        try
        {
            var args = new List<string>
            {
                "-y", "-f", "lavfi", "-i",
                string.Concat("anullsrc=sample_rate=48000:channel_layout=stereo:duration=", durationSec.ToString(CultureInfo.InvariantCulture)),
                "-c:a", "pcm_s16le", destPath,
            };

            var result = await runner.RunAsync(
                "ffmpeg", args, workDir, TimeSpan.FromSeconds(90), CancellationToken.None).ConfigureAwait(true);
            if (result.TimedOut || result.ExitCode != 0)
            {
                throw new InvalidOperationException($"ffmpeg silence fixture failed (exit {result.ExitCode}, timedOut={result.TimedOut}): {result.StdErr}");
            }
        }
        finally
        {
            DeleteDirQuietly(workDir);
        }
    }

    private async Task EnsureFfmpegAsync()
    {
        try
        {
            var runner = new ProcessRunner(NullLogger<ProcessRunner>.Instance);
            var workingDir = ProcessRunner.CreateTempWorkingDir();
            try
            {
                var result = await runner.RunAsync(
                    "ffprobe",
                    ["-version"],
                    workingDir,
                    TimeSpan.FromSeconds(15),
                    CancellationToken.None).ConfigureAwait(true);
                if (!result.TimedOut && result.ExitCode == 0)
                {
                    return;
                }

                Skip.If(true, $"ffmpeg/ffprobe missing: ffprobe -version exited {result.ExitCode} (timedOut={result.TimedOut}). Install ffmpeg to run QC signal tests.");
            }
            finally
            {
                DeleteDirQuietly(workingDir);
            }
        }
#pragma warning disable CA1031 // Availability probe: any failure means skip with an explicit message.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _output.WriteLine($"ffmpeg/ffprobe missing, skipping QC signal test: {ex.Message}");
            Skip.If(true, $"ffmpeg/ffprobe missing: {ex.Message}. Install ffmpeg to run QC signal tests.");
        }

        throw new InvalidOperationException("Unreachable: Skip.If always throws.");
    }

    private static void DeleteDirQuietly(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch (Exception)
        {
            // Best effort.
        }
    }
}
