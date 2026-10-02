// GAP-014: artifact publish is upload-first with a single commit transaction
// (no Pending rows), and lineage is one directed edge table queried in both
// directions. See docs/adr/ADR-014-artifact-publish-as-built.md.
using System.Reflection;
using System.Text.RegularExpressions;
using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Domain.Enums;
using DubbingPlatform.Infrastructure.Persistence;
using DubbingPlatform.Infrastructure.Persistence.Configurations;
using EFCore.NamingConventions;
using Microsoft.EntityFrameworkCore;

namespace DubbingPlatform.UnitTests.Persistence;

public sealed class ArtifactPublishAsBuiltTests : IDisposable
{
    private static readonly Regex SourceText = new(
        @"UPDATE stage_executions SET status",
        RegexOptions.Compiled);

    private readonly AppDbContext _context;

    public ArtifactPublishAsBuiltTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=dummy;Username=dummy;Password=dummy")
            .UseSnakeCaseNamingConvention()
            .Options;
        _context = new AppDbContext(options);
    }

    public void Dispose()
    {
        _context.Dispose();
    }

    [Fact]
    public void Publish_Path_Never_Inserts_Pending_Rows()
    {
        // ADR-014: upload-first + single-txn commit. A Pending reserve would
        // need a stale-Pending sweeper and would expose uncommitted bytes.
        var publishSource = ReadArtifactServiceSource();
        Assert.Contains("CommitNewAsync", publishSource, StringComparison.Ordinal);
        Assert.Contains("ContentObjectStatus.Committed", publishSource, StringComparison.Ordinal);
        Assert.Contains("ArtifactStatus.Committed", publishSource, StringComparison.Ordinal);
        Assert.DoesNotContain("ContentObjectStatus.Pending", publishSource, StringComparison.Ordinal);
        Assert.DoesNotContain("ArtifactStatus.Pending", publishSource, StringComparison.Ordinal);
    }

    [Fact]
    public void Artifact_Commit_Is_One_Transaction_Covering_Rows_And_Lineage()
    {
        var source = ReadArtifactServiceSource();
        Assert.Contains("BeginTransaction", source, StringComparison.Ordinal);
        Assert.Contains("ValidateParentsAsync", source, StringComparison.Ordinal);
        Assert.Contains("ArtifactParent", source, StringComparison.Ordinal);
        Assert.Contains("StageOutputArtifact", source, StringComparison.Ordinal);

        // The stage completion is deliberately NOT in the artifact transaction:
        // it is a lease-fenced conditional UPDATE on stage_executions.
        Assert.DoesNotContain("UPDATE stage_executions SET status", source, StringComparison.Ordinal);
        Assert.DoesNotMatch(SourceText, source);
    }

    [Fact]
    public void Lineage_Is_One_Directed_Edge_Table_With_A_Child_To_Parent_Unique_Index()
    {
        var entity = _context.Model.FindEntityType(typeof(ArtifactParent));
        Assert.NotNull(entity);
        Assert.Equal("artifact_parents", entity!.GetTableName());

        var unique = entity.GetIndexes()
            .FirstOrDefault(i => i.IsUnique
                && i.Properties.Select(p => p.Name).SequenceEqual(["ChildArtifactId", "ParentArtifactId"]));
        Assert.NotNull(unique);

        // Both directions stay relational (no JSON-only lineage): the reverse
        // direction is served by this index, not by a second table.
        Assert.Contains(entity.GetIndexes(), i =>
            i.Properties.Select(p => p.Name).SequenceEqual(["ParentArtifactId"]));
    }

    [Fact]
    public void No_Separate_Artifact_Child_Table_Exists()
    {
        var tables = _context.Model.GetEntityTypes()
            .Select(e => e.GetTableName())
            .Where(t => t is not null)
            .ToHashSet();

        Assert.DoesNotContain("artifact_children", tables);
        Assert.Contains("artifact_parents", tables);

        // Stage lineage is relational too.
        Assert.Contains("stage_input_artifacts", tables);
        Assert.Contains("stage_output_artifacts", tables);
    }

    [Fact]
    public void Artifact_Configuration_Still_Allows_Pending_For_Legacy_Rows()
    {
        // Pending stays a valid enum value (state machines + migrations) even
        // though the publish path no longer writes it; expand-only compatibility.
        Assert.Equal("Pending", ArtifactStatus.Pending.ToString());
        Assert.Equal("Pending", ContentObjectStatus.Pending.ToString());

        var configuration = new ArtifactConfiguration();
        Assert.NotNull(configuration);
        Assert.True(configuration is IEntityTypeConfiguration<Artifact>);
        var artifacts = _context.Model.GetEntityTypes()
            .FirstOrDefault(e => e.GetTableName() == "artifacts");
        Assert.NotNull(artifacts);

        var properties = artifacts!.GetProperties().Select(p => p.Name).ToList();
        Assert.Contains("Status", properties);
        Assert.Contains("Type", properties);

        // Enum columns are stored as text, so "Pending" remains a legal stored
        // value for rows written before the publish path changed.
        var configurationSource = ReadConfigurationSource();
        Assert.Contains("builder.Property(e => e.Status).HasConversion<string>()", configurationSource, StringComparison.Ordinal);
        Assert.Contains("builder.Property(e => e.Type).HasConversion<string>()", configurationSource, StringComparison.Ordinal);
    }

    private static string ReadConfigurationSource()
    {
        return File.ReadAllText(Path.Combine(
            FindRepoRoot(), "src", "DubbingPlatform.Infrastructure", "Persistence", "Configurations", "ArtifactConfiguration.cs"));
    }

    private static string ReadArtifactServiceSource()
    {
        var path = Path.Combine(
            FindRepoRoot(), "src", "DubbingPlatform.Application", "Services", "ArtifactService.cs");
        Assert.True(File.Exists(path), $"ArtifactService source not found at {path}.");
        return File.ReadAllText(path);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DubbingPlatform.sln")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return dir!.FullName;
    }
}