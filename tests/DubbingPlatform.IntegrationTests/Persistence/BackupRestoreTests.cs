using System.Text.Json;
using DubbingPlatform.Application.MultiTenancy;
using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Domain.Enums;
using DubbingPlatform.Infrastructure.Persistence;
using DubbingPlatform.IntegrationTests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Xunit.Abstractions;

namespace DubbingPlatform.IntegrationTests.Persistence;

/// <summary>
/// Task 39: backup/restore tier. The manifest round-trip is hermetic; the live
/// drill takes a <c>pg_dump --data-only --inserts</c> backup inside the
/// PostgreSQL container, wipes the seeded rows, restores, and verifies row
/// counts (skips without Docker, live in CI).
/// </summary>
public sealed class BackupRestoreTests : TestFixtureBase
{
    private readonly ITestOutputHelper _output;

    public BackupRestoreTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void Backup_Manifest_RoundTrip()
    {
        var manifest = new BackupManifest(
            new DateTimeOffset(2026, 9, 17, 0, 0, 0, TimeSpan.Zero),
            new Dictionary<string, long>(StringComparer.Ordinal)
            {
                ["tenants"] = 1,
                ["dubbing_projects"] = 1,
                ["processing_runs"] = 1,
                ["content_objects"] = 1,
                ["media_assets"] = 1,
            },
            new string('a', 64));

        var json = manifest.ToJson();
        var restored = BackupManifest.FromJson(json);

        Assert.Equal(manifest.TakenAt, restored.TakenAt);
        Assert.Equal(manifest.Tables, restored.Tables);
        Assert.Equal(manifest.Hash, restored.Hash);
    }

    [SkippableFact]
    public async Task PgDump_Restore_Verifies_Counts()
    {
        var container = await StartPostgresAsync(_output).ConfigureAwait(true);
        await using (container.ConfigureAwait(true))
        {
            var connectionString = container.GetConnectionString();
            await MigrateAsync(connectionString).ConfigureAwait(true);
            var tenantId = Guid.NewGuid();
            var projectId = Guid.NewGuid();
            var runId = Guid.NewGuid();
            await SeedAsync(CreatePgOptions(connectionString), tenantId, projectId, runId).ConfigureAwait(true);

            var before = await CountAsync(connectionString, tenantId).ConfigureAwait(true);
            Assert.Equal(1, before.Tenants);
            Assert.Equal(1, before.Projects);
            Assert.Equal(1, before.Runs);

            // Backup: data-only dump with plain INSERTs (COPY FROM stdin is not
            // replayable over ExecScriptAsync, INSERTs are).
            var backup = await container.ExecAsync(
                ["sh", "-c", "PGPASSWORD=$POSTGRES_PASSWORD pg_dump -a --inserts -h localhost -U $POSTGRES_USER -d $POSTGRES_DB"]).ConfigureAwait(true);
            Assert.True(backup.ExitCode == 0, $"pg_dump failed (exit {backup.ExitCode}): {backup.Stderr}");
            Assert.Contains("INSERT INTO", backup.Stdout, StringComparison.Ordinal);
            _output.WriteLine($"Backup captured ({backup.Stdout.Length} chars).");

            // Wipe the seeded rows.
            using (TenantContext.BeginMaintenanceScope())
            {
                using var db = new AppDbContext(CreatePgOptions(connectionString));
                await db.Database.ExecuteSqlRawAsync(
                    "TRUNCATE tenants, content_objects, media_assets, dubbing_projects, processing_runs CASCADE").ConfigureAwait(true);
            }

            var wiped = await CountAsync(connectionString, tenantId).ConfigureAwait(true);
            Assert.Equal(0, wiped.Tenants);
            Assert.Equal(0, wiped.Projects);
            Assert.Equal(0, wiped.Runs);

            // Restore and verify counts.
            await container.ExecScriptAsync(backup.Stdout).ConfigureAwait(true);
            var after = await CountAsync(connectionString, tenantId).ConfigureAwait(true);
            Assert.Equal(before, after);
        }
    }

    private static async Task<(int Tenants, int Projects, int Runs)> CountAsync(string connectionString, Guid tenantId)
    {
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = new AppDbContext(CreatePgOptions(connectionString));
            var tenants = await db.Tenants.CountAsync().ConfigureAwait(true);
            var projects = await db.DubbingProjects.CountAsync().ConfigureAwait(true);
            var runs = await db.Set<ProcessingRun>().CountAsync().ConfigureAwait(true);
            return (tenants, projects, runs);
        }
    }

    private static async Task SeedAsync(
        DbContextOptions<AppDbContext> options,
        Guid tenantId,
        Guid projectId,
        Guid runId)
    {
        var now = DateTimeOffset.UtcNow;
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = new AppDbContext(options);
            db.Tenants.Add(new Tenant(tenantId, $"T-{tenantId:N}", $"t-{tenantId:N}", now));
            var contentId = Guid.NewGuid();
            var hash = new string('c', 64);
            db.Set<ContentObject>().Add(new ContentObject(
                contentId, tenantId, hash, hash, 1024, "audio/flac",
                string.Concat(tenantId.ToString("N"), "/seed/canonical.flac"),
                ContentObjectStatus.Committed, now, now));
            db.Set<MediaAsset>().Add(new MediaAsset(
                Guid.NewGuid(), tenantId, projectId, contentId, "canonical.flac", "flac", "flac", null,
                1024, 4000, 48000, 2, "stereo", MediaAssetStatus.Valid, null, hash, now));
            db.DubbingProjects.Add(new DubbingProject(
                projectId, tenantId, "en", "es", ProjectStatus.Processing,
                "{}", new string('b', 64), null, runId, now, now));
            db.Set<ProcessingRun>().Add(new ProcessingRun(
                runId, tenantId, projectId, 0, ProcessingRunStatus.Running,
                "1.0.0", new string('b', 64), new string('d', 64), new string('e', 64),
                now, now, now, null));
            await db.SaveChangesAsync().ConfigureAwait(true);
        }
    }

    private sealed record BackupManifest(DateTimeOffset TakenAt, Dictionary<string, long> Tables, string Hash)
    {
        public string ToJson()
        {
            return JsonSerializer.Serialize(this, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }

        public static BackupManifest FromJson(string json)
        {
            var manifest = JsonSerializer.Deserialize<BackupManifest>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            Assert.NotNull(manifest);
            return manifest;
        }
    }
}
