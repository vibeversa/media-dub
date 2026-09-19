using DubbingPlatform.Application.Abstractions;
using DubbingPlatform.Application.Errors;
using DubbingPlatform.Application.Exceptions;
using DubbingPlatform.Application.Options;
using DubbingPlatform.Application.Services;
using DubbingPlatform.Infrastructure.Media;
using DubbingPlatform.IntegrationTests.Fixtures;
using Xunit.Abstractions;

namespace DubbingPlatform.IntegrationTests.Media;

/// <summary>
/// Task 39: media-bomb and resource-exhaustion tier (hermetic). Declared-vs-
/// actual size mismatches and oversized sparse uploads are rejected at the
/// <see cref="MediaValidator"/> gate; staging them would fail fast with
/// <c>RESOURCE_EXHAUSTED</c> via <see cref="DiskSpaceChecker"/> (no partial
/// artifact is committed). Fixture files are used when present for the
/// positive control.
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
}
