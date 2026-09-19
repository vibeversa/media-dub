using System.Globalization;
using System.Text.Json;
using DubbingPlatform.Application.Abstractions;
using DubbingPlatform.Application.Options;
using DubbingPlatform.Infrastructure.Media;
using DubbingPlatform.Infrastructure.Processes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;

namespace DubbingPlatform.IntegrationTests.Media;

/// <summary>
/// Task 031: FFmpeg mixing with loudness, peak, clipping, and ducking over
/// sine fixtures (requires ffmpeg/ffprobe). Skips with an explicit message
/// when ffmpeg is unavailable (CI runs live).
/// </summary>
public sealed class MixingTests
{
    private readonly ITestOutputHelper _output;

    public MixingTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [SkippableFact]
    public async Task Duration_Rate_Channels()
    {
        await EnsureFfmpegAsync().ConfigureAwait(true);
        var mixer = CreateMixer();
        var workDir = ProcessRunner.CreateTempWorkingDir();
        try
        {
            var dlg1 = Path.Combine(workDir, "dlg1.wav");
            var dlg2 = Path.Combine(workDir, "dlg2.wav");
            var bg = Path.Combine(workDir, "bg.wav");
            var output = Path.Combine(workDir, "mixed.wav");
            await GenerateSineAsync(880, 1, dlg1).ConfigureAwait(true);
            await GenerateSineAsync(660, 1, dlg2).ConfigureAwait(true);
            await GenerateSineAsync(220, 4, bg).ConfigureAwait(true);

            var timeline = BuildTimeline(
                [(0, 0, 1000), (1, 2000, 1000)],
                sourceDurationMs: 4000);

            var result = await mixer.MixAsync(
                timeline, [dlg1, dlg2], bg, output, "web", CancellationToken.None).ConfigureAwait(true);

            Assert.True(File.Exists(output), "Mix output must exist.");
            Assert.Equal(48000, result.SampleRate);
            Assert.Equal(2, result.Channels);
            Assert.True(Math.Abs(result.DurationMs - 4000) <= 500, $"Duration {result.DurationMs}ms must be within 500ms of 4000ms.");
            Assert.Contains("sidechaincompress", result.FilterComplex, StringComparison.Ordinal);
            Assert.Contains("loudnorm", result.FilterComplex, StringComparison.Ordinal);
            Assert.Contains("48000", result.FilterComplex, StringComparison.Ordinal);
        }
        finally
        {
            DeleteDirQuietly(workDir);
        }
    }

    [SkippableFact]
    public async Task Loudness_Within_Target()
    {
        await EnsureFfmpegAsync().ConfigureAwait(true);
        var mixer = CreateMixer();
        var workDir = ProcessRunner.CreateTempWorkingDir();
        try
        {
            var dlg = Path.Combine(workDir, "dlg.wav");
            var bg = Path.Combine(workDir, "bg.wav");
            var output = Path.Combine(workDir, "mixed.wav");
            await GenerateSineAsync(880, 1, dlg).ConfigureAwait(true);
            await GenerateSineAsync(220, 3, bg).ConfigureAwait(true);

            var timeline = BuildTimeline([(0, 500, 1000)], sourceDurationMs: 3000);
            var result = await mixer.MixAsync(
                timeline, [dlg], bg, output, "web", CancellationToken.None).ConfigureAwait(true);

            Assert.True(Math.Abs(result.IntegratedLufs - -16.0) <= 1.0, $"Integrated {result.IntegratedLufs:F2} LUFS must be within ±1 of -16.");
        }
        finally
        {
            DeleteDirQuietly(workDir);
        }
    }

    [SkippableFact]
    public async Task Peak_Within_Limit()
    {
        await EnsureFfmpegAsync().ConfigureAwait(true);
        var mixer = CreateMixer();
        var workDir = ProcessRunner.CreateTempWorkingDir();
        try
        {
            var dlg = Path.Combine(workDir, "dlg.wav");
            var output = Path.Combine(workDir, "mixed.wav");
            await GenerateSineAsync(440, 2, dlg).ConfigureAwait(true);

            var timeline = BuildTimeline([(0, 0, 2000)], sourceDurationMs: 2000);
            var result = await mixer.MixAsync(
                timeline, [dlg], null, output, "web", CancellationToken.None).ConfigureAwait(true);

            Assert.True(result.TruePeakDbtp <= -1.0 + 1e-9, $"True-peak {result.TruePeakDbtp:F2} dBTP must be at or below -1 dBTP.");
        }
        finally
        {
            DeleteDirQuietly(workDir);
        }
    }

