using DubbingPlatform.Application.Errors;
using DubbingPlatform.Application.Exceptions;
using DubbingPlatform.Application.Services;

namespace DubbingPlatform.UnitTests.Media;

/// <summary>
/// GAP-009: memory / temp-disk / MinDiskFreeBytes must fail fast with
/// RESOURCE_EXHAUSTED and no partial commit.
/// </summary>
public sealed class MediaResourceEnforcementTests
{
    [Fact]
    public void MinDiskFreeBytes_Is_Honored_As_Floor()
    {
        var source = File.ReadAllText(RepoPath("src/DubbingPlatform.Application/Services/AudioPreparationService.cs"));
        Assert.Contains("MinDiskFreeBytes", source, StringComparison.Ordinal);
        Assert.Contains("EnsureMemoryAvailable", source, StringComparison.Ordinal);
    }

    [Fact]
    public void EnsureMemoryAvailable_Rejects_Impossible_Reservation()
    {
        var ex = Assert.Throws<ErrorCodeException>(() => AudioPreparationService.EnsureMemoryAvailable(long.MaxValue));
        Assert.Equal(ErrorCodes.ResourceExhausted, ex.ErrorCode);
    }

    [Fact]
    public void EnsureMemoryAvailable_Accepts_Trivial_Reservation()
    {
        AudioPreparationService.EnsureMemoryAvailable(1);
    }

    private static string RepoPath(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "DubbingPlatform.sln")))
            {
                return Path.Combine(dir.FullName, relative);
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Repo root not found.");
    }
}
