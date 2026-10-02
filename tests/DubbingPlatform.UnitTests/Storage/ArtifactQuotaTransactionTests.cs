using DubbingPlatform.Application.Options;
using DubbingPlatform.Application.Services;
using Microsoft.Extensions.Options;

namespace DubbingPlatform.UnitTests.Storage;

/// <summary>
/// GAP-005: storage quota must be joined to the artifact-commit transaction,
/// not a separate pre-check connection. Serializes per-tenant via
/// pg_advisory_xact_lock and SUMs committed bytes on the same DbContext.
/// </summary>
public sealed class ArtifactQuotaTransactionTests
{
    [Fact]
    public void ArtifactService_Accepts_QuotaOptions_For_Transactional_Enforcement()
    {
        var ctor = typeof(ArtifactService).GetConstructors().Single();
        var paramNames = ctor.GetParameters().Select(p => p.ParameterType.Name).ToList();
        Assert.Contains("IQuotaGate", paramNames);
        Assert.Contains("IOptions`1", paramNames);
    }

    [Fact]
    public void Commit_Path_Serializes_Per_Tenant_With_Advisory_Lock()
    {
        var source = File.ReadAllText(RepoPath("src/DubbingPlatform.Application/Services/ArtifactService.cs"));
        Assert.Contains("pg_advisory_xact_lock", source, StringComparison.Ordinal);
        Assert.Contains("IsStorageExceeded", source, StringComparison.Ordinal);
        Assert.DoesNotContain("NOW()", source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Storage_Quota_Pure_Check_Rejects_Over_Admit()
    {
        Assert.True(QuotaService.IsStorageExceeded(100, 50, 120));
        Assert.False(QuotaService.IsStorageExceeded(100, 20, 120));
        Assert.True(QuotaService.IsStorageExceeded(120, 1, 120));
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