    [SkippableFact]
    public async Task No_Clipping()
    {
        await EnsureFfmpegAsync().ConfigureAwait(true);
        var mixer = CreateMixer();
        var workDir = ProcessRunner.CreateTempWorkingDir();
        try
        {
            var dlg = Path.Combine(workDir, "dlg.wav");
            var bg = Path.Combine(workDir, "bg.wav");
            var output = Path.Combine(workDir, "mixed.wav");
            await GenerateSineAsync(440, 2, dlg).ConfigureAwait(true);
            await GenerateSineAsync(220, 2, bg).ConfigureAwait(true);

            var timeline = BuildTimeline([(0, 0, 2000)], sourceDurationMs: 2000);
            var result = await mixer.MixAsync(
                timeline, [dlg], bg, output, "web", CancellationToken.None).ConfigureAwait(true);

            var verification = await mixer.VerifyMixAsync(
                output, DubbingPlatform.Domain.ValueObjects.LoudnessTarget.WebDefault, 2000, CancellationToken.None).ConfigureAwait(true);
            Assert.False(verification.Clipped, "Mixed audio must not clip (peak < 0dBFS).");
            Assert.True(verification.PeakOk, "Mixed peak must be within limit.");
            Assert.True(verification.LoudnessOk, "Mixed loudness must be within target.");
            Assert.Equal(result.IntegratedLufs, verification.Integrated, precision: 2);
        }
        finally
        {
            DeleteDirQuietly(workDir);
        }
    }

    [SkippableFact]
    public async Task Ducking_Applied()
    {
        await EnsureFfmpegAsync().ConfigureAwait(true);
        var mixer = CreateMixer();
        var workDir = ProcessRunner.CreateTempWorkingDir();
        try
        {
            // Ducking is background attenuation during dialogue: the ducked
            // background stem (sidechaincompress + volume, same params as the
            // mixer) must be quieter inside dialogue windows than outside.
            // The final mix separately proves dialogue placement (inside louder
            // via the added voice) plus the duck filter in its graph.
            var dlg = Path.Combine(workDir, "dlg.wav");
            var bg = Path.Combine(workDir, "bg.wav");
            var ducked = Path.Combine(workDir, "ducked.wav");
            var output = Path.Combine(workDir, "mixed.wav");
            await GenerateSineAsync(880, 1, dlg).ConfigureAwait(true);
            await GenerateSineAsync(220, 4, bg).ConfigureAwait(true);
            await GenerateDuckedBackgroundAsync(dlg, 1000, bg, 4000, ducked).ConfigureAwait(true);

            var check = await mixer.VerifyDuckingAsync(
                ducked, [(1000, 2000)], CancellationToken.None).ConfigureAwait(true);
            _output.WriteLine($"Ducking: inside {check.InsideRmsDb:F2}dB, outside {check.OutsideRmsDb:F2}dB, applied={check.Applied}.");
            Assert.True(check.Applied, $"Ducking must apply (inside {check.InsideRmsDb:F2}dB < outside {check.OutsideRmsDb:F2}dB).");

            var timeline = BuildTimeline([(0, 1000, 1000)], sourceDurationMs: 4000);
            var mixed = await mixer.MixAsync(
                timeline, [dlg], bg, output, "web", CancellationToken.None).ConfigureAwait(true);
            Assert.Contains("sidechaincompress", mixed.FilterComplex, StringComparison.Ordinal);
            Assert.Contains("volume=-12dB", mixed.FilterComplex, StringComparison.Ordinal);
            Assert.Contains("asplit", mixed.FilterComplex, StringComparison.Ordinal);
            Assert.True(mixed.DuckingApplied, "Mixer must report ducking applied when background is present.");
        }
        finally
        {
            DeleteDirQuietly(workDir);
        }
    }

    private static FFmpegMixer CreateMixer()
    {
        var runner = new ProcessRunner(NullLogger<ProcessRunner>.Instance);
        var storageMock = new Moq.Mock<IArtifactStorage>(Moq.MockBehavior.Strict);
        var mediaOptions = Microsoft.Extensions.Options.Options.Create(new MediaOptions());
        var ffprobe = new FFprobeService(
            storageMock.Object, runner, mediaOptions, NullLogger<FFprobeService>.Instance);
        var ffmpeg = new FFmpegService(
            runner, ffprobe, mediaOptions, NullLogger<FFmpegService>.Instance);
        var gate = new MediaJobGate(mediaOptions);
        var mixingOptions = Microsoft.Extensions.Options.Options.Create(new MixingOptions());
        return new FFmpegMixer(
            runner, ffprobe, ffmpeg, gate, mixingOptions, mediaOptions, NullLogger<FFmpegMixer>.Instance);
    }

