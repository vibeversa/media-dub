using DubbingPlatform.Application.Abstractions;
using DubbingPlatform.Application.Errors;
using DubbingPlatform.Application.Exceptions;
using DubbingPlatform.Application.Options;
using DubbingPlatform.Infrastructure.Media;
using DubbingPlatform.Infrastructure.Processes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DubbingPlatform.UnitTests.Media;

/// <summary>
/// Hermetic validation-rule tests (no Docker): ffprobe JSON parsing,
/// container normalization, and <c>MediaValidator</c> allowlists.
/// </summary>
public sealed class MediaValidationTests
{
    private static MediaOptions DefaultMedia()
    {
        return new MediaOptions();
    }

    private static FfprobeResult Probe(string container, long durationMs, params FfprobeStream[] streams)
    {
        return new FfprobeResult(container, durationMs, streams);
    }

    private static FfprobeStream Audio(string codec, int sampleRate = 48000, int channels = 2, string layout = "stereo")
    {
        return new FfprobeStream("audio", codec, null, null, null, sampleRate, channels, layout);
    }

    private static FfprobeStream Video(string codec)
    {
        return new FfprobeStream("video", codec, 320, 240, 10.0, null, null, null);
    }

    [Fact]
    public void Valid_Mp4_H264_Aac_Passes()
    {
        var probe = Probe("mp4", 2000, Video("h264"), Audio("aac"));
        var outcome = DubbingPlatform.Application.Services.MediaValidator.Validate(probe, 1024, DefaultMedia());
        Assert.True(outcome.IsValid);
        Assert.Equal("mp4", outcome.Container);
        Assert.Equal("aac", outcome.AudioCodec);
        Assert.Equal("h264", outcome.VideoCodec);
    }

    [Fact]
    public void Pcm_Audio_Passes()
    {
        var probe = Probe("wav", 2000, Audio("pcm_s16le", 44100, 1, "mono"));
        var outcome = DubbingPlatform.Application.Services.MediaValidator.Validate(probe, 1024, DefaultMedia());
        Assert.True(outcome.IsValid);
    }

    [Fact]
    public void Missing_Audio_Unsupported()
    {
        var probe = Probe("mp4", 2000, Video("h264"));
        var outcome = DubbingPlatform.Application.Services.MediaValidator.Validate(probe, 1024, DefaultMedia());
        Assert.False(outcome.IsValid);
        Assert.Equal(ErrorCodes.MediaUnsupported, outcome.FailureCode);
    }

    [Fact]
    public void Video_Mpeg4_Unsupported()
    {
        var probe = Probe("mp4", 2000, Video("mpeg4"), Audio("aac"));
        var outcome = DubbingPlatform.Application.Services.MediaValidator.Validate(probe, 1024, DefaultMedia());
        Assert.False(outcome.IsValid);
        Assert.Equal(ErrorCodes.MediaUnsupported, outcome.FailureCode);
    }

    [Fact]
    public void Bad_Container_Unsupported()
    {
        var probe = Probe("exe", 2000, Audio("aac"));
        var outcome = DubbingPlatform.Application.Services.MediaValidator.Validate(probe, 1024, DefaultMedia());
        Assert.False(outcome.IsValid);
        Assert.Equal(ErrorCodes.MediaUnsupported, outcome.FailureCode);
    }

    [Fact]
    public void Too_Short_Unsupported()
    {
        var probe = Probe("mp4", 500, Video("h264"), Audio("aac"));
        var outcome = DubbingPlatform.Application.Services.MediaValidator.Validate(probe, 1024, DefaultMedia());
        Assert.False(outcome.IsValid);
        Assert.Equal(ErrorCodes.MediaUnsupported, outcome.FailureCode);
    }

    [Fact]
    public void Oversize_Unsupported()
    {
        var media = DefaultMedia();
        var probe = Probe("mp4", 2000, Video("h264"), Audio("aac"));
        var outcome = DubbingPlatform.Application.Services.MediaValidator.Validate(probe, media.MaxUploadBytes + 1, media);
        Assert.False(outcome.IsValid);
        Assert.Equal(ErrorCodes.MediaUnsupported, outcome.FailureCode);
    }

    [Fact]
    public void Parse_Valid_Ffprobe_Json()
    {
        const string json = """
            {
              "streams": [
                {"codec_type": "video", "codec_name": "h264", "width": 320, "height": 240, "avg_frame_rate": "10/1"},
                {"codec_type": "audio", "codec_name": "aac", "sample_rate": "48000", "channels": 2, "channel_layout": "stereo"}
              ],
              "format": {"format_name": "mov,mp4,m4a,3gp,3g2,mj2", "duration": "2.000000"}
            }
            """;
        var result = FFprobeService.Parse(json);
        Assert.Equal("mov", result.Container);
        Assert.Equal(2000, result.DurationMs);
        Assert.Equal(2, result.Streams.Count);
        Assert.Equal("h264", result.Streams[0].Codec);
        Assert.Equal(10.0, result.Streams[0].Fps);
        Assert.Equal(48000, result.Streams[1].SampleRate);
    }

