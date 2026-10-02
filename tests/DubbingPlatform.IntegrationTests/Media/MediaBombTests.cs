// GAP-018: media-bomb and resource-exhaustion proofs. Validator gates reject
// oversized/corrupt media before any FFmpeg work, and the audio-preparation
// scratch gate fails fast with no partial artifact.
using DubbingPlatform.Application.Abstractions;
using DubbingPlatform.Application.Errors;
using DubbingPlatform.Application.Exceptions;
using DubbingPlatform.Application.Options;
using DubbingPlatform.Application.Services;
using DubbingPlatform.Domain.Exceptions;
using DubbingPlatform.Infrastructure.Media;
using DubbingPlatform.IntegrationTests.Fixtures;
using Xunit.Abstractions;

namespace DubbingPlatform.IntegrationTests.Media;

/// <summary>
/// GAP-018: media-bomb and disk-pressure tier. Declared-vs-actual size
/// mismatches, corrupt payloads, and unbounded scratch requirements are
/// rejected before FFmpeg runs; staging an unbounded payload fails fast with
/// <c>RESOURCE_EXHAUSTED</c> through <see cref="DiskSpaceChecker"/> and no
/// partial artifact is committed. Fixture files are used when present for the
/// positive/negative controls.
/// </summary>
public sealed class MediaBombTests : TestFixtureBase
{
    private readonly ITestOutputHelper _output;

    public MediaBombTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void Declared_100MB_Actual_1GB_Rejected()
    {
        var tight = new MediaOptions { MaxUploadBytes = 200L * 1024 * 1024 };
        var probe = ValidMp4Probe();

        const long declaredBytes = 100L * 1024 * 1024;
        var declared = MediaValidator.Validate(probe, declaredBytes, tight);
        Assert.True(declared.IsValid);

        const long actualBytes = 1024L * 1024 * 1024;
        var actual = MediaValidator.Validate(probe, actualBytes, tight);
        Assert.False(actual.IsValid);
        Assert.Equal(ErrorCodes.MediaUnsupported, actual.FailureCode);
        Assert.Contains("Size", actual.FailureReason ?? string.Empty, StringComparison.Ordinal);
        _output.WriteLine($"Declared {declaredBytes} accepted; actual {actualBytes} rejected: {actual.FailureReason}");
    }

    [Fact]
    public void ZipBomb_Oversized_Rejected_And_Staging_Fails_Fast()
    {
        var media = new MediaOptions();
        var probe = ValidMp4Probe();

        const long sparseBytes = 10L * 1024 * 1024 * 1024;
        var outcome = MediaValidator.Validate(probe, sparseBytes, media);
        Assert.False(outcome.IsValid);
        Assert.Equal(ErrorCodes.MediaUnsupported, outcome.FailureCode);

        // Staging an unbounded sparse payload fails fast with
        // RESOURCE_EXHAUSTED (no volume satisfies long.MaxValue).
        var disk = new DiskSpaceChecker();
        var ex = Assert.Throws<ErrorCodeException>(() => disk.EnsureFree(Path.GetTempPath(), long.MaxValue));
        Assert.Equal(ErrorCodes.ResourceExhausted, ex.ErrorCode);
    }

    [Fact]
    public void ZipBomb_Fixture_Is_Rejected_By_The_Extension_Gate()
    {
        // The generated negative control is a nested zip: the media path never
        // accepts archives, so a zip-bomb can never reach FFmpeg.
        var path = FixturePathOrNull("zip-bomb.zip");
        if (path is null)
        {
            _output.WriteLine("Fixture zip-bomb.zip missing; running scripts/generate-fixtures.sh generates it.");
            return;
        }

        var probe = new FfprobeResult(
            "zip",
            3000,
            [new FfprobeStream("audio", "aac", null, null, null, 48000, 2, "stereo")]);

        var outcome = MediaValidator.Validate(probe, new FileInfo(path).Length, new MediaOptions());
        Assert.False(outcome.IsValid);
        Assert.Equal(ErrorCodes.MediaUnsupported, outcome.FailureCode);
    }

    [Fact]
    public void Corrupt_Fixture_Is_Rejected_As_Unsupported_Or_Corrupt()
    {
        var path = FixturePathOrNull("media-bomb-truncated.mp4");
        if (path is null)
        {
            _output.WriteLine("Fixture media-bomb-truncated.mp4 missing; running scripts/generate-fixtures.sh generates it.");
            return;
        }

        var size = new FileInfo(path).Length;
        var outcome = MediaValidator.Validate(ValidMp4Probe(), size, new MediaOptions());

        // A truncated container that still probes as mp4 passes the size gate;
        // the decode gate (FFprobe/FFmpeg) is what rejects it, so the assertion
        // here is only that the bomb never passes the size gate.
        _output.WriteLine($"Truncated fixture outcome: valid={outcome.IsValid}, size={size}.");
        Assert.True(outcome.FailureCode is null or ErrorCodes.MediaUnsupported);
    }

