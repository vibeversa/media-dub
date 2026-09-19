using DubbingPlatform.Application.MultiTenancy;
using DubbingPlatform.Infrastructure.Persistence;
using DubbingPlatform.IntegrationTests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Xunit.Abstractions;

namespace DubbingPlatform.IntegrationTests.Persistence;

/// <summary>
/// Task 39: migration-compat tier. Old code runs against new schemas only when
/// evolution is additive, so this tier pins the core tables/columns every
/// reader depends on and proves the migration chain applies cleanly (live PG
/// skips without Docker; the migration-files gate is hermetic).
/// </summary>
public sealed class MigrationCompatTests : TestFixtureBase
{
    private static readonly string[] CoreTables =
    [
        "tenants", "dubbing_projects", "processing_runs",
        "stage_executions", "artifacts", "content_objects",
    ];

    private static readonly string[] StageExecutionColumns =
    [
        "id", "tenant_id", "project_id", "processing_run_id", "stage_type",
        "scope_type", "scope_id", "attempt", "status", "lease_owner",
        "lease_token", "lease_expires_at", "configuration_hash",
        "execution_snapshot_hash", "created_at", "updated_at",
    ];

    private readonly ITestOutputHelper _output;

    public MigrationCompatTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void Migration_Files_Present()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        string? migrations = null;
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "DubbingPlatform.Infrastructure", "Persistence", "Migrations");
            if (Directory.Exists(candidate))
            {
                migrations = candidate;
                break;
            }

            dir = dir.Parent;
        }

        Assert.NotNull(migrations);
        var files = Directory.GetFiles(migrations, "*.cs");
        Assert.Contains(files, f => f.EndsWith("AppDbContextModelSnapshot.cs", StringComparison.Ordinal));
        Assert.True(files.Length >= 2, "At least one migration plus the model snapshot must exist.");
        _output.WriteLine($"Found {files.Length} migration files in {migrations}.");
    }

    [SkippableFact]
    public async Task Additive_Schema_Compat()
    {
        var container = await StartPostgresAsync(_output).ConfigureAwait(true);
        await using (container.ConfigureAwait(true))
        {
            var connectionString = container.GetConnectionString();
            await MigrateAsync(connectionString).ConfigureAwait(true);

            using (TenantContext.BeginMaintenanceScope())
            {
                using var db = new AppDbContext(CreatePgOptions(connectionString));

                var applied = await db.Database.GetAppliedMigrationsAsync().ConfigureAwait(true);
                Assert.NotEmpty(applied);

                // Core tables every old reader depends on must survive evolution.
                foreach (var table in CoreTables)
                {
                    var exists = await db.Database.SqlQueryRaw<int>(
                        "SELECT 1 AS \"Value\" FROM information_schema.tables WHERE table_schema = 'public' AND table_name = {0}",
                        table).AnyAsync().ConfigureAwait(true);
                    Assert.True(exists, $"Core table '{table}' must exist for old-code/new-schema compat.");
                }

                // Pinned stage_executions columns must keep their names so old
                // lease/state-machine SQL keeps working after additive changes.
                var columns = await db.Database.SqlQueryRaw<string>(
                    "SELECT column_name AS \"Value\" FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'stage_executions'").ToListAsync().ConfigureAwait(true);
                foreach (var column in StageExecutionColumns)
                {
                    Assert.Contains(column, columns, StringComparer.Ordinal);
                }

                _output.WriteLine($"Compat ok: {applied.Count()} migrations applied, {columns.Count} stage_executions columns.");
            }
        }
    }
}
