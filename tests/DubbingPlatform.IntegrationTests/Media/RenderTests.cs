using System.Globalization;
using DubbingPlatform.Application.Abstractions;
using DubbingPlatform.Application.Options;
using DubbingPlatform.Application.Storage;
using DubbingPlatform.Infrastructure.Media;
using DubbingPlatform.Infrastructure.Processes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;

namespace DubbingPlatform.IntegrationTests.Media;

/// <summary>
/// Task 033: final rendering over sine/video fixtures (requires
/// ffmpeg/ffprobe). Skips with an explicit message when ffmpeg is unavailable
/// (CI runs live). No database is needed; the download test exercises the
/// 15-minute presigned-URL policy against fake blob storage.
/// </summary>
public sealed class RenderTests
{
    private readonly ITestOutputHelper _output;

    public RenderTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [SkippableFact]
    public async Task Produces_Mp4_Within_Tolerance()
    {
        await EnsureFfmpegAsync().ConfigureAwait(true);
        var renderer = CreateRenderer();
        var workDir = ProcessRunner.CreateTempWorkingDir();
        try
        {
            var video = Path.Combine(workDir, "source.mp4");
            var mixed = Path.Combine(workDir, "mixed.wav");
            var output = Path.Combine(workDir, "rendered.mp4");
            await GenerateVideoAsync(2, "libx264", video).ConfigureAwait(true);
            await GenerateSineAsync(440, 2, mixed).ConfigureAwait(true);

            var result = await renderer.RenderAsync(
                video, mixed, output, "mp4", 25.0, CancellationToken.None).ConfigureAwait(true);

            Assert.True(File.Exists(output), "Render output must exist.");
            Assert.Equal("mp4", result.OutputFormat);
            Assert.True(result.VideoIncluded);
            var tolerance = RenderService.ToleranceMsFor(true, 25.0);
            Assert.True(
                Math.Abs((long)result.DurationMs - result.SourceDurationMs) <= tolerance,
                $"Rendered {result.DurationMs}ms must be within {tolerance:F1}ms of source {result.SourceDurationMs}ms.");
            _output.WriteLine($"MP4: {result.DurationMs}ms from {result.SourceDurationMs}ms (tol {tolerance:F1}ms, copy={result.CopyVideo}).");

            var probe = await ProbeAsync(output).ConfigureAwait(true);
            Assert.Contains(probe.Streams, s => string.Equals(s.CodecType, "video", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(probe.Streams, s => string.Equals(s.CodecType, "audio", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            DeleteDirQuietly(workDir);
        }
    }

    [SkippableFact]
    public async Task Copy_When_Safe()
    {
        await EnsureFfmpegAsync().ConfigureAwait(true);
        var renderer = CreateRenderer();
        var workDir = ProcessRunner.CreateTempWorkingDir();
        try
        {
            var video = Path.Combine(workDir, "source.mp4");
            var mixed = Path.Combine(workDir, "mixed.wav");
            var output = Path.Combine(workDir, "rendered.mp4");
            await GenerateVideoAsync(2, "libx264", video).ConfigureAwait(true);
            await GenerateSineAsync(880, 2, mixed).ConfigureAwait(true);

            var result = await renderer.RenderAsync(
                video, mixed, output, "mp4", 25.0, CancellationToken.None).ConfigureAwait(true);

            Assert.True(result.CopyVideo, "H.264 source must stream-copy video.");
            Assert.Null(result.ReencodeReason);
            Assert.Contains("copy", result.FfmpegArgs, StringComparer.Ordinal);
            Assert.DoesNotContain("libx264", result.FfmpegArgs, StringComparer.Ordinal);
        }
        finally
        {
            DeleteDirQuietly(workDir);
        }
    }

    [SkippableFact]
    public async Task Reencode_Recorded_When_Unsafe()
    {
        await EnsureFfmpegAsync().ConfigureAwait(true);
        var renderer = CreateRenderer();
        var workDir = ProcessRunner.CreateTempWorkingDir();
        try
        {
            var video = Path.Combine(workDir, "source.mp4");
            var mixed = Path.Combine(workDir, "mixed.wav");
            var output = Path.Combine(workDir, "rendered.mp4");
            await GenerateVideoAsync(2, "mpeg4", video).ConfigureAwait(true);
            await GenerateSineAsync(660, 2, mixed).ConfigureAwait(true);

            var result = await renderer.RenderAsync(
                video, mixed, output, "mp4", 25.0, CancellationToken.None).ConfigureAwait(true);

            Assert.False(result.CopyVideo, "Non-H.264 source must re-encode video.");
            Assert.Equal(RenderService.ReencodeVideoCodec, result.ReencodeReason);
            Assert.Contains("libx264", result.FfmpegArgs, StringComparer.Ordinal);
            Assert.Contains("slow", result.FfmpegArgs, StringComparer.Ordinal);
            Assert.Contains("18", result.FfmpegArgs, StringComparer.Ordinal);

            var probe = await ProbeAsync(output).ConfigureAwait(true);
            var outVideo = probe.Streams.First(s => string.Equals(s.CodecType, "video", StringComparison.OrdinalIgnoreCase));
            Assert.Equal("h264", outVideo.Codec, StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteDirQuietly(workDir);
        }
    }

    [SkippableFact]
    public async Task Audio_Only_Within_100ms()
    {
        await EnsureFfmpegAsync().ConfigureAwait(true);
        var renderer = CreateRenderer();
        var workDir = ProcessRunner.CreateTempWorkingDir();
        try
        {
            var mixed = Path.Combine(workDir, "mixed.wav");
            await GenerateSineAsync(440, 2, mixed).ConfigureAwait(true);

            foreach (var format in new[] { "wav", "mp3", "aac" })
            {
                var output = Path.Combine(workDir, string.Concat("rendered", RenderService.ExtensionFor(format)));
                var result = await renderer.RenderAsync(
                    null, mixed, output, format, null, CancellationToken.None).ConfigureAwait(true);

                Assert.True(File.Exists(output), $"{format} output must exist.");
                Assert.Equal(format, result.OutputFormat);
                Assert.False(result.VideoIncluded);
                Assert.True(
                    Math.Abs((long)result.DurationMs - result.SourceDurationMs) <= RenderService.AudioOnlyToleranceMs,
                    $"{format}: rendered {result.DurationMs}ms must be within 100ms of source {result.SourceDurationMs}ms.");
                _output.WriteLine($"{format}: {result.DurationMs}ms from {result.SourceDurationMs}ms.");

                var probe = await ProbeAsync(output).ConfigureAwait(true);
                Assert.Contains(probe.Streams, s => string.Equals(s.CodecType, "audio", StringComparison.OrdinalIgnoreCase));
                Assert.DoesNotContain(probe.Streams, s => string.Equals(s.CodecType, "video", StringComparison.OrdinalIgnoreCase));
            }
        }
        finally
        {
            DeleteDirQuietly(workDir);
        }
    }

    [SkippableFact]
    public async Task Download_Url_Works()
    {
        await EnsureFfmpegAsync().ConfigureAwait(true);
        var renderer = CreateRenderer();
        var workDir = ProcessRunner.CreateTempWorkingDir();
        try
        {
            var mixed = Path.Combine(workDir, "mixed.wav");
            var output = Path.Combine(workDir, "rendered.wav");
            await GenerateSineAsync(440, 2, mixed).ConfigureAwait(true);
            await renderer.RenderAsync(null, mixed, output, "wav", null, CancellationToken.None).ConfigureAwait(true);

            Assert.Equal(TimeSpan.FromMinutes(15), SignedUrlPolicy.DefaultExpiry);

            var storage = new FakeStorage();
            var bytes = await File.ReadAllBytesAsync(output).ConfigureAwait(true);
            const string key = "tenant/output/rendered.wav";
            using (var stream = new MemoryStream(bytes, writable: false))
            {
                await storage.UploadAsync(stream, key, "audio/wav", CancellationToken.None).ConfigureAwait(true);
            }

            var url = await storage.GetPresignedDownloadUrlAsync(
                key, SignedUrlPolicy.DefaultExpiry, CancellationToken.None).ConfigureAwait(true);

            Assert.False(string.IsNullOrWhiteSpace(url), "Presigned download URL must be issued.");
            Assert.Contains(key, url, StringComparison.Ordinal);
            Assert.True(await storage.ExistsAsync(key, CancellationToken.None).ConfigureAwait(true));
            _output.WriteLine($"Download URL issued for '{key}' with 15min expiry.");
        }
        finally
        {
            DeleteDirQuietly(workDir);
        }
    }

    private static RenderService CreateRenderer()
    {
        var runner = new ProcessRunner(NullLogger<ProcessRunner>.Instance);
        var storageMock = new Moq.Mock<IArtifactStorage>(Moq.MockBehavior.Strict);
        var mediaOptions = Microsoft.Extensions.Options.Options.Create(new MediaOptions());
        var ffprobe = new FFprobeService(
            storageMock.Object, runner, mediaOptions, NullLogger<FFprobeService>.Instance);
        var ffmpeg = new FFmpegService(
            runner, ffprobe, mediaOptions, NullLogger<FFmpegService>.Instance);
        return new RenderService(ffmpeg, ffprobe, NullLogger<RenderService>.Instance);
    }

    private static async Task<FfprobeResult> ProbeAsync(string file)
    {
        var runner = new ProcessRunner(NullLogger<ProcessRunner>.Instance);
        var storageMock = new Moq.Mock<IArtifactStorage>(Moq.MockBehavior.Strict);
        var mediaOptions = Microsoft.Extensions.Options.Options.Create(new MediaOptions());
        var ffprobe = new FFprobeService(
            storageMock.Object, runner, mediaOptions, NullLogger<FFprobeService>.Instance);
        return await ffprobe.ProbeAsync(file, CancellationToken.None).ConfigureAwait(true);
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

    private static async Task GenerateVideoAsync(int durationSec, string videoCodec, string destPath)
    {
        var runner = new ProcessRunner(NullLogger<ProcessRunner>.Instance);
        var workDir = ProcessRunner.CreateTempWorkingDir();
        try
        {
            var duration = durationSec.ToString(CultureInfo.InvariantCulture);
            var args = new List<string>
            {
                "-y",
                "-f", "lavfi", "-i", string.Concat("testsrc=duration=", duration, ":size=320x240:rate=25"),
                "-f", "lavfi", "-i", string.Concat("sine=frequency=440:duration=", duration, ":sample_rate=48000"),
                "-c:v", videoCodec, "-pix_fmt", "yuv420p",
                "-c:a", "aac", "-b:a", "128k",
                "-shortest", destPath,
            };

            var result = await runner.RunAsync(
                "ffmpeg", args, workDir, TimeSpan.FromSeconds(120), CancellationToken.None).ConfigureAwait(true);
            if (result.TimedOut || result.ExitCode != 0)
            {
                throw new InvalidOperationException($"ffmpeg video fixture failed (exit {result.ExitCode}, timedOut={result.TimedOut}): {result.StdErr}");
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

                Skip.If(true, $"ffmpeg/ffprobe missing: ffprobe -version exited {result.ExitCode} (timedOut={result.TimedOut}). Install ffmpeg to run render tests.");
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
            _output.WriteLine($"ffmpeg/ffprobe missing, skipping render test: {ex.Message}");
            Skip.If(true, $"ffmpeg/ffprobe missing: {ex.Message}. Install ffmpeg to run render tests.");
        }

        throw new InvalidOperationException("Unreachable: Skip.If always throws.");
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
            return Task.FromResult(string.Concat("https://fake/", storageKey, "?expiry=", ((int)expiry.TotalSeconds).ToString(CultureInfo.InvariantCulture)));
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