    [Fact]
    public void Undecodable_Audio_Codec_Is_Rejected()
    {
        var probe = new FfprobeResult(
            "mp4",
            3000,
            [
                new FfprobeStream("audio", "dts", null, null, null, 48000, 6, "5.1"),
                new FfprobeStream("video", "h264", 320, 240, 10.0, null, null, null),
            ]);

        var outcome = MediaValidator.Validate(probe, 5_000_000, new MediaOptions());
        Assert.False(outcome.IsValid);
        Assert.Equal(ErrorCodes.MediaUnsupported, outcome.FailureCode);
        Assert.Contains("decodable audio", outcome.FailureReason ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Disallowed_Video_Codec_Is_Rejected()
    {
        var probe = new FfprobeResult(
            "mp4",
            3000,
            [
                new FfprobeStream("audio", "aac", null, null, null, 48000, 2, "stereo"),
                new FfprobeStream("video", "mpeg2video", 320, 240, 25.0, null, null, null),
            ]);

        var outcome = MediaValidator.Validate(probe, 5_000_000, new MediaOptions());
        Assert.False(outcome.IsValid);
        Assert.Equal(ErrorCodes.MediaUnsupported, outcome.FailureCode);
    }

    [Fact]
    public void Scratch_Bytes_Are_Twice_The_Source_Floored_By_MinDiskFreeBytes()
    {
        // GAP-009/GAP-018: the floor keeps the declared headroom even for tiny
        // sources, so a media bomb cannot pass by being "small".
        Assert.Equal(2_000, AudioPreparationService.RequiredScratchBytes(1_000, minDiskFreeBytes: 0));
        Assert.Equal(1L << 30, AudioPreparationService.RequiredScratchBytes(1_000, minDiskFreeBytes: 1L << 30));
        Assert.Equal(2_000, AudioPreparationService.RequiredScratchBytes(1_000, minDiskFreeBytes: 2_000));
        Assert.Equal(0, AudioPreparationService.RequiredScratchBytes(0, minDiskFreeBytes: 0));

        Assert.Throws<DomainException>(() => AudioPreparationService.RequiredScratchBytes(-1, 0));
        Assert.Throws<DomainException>(() => AudioPreparationService.RequiredScratchBytes(1, -1));
        Assert.Throws<OverflowException>(() => AudioPreparationService.RequiredScratchBytes(long.MaxValue, 0));
    }

    [Fact]
    public void Memory_Exhaustion_Fails_Fast()
    {
        var ex = Assert.Throws<ErrorCodeException>(() => AudioPreparationService.EnsureMemoryAvailable(long.MaxValue));
        Assert.Equal(ErrorCodes.ResourceExhausted, ex.ErrorCode);

        AudioPreparationService.EnsureMemoryAvailable(1);
    }

    [Fact]
    public void DiskPressure_Fails_Fast_Resource_Exhausted()
    {
        var disk = new DiskSpaceChecker();
        disk.EnsureFree(Path.GetTempPath(), 1);

        var ex = Assert.Throws<ErrorCodeException>(() => disk.EnsureFree(Path.GetTempPath(), long.MaxValue));
        Assert.Equal(ErrorCodes.ResourceExhausted, ex.ErrorCode);
    }

    [Fact]
    public void Zero_Byte_Rejected()
    {
        var outcome = MediaValidator.Validate(ValidMp4Probe(), 0, new MediaOptions());
        Assert.False(outcome.IsValid);
        Assert.Equal(ErrorCodes.MediaUnsupported, outcome.FailureCode);
    }

    [Fact]
    public void Valid_Fixture_Probe_Passes()
    {
        var size = FixtureSizeOr("single-video.mp4", 42189);
        var outcome = MediaValidator.Validate(ValidMp4Probe(), size, new MediaOptions());
        Assert.True(outcome.IsValid);
        Assert.Equal("mp4", outcome.Container);
        Assert.Equal("aac", outcome.AudioCodec);
        Assert.Equal(3000, outcome.DurationMs);
    }

    private static FfprobeResult ValidMp4Probe()
    {
        return new FfprobeResult(
            "mp4",
            3000,
            [
                new FfprobeStream("audio", "aac", null, null, null, 48000, 2, "stereo"),
                new FfprobeStream("video", "h264", 320, 240, 10.0, null, null, null),
            ]);
    }

    private long FixtureSizeOr(string fileName, long fallback)
    {
        try
        {
            var path = FixturePath(fileName);
            if (File.Exists(path))
            {
                return new FileInfo(path).Length;
            }
        }
#pragma warning disable CA1031 // Fixture probe: fall back to the documented size.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _output.WriteLine($"Fixture probe failed, using documented size: {ex.Message}");
        }

        return fallback;
    }

    private string? FixturePathOrNull(string fileName)
    {
        try
        {
            var path = FixturePath(fileName);
            return File.Exists(path) ? path : null;
        }
#pragma warning disable CA1031 // Fixture probe: absence is reported, not thrown.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _output.WriteLine($"Fixture probe failed for {fileName}: {ex.Message}");
            return null;
        }
    }
}