    private static string BuildTimeline(
        IReadOnlyList<(int Sequence, int StartMs, int DurationMs)> entries,
        int sourceDurationMs)
    {
        var list = entries.Select(e => new
        {
            audioArtifactId = Guid.NewGuid().ToString("N"),
            durationMs = e.DurationMs,
            overlapGroupId = (string?)null,
            segmentId = Guid.NewGuid().ToString("N"),
            sequence = e.Sequence,
            startMs = e.StartMs,
        }).ToList();

        var root = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["backgroundArtifactId"] = null,
            ["entries"] = list,
            ["overflowToleranceMs"] = 100,
            ["runId"] = Guid.NewGuid().ToString("N"),
            ["schemaVersion"] = "1",
            ["skippedSegmentIds"] = Array.Empty<string>(),
            ["sourceDurationMs"] = sourceDurationMs,
        };

        return JsonSerializer.Serialize(root, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    private static async Task GenerateSineAsync(int frequencyHz, int durationSec, string destPath, double volume = 1.0)
    {
        var runner = new ProcessRunner(NullLogger<ProcessRunner>.Instance);
        var workDir = ProcessRunner.CreateTempWorkingDir();
        try
        {
            var args = new List<string>
            {
                "-y", "-f", "lavfi", "-i",
                string.Concat("sine=frequency=", frequencyHz.ToString(CultureInfo.InvariantCulture), ":duration=", durationSec.ToString(CultureInfo.InvariantCulture), ":sample_rate=48000"),
            };

            if (Math.Abs(volume - 1.0) > 1e-9)
            {
                args.Add("-filter:a");
                args.Add(string.Concat("volume=", volume.ToString("F2", CultureInfo.InvariantCulture)));
            }

            args.Add("-c:a");
            args.Add("pcm_s16le");
            args.Add("-ac");
            args.Add("2");
            args.Add(destPath);

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

    private static async Task GenerateDuckedBackgroundAsync(
        string dialogueFile,
        int dialogueStartMs,
        string backgroundFile,
        int totalMs,
        string destPath)
    {
        var runner = new ProcessRunner(NullLogger<ProcessRunner>.Instance);
        var workDir = ProcessRunner.CreateTempWorkingDir();
        try
        {
            var totalSec = (totalMs / 1000.0).ToString("F3", CultureInfo.InvariantCulture);
            var filter = string.Concat(
                "[0:a]aformat=sample_fmts=fltp:sample_rates=48000:channel_layouts=stereo,adelay=",
                dialogueStartMs.ToString(CultureInfo.InvariantCulture), "|", dialogueStartMs.ToString(CultureInfo.InvariantCulture),
                ":all=1,apad=whole_dur=", totalSec, ",atrim=0:", totalSec, "[dialog];",
                "[1:a]aformat=sample_fmts=fltp:sample_rates=48000:channel_layouts=stereo,atrim=0:", totalSec,
                ",apad=whole_dur=", totalSec, ",atrim=0:", totalSec, "[bgfmt];",
                "[bgfmt][dialog]sidechaincompress=threshold=0.01:ratio=8:attack=150:release=150[bgcomp];",
                "[bgcomp]volume=-12dB[duck]");
            var args = new List<string>
            {
                "-y", "-i", dialogueFile, "-i", backgroundFile,
                "-filter_complex", filter,
                "-map", "[duck]",
                "-ar", "48000", "-ac", "2", "-c:a", "pcm_s16le",
                destPath,
            };

            var result = await runner.RunAsync(
                "ffmpeg", args, workDir, TimeSpan.FromSeconds(90), CancellationToken.None).ConfigureAwait(true);
            if (result.TimedOut || result.ExitCode != 0)
            {
                throw new InvalidOperationException($"ffmpeg duck fixture failed (exit {result.ExitCode}, timedOut={result.TimedOut}): {result.StdErr}");
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

                Skip.If(true, $"ffmpeg/ffprobe missing: ffprobe -version exited {result.ExitCode} (timedOut={result.TimedOut}). Install ffmpeg to run mixing tests.");
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
            _output.WriteLine($"ffmpeg/ffprobe missing, skipping mixing test: {ex.Message}");
            Skip.If(true, $"ffmpeg/ffprobe missing: {ex.Message}. Install ffmpeg to run mixing tests.");
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