    [Fact]
    public void Parse_Matroska_Maps_To_Mkv()
    {
        const string json = """
            {"streams": [{"codec_type": "audio", "codec_name": "opus"}],
             "format": {"format_name": "matroska,webm", "duration": "2.0"}}
            """;
        var result = FFprobeService.Parse(json);
        Assert.Equal("mkv", result.Container);
    }

    [Fact]
    public void Parse_Invalid_Json_Corrupt()
    {
        var ex = Assert.Throws<ErrorCodeException>(() => FFprobeService.Parse("not json"));
        Assert.Equal(ErrorCodes.MediaCorrupt, ex.ErrorCode);
    }

    [Fact]
    public void Parse_Missing_Streams_Corrupt()
    {
        var ex = Assert.Throws<ErrorCodeException>(() => FFprobeService.Parse("""{"format": {"format_name": "mp4"}}"""));
        Assert.Equal(ErrorCodes.MediaCorrupt, ex.ErrorCode);
    }

    [Fact]
    public async Task Probe_Real_Files_Via_Ffprobe()
    {
        var runner = new ProcessRunner(NullLogger<ProcessRunner>.Instance);
        string workingDir;
        try
        {
            workingDir = ProcessRunner.CreateTempWorkingDir();
        }
        catch (IOException ex)
        {
            Skip.If(true, $"Temp dir unavailable: {ex.Message}");
            return;
        }

        try
        {
            var version = await runner.RunAsync("ffprobe", ["-version"], workingDir, TimeSpan.FromSeconds(15), CancellationToken.None);
            if (version.TimedOut || version.ExitCode != 0)
            {
                Skip.If(true, "ffmpeg/ffprobe missing: ffprobe -version failed. Install ffmpeg to run media tests.");
                return;
            }

            var mp4Path = Path.Combine(Path.GetTempPath(), string.Concat("dubbing-ut-", Guid.NewGuid().ToString("N"), ".mp4"));
            try
            {
                var gen = await runner.RunAsync(
                    "ffmpeg",
                    ["-y", "-f", "lavfi", "-i", "testsrc=duration=2:size=320x240:rate=10", "-f", "lavfi", "-i", "sine=frequency=440:duration=2", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", mp4Path],
                    workingDir, TimeSpan.FromSeconds(90), CancellationToken.None);
                if (gen.TimedOut || gen.ExitCode != 0)
                {
                    Skip.If(true, $"ffmpeg fixture generation failed (exit {gen.ExitCode}). Install a full ffmpeg build with libx264/aac.");
                    return;
                }

                var storageMock = new Moq.Mock<IArtifactStorage>(Moq.MockBehavior.Strict);
                var service = new FFprobeService(
                    storageMock.Object, runner, global::Microsoft.Extensions.Options.Options.Create(new MediaOptions()), NullLogger<FFprobeService>.Instance);
                var probe = await service.ProbeAsync(mp4Path, CancellationToken.None);
                Assert.Contains(probe.Container, new[] { "mov", "mp4" });
                Assert.True(probe.DurationMs >= 1500 && probe.DurationMs <= 3000, $"Duration {probe.DurationMs}ms out of range.");
                Assert.Contains(probe.Streams, s => s.CodecType == "audio");
                var outcome = DubbingPlatform.Application.Services.MediaValidator.Validate(probe, new FileInfo(mp4Path).Length, new MediaOptions());
                Assert.True(outcome.IsValid);

                var textPath = Path.Combine(Path.GetTempPath(), string.Concat("dubbing-ut-", Guid.NewGuid().ToString("N"), ".mp4"));
                try
                {
                    await File.WriteAllTextAsync(textPath, "this is not a video");
                    var corrupt = await Assert.ThrowsAsync<ErrorCodeException>(() => service.ProbeAsync(textPath, CancellationToken.None));
                    Assert.Equal(ErrorCodes.MediaCorrupt, corrupt.ErrorCode);
                }
                finally
                {
                    try { if (File.Exists(textPath)) { File.Delete(textPath); } } catch (Exception) { }
                }
            }
            finally
            {
                try { if (File.Exists(mp4Path)) { File.Delete(mp4Path); } } catch (Exception) { }
            }
        }
        finally
        {
            try { Directory.Delete(workingDir, recursive: true); } catch (Exception) { }
        }
    }
}
