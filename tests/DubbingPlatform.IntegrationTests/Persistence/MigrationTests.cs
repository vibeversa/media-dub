using System.Data.Common;
using System.Net.Sockets;
using DubbingPlatform.Application.MultiTenancy;
using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Domain.Enums;
using DubbingPlatform.Domain.Exceptions;
using DubbingPlatform.Infrastructure.Persistence;
using DubbingPlatform.Infrastructure.Persistence.Interceptors;
using EFCore.NamingConventions;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using Xunit.Abstractions;

namespace DubbingPlatform.IntegrationTests.Persistence;

/// <summary>
/// Verifies the InitialCreate migration against PostgreSQL 16 via Testcontainers.
/// All tests are skippable when Docker is unavailable (CI runs them live).
/// AppDbContext maps unique violations to <see cref="DomainException"/> with a
/// CONFLICT prefix, so uniqueness tests accept either that or the underlying
/// <see cref="DbUpdateException"/>.
/// </summary>
public sealed class MigrationTests
{
    private static readonly string[] ExpectedTables =
    [
        "tenants", "dubbing_projects", "processing_runs", "media_assets",
        "upload_sessions", "upload_parts", "speakers", "speaker_voice_assignments",
        "voice_profiles", "consent_records", "speech_segments", "segment_selections", "overlap_groups",
        "segment_overlaps", "context_windows", "segment_context_assignments",
        "transcript_versions", "translation_versions", "generated_audio_artifacts",
        "sync_results", "stage_executions", "run_stage_summaries",
        "stage_unit_completions", "content_objects", "artifacts", "artifact_parents",
        "stage_input_artifacts", "stage_output_artifacts", "provider_executions",
        "provider_capability_descriptors", "provider_route_snapshots",
        "prompt_templates", "prompt_template_versions", "quality_results",
        "review_items", "review_decisions", "output_assets", "export_jobs",
        "export_artifacts", "audit_events", "idempotency_records",
        "cost_reservations", "quota_usages", "processing_policies",
        "retention_holds", "deletion_jobs", "outbox_state", "outbox_message",
        "inbox_state", "tenant_users", "user_preferences", "project_memberships",
        "notifications", "activity_events", "voice_preview_jobs",
    ];

    private static readonly string[] RlsTables =
    [
        "dubbing_projects", "processing_runs", "media_assets",
        "upload_sessions", "upload_parts", "voice_profiles", "speakers",
        "speech_segments", "segment_selections", "context_windows", "segment_context_assignments",
        "overlap_groups", "segment_overlaps", "speaker_voice_assignments",
        "consent_records", "transcript_versions", "translation_versions",
        "generated_audio_artifacts", "sync_results", "stage_executions",
        "run_stage_summaries", "stage_unit_completions", "content_objects",
        "artifacts", "artifact_parents", "stage_input_artifacts",
        "stage_output_artifacts", "provider_executions",
        "provider_capability_descriptors", "provider_route_snapshots",
        "prompt_templates", "prompt_template_versions", "quality_results",
        "review_items", "review_decisions", "output_assets", "export_jobs",
        "export_artifacts", "audit_events", "idempotency_records",
        "cost_reservations", "quota_usages", "processing_policies",
        "retention_holds", "deletion_jobs",
        "tenant_users", "user_preferences", "project_memberships",
        "notifications", "activity_events", "voice_preview_jobs",
    ];

    private readonly ITestOutputHelper _output;

    public MigrationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [SkippableFact]
    public async Task Migration_Applies_Cleanly()
    {
        var container = await StartContainerAsync().ConfigureAwait(true);
        await using (container.ConfigureAwait(true))
        {
            using (TenantContext.BeginMaintenanceScope())
            {
                var options = CreateOptions(container);
                using var context = new AppDbContext(options);
                await context.Database.MigrateAsync().ConfigureAwait(true);

                var tables = await GetTableNamesAsync(context).ConfigureAwait(true);
                foreach (var expected in ExpectedTables)
                {
                    Assert.Contains(expected, tables);
                }

                var rlsEnabled = await GetRlsEnabledTablesAsync(context).ConfigureAwait(true);
                foreach (var table in RlsTables)
                {
                    Assert.Contains(table, rlsEnabled);
                }

                var policies = await GetPoliciesAsync(context).ConfigureAwait(true);
                var tenantPolicies = policies.Count(p => string.Equals(p, "tenant_isolation", StringComparison.Ordinal));
                Assert.True(tenantPolicies >= RlsTables.Length, $"Expected at least {RlsTables.Length} tenant_isolation policies, found {tenantPolicies}.");
            }
        }
    }

    [SkippableFact]
    public async Task Snake_Case_Names_Verified()
    {
        var container = await StartContainerAsync().ConfigureAwait(true);
        await using (container.ConfigureAwait(true))
        {
            using (TenantContext.BeginMaintenanceScope())
            {
                var options = CreateOptions(container);
                using var context = new AppDbContext(options);
                await context.Database.MigrateAsync().ConfigureAwait(true);

                var tables = await GetTableNamesAsync(context).ConfigureAwait(true);
                foreach (var table in tables)
                {
                    Assert.Equal(table.ToLowerInvariant(), table);
                }

                var columns = await GetColumnNamesAsync(context).ConfigureAwait(true);
                Assert.NotEmpty(columns);
                foreach (var column in columns)
                {
                    Assert.Equal(column.ToLowerInvariant(), column);
                }
            }
        }
    }

    [SkippableFact]
    public async Task Unique_Constraints_Verified()
    {
        var container = await StartContainerAsync().ConfigureAwait(true);
        await using (container.ConfigureAwait(true))
        {
            var tenantId = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;

            using (TenantContext.BeginMaintenanceScope())
            {
                var options = CreateOptions(container);
                using var migrate = new AppDbContext(options);
                await migrate.Database.MigrateAsync().ConfigureAwait(true);
            }

            using (TenantContext.BeginScope(tenantId))
            {
                var options = CreateOptions(container);

                using (var context = new AppDbContext(options))
                {
                    context.Tenants.Add(new Tenant(tenantId, "Unique", "unique", now));
                    await context.SaveChangesAsync().ConfigureAwait(true);
                }

                var projectId = Guid.NewGuid();
                using (var context = new AppDbContext(options))
                {
                    context.DubbingProjects.Add(new DubbingProject(
                        projectId, tenantId, "en", "de", ProjectStatus.Created,
                        "{}", new string('a', 64), null, null, now, now));
                    await context.SaveChangesAsync().ConfigureAwait(true);
                }

                using (var context = new AppDbContext(options))
                {
                    context.ContentObjects.Add(new ContentObject(
                        Guid.NewGuid(), tenantId, "dup-hash", new string('b', 64),
                        10, "audio/wav", "keys/a.wav", ContentObjectStatus.Pending, now, now));
                    await context.SaveChangesAsync().ConfigureAwait(true);
                }

                using (var context = new AppDbContext(options))
                {
                    context.ContentObjects.Add(new ContentObject(
                        Guid.NewGuid(), tenantId, "dup-hash", new string('c', 64),
                        11, "audio/wav", "keys/b.wav", ContentObjectStatus.Pending, now, now));
                    var ex = await Assert.ThrowsAnyAsync<Exception>(() => context.SaveChangesAsync()).ConfigureAwait(true);
                    AssertUniqueViolation(ex);
                }

                using (var context = new AppDbContext(options))
                {
                    context.ProcessingRuns.Add(NewRun(Guid.NewGuid(), tenantId, projectId, ProcessingRunStatus.Pending, now));
                    await context.SaveChangesAsync().ConfigureAwait(true);
                }

                using (var context = new AppDbContext(options))
                {
                    context.ProcessingRuns.Add(NewRun(Guid.NewGuid(), tenantId, projectId, ProcessingRunStatus.Pending, now));
                    var ex = await Assert.ThrowsAnyAsync<Exception>(() => context.SaveChangesAsync()).ConfigureAwait(true);
                    AssertUniqueViolation(ex);
                }

                var sessionId = Guid.NewGuid();
                using (var context = new AppDbContext(options))
                {
                    context.UploadSessions.Add(new UploadSession(
                        sessionId, tenantId, projectId, "source.mp4", "video/mp4",
                        100, 10, "keys/upload", null, UploadStatus.Created,
                        null, null, 0, now, now.AddHours(1)));
                    await context.SaveChangesAsync().ConfigureAwait(true);
                }

                using (var context = new AppDbContext(options))
                {
                    context.UploadParts.Add(new UploadPart(Guid.NewGuid(), tenantId, sessionId, 1, "etag-1", 10, now));
                    await context.SaveChangesAsync().ConfigureAwait(true);
                }

                using (var context = new AppDbContext(options))
                {
                    context.UploadParts.Add(new UploadPart(Guid.NewGuid(), tenantId, sessionId, 1, "etag-2", 10, now));
                    var ex = await Assert.ThrowsAnyAsync<Exception>(() => context.SaveChangesAsync()).ConfigureAwait(true);
                    AssertUniqueViolation(ex);
                }
            }
        }
    }

    [SkippableFact]
    public async Task One_Active_Run_Per_Project()
    {
        var container = await StartContainerAsync().ConfigureAwait(true);
        await using (container.ConfigureAwait(true))
        {
            var tenantId = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;
            var projectId = Guid.NewGuid();

            using (TenantContext.BeginMaintenanceScope())
            {
                var options = CreateOptions(container);
                using var migrate = new AppDbContext(options);
                await migrate.Database.MigrateAsync().ConfigureAwait(true);
            }

            using (TenantContext.BeginScope(tenantId))
            {
                var options = CreateOptions(container);

                using (var context = new AppDbContext(options))
                {
                    context.Tenants.Add(new Tenant(tenantId, "Runs", "runs", now));
                    context.DubbingProjects.Add(new DubbingProject(
                        projectId, tenantId, "en", "de", ProjectStatus.Created,
                        "{}", new string('a', 64), null, null, now, now));
                    await context.SaveChangesAsync().ConfigureAwait(true);
                }

                var firstRunId = Guid.NewGuid();
                using (var context = new AppDbContext(options))
                {
                    context.ProcessingRuns.Add(NewRun(firstRunId, tenantId, projectId, ProcessingRunStatus.Pending, now));
                    await context.SaveChangesAsync().ConfigureAwait(true);
                }

                using (var context = new AppDbContext(options))
                {
                    context.ProcessingRuns.Add(NewRun(Guid.NewGuid(), tenantId, projectId, ProcessingRunStatus.Pending, now));
                    var ex = await Assert.ThrowsAnyAsync<Exception>(() => context.SaveChangesAsync()).ConfigureAwait(true);
                    AssertUniqueViolation(ex);
                    Assert.IsType<DomainException>(ex);
                }

                using (var context = new AppDbContext(options))
                {
                    await context.Database.ExecuteSqlRawAsync(
                        "UPDATE processing_runs SET status = 'Completed' WHERE id = {0}", firstRunId).ConfigureAwait(true);
                }

                using (var context = new AppDbContext(options))
                {
                    context.ProcessingRuns.Add(NewRun(Guid.NewGuid(), tenantId, projectId, ProcessingRunStatus.Pending, now));
                    await context.SaveChangesAsync().ConfigureAwait(true);
                }
            }
        }
    }

    [SkippableFact]
    public async Task Rls_Policies_Created()
    {
        var container = await StartContainerAsync().ConfigureAwait(true);
        await using (container.ConfigureAwait(true))
        {
            using (TenantContext.BeginMaintenanceScope())
            {
                var options = CreateOptions(container);
                using var context = new AppDbContext(options);
                await context.Database.MigrateAsync().ConfigureAwait(true);

                var rlsEnabled = await GetRlsEnabledTablesAsync(context).ConfigureAwait(true);
                Assert.Equal(RlsTables.Length, rlsEnabled.Intersect(RlsTables, StringComparer.Ordinal).Count());

                var policies = await GetPoliciesAsync(context).ConfigureAwait(true);
                Assert.Equal(RlsTables.Length, policies.Count(p => string.Equals(p, "tenant_isolation", StringComparison.Ordinal)));

                Assert.DoesNotContain("tenants", rlsEnabled);
                Assert.DoesNotContain("outbox_state", rlsEnabled);
                Assert.DoesNotContain("outbox_message", rlsEnabled);
                Assert.DoesNotContain("inbox_state", rlsEnabled);
            }
        }
    }

    private static ProcessingRun NewRun(Guid id, Guid tenantId, Guid projectId, ProcessingRunStatus status, DateTimeOffset now)
    {
        return new ProcessingRun(
            id, tenantId, projectId, 0, status, "v1",
            new string('a', 64), new string('b', 64), new string('c', 64),
            now, now, null, null);
    }

    private static void AssertUniqueViolation(Exception exception)
    {
        if (exception is DomainException domain &&
            domain.Message.Contains("CONFLICT", StringComparison.Ordinal))
        {
            return;
        }

        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is DbUpdateException)
            {
                return;
            }
        }

        Assert.Fail($"Expected unique-constraint violation (DomainException CONFLICT or DbUpdateException), got {exception.GetType().FullName}: {exception.Message}");
    }

    private static DbContextOptions<AppDbContext> CreateOptions(PostgreSqlContainer container)
    {
        return new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(container.GetConnectionString())
            .UseSnakeCaseNamingConvention()
            .AddInterceptors(new TenantSessionInterceptor())
            .Options;
    }

    private async Task<PostgreSqlContainer> StartContainerAsync()
    {
        try
        {
            var container = new PostgreSqlBuilder("postgres:16-alpine").Build();
            await container.StartAsync().ConfigureAwait(true);
            return container;
        }
#pragma warning disable CA1031 // Docker availability probe: any transport-level failure means "skip", domain failures surface later.
        catch (Exception ex) when (IsInfrastructureUnavailable(ex))
#pragma warning restore CA1031
        {
            _output.WriteLine($"Docker/PostgreSQL unavailable, skipping migration test: {ex.Message}");
            Skip.If(true, $"Docker/PostgreSQL unavailable: {ex.Message}");
            throw new InvalidOperationException("Unreachable: Skip.If always throws.", ex);
        }
    }

    private static async Task<HashSet<string>> GetTableNamesAsync(AppDbContext context)
    {
        var connection = context.Database.GetDbConnection();
        var shouldClose = false;
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync().ConfigureAwait(true);
            shouldClose = true;
        }

        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT table_name FROM information_schema.tables WHERE table_schema = 'public'";
            var tables = new HashSet<string>(StringComparer.Ordinal);
            using var reader = await command.ExecuteReaderAsync().ConfigureAwait(true);
            while (await reader.ReadAsync().ConfigureAwait(true))
            {
                tables.Add(reader.GetString(0));
            }

            return tables;
        }
        finally
        {
            if (shouldClose)
            {
                await connection.CloseAsync().ConfigureAwait(true);
            }
        }
    }

    private static async Task<HashSet<string>> GetColumnNamesAsync(AppDbContext context)
    {
        var connection = context.Database.GetDbConnection();
        var shouldClose = false;
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync().ConfigureAwait(true);
            shouldClose = true;
        }

        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT column_name FROM information_schema.columns WHERE table_schema = 'public'";
            var columns = new HashSet<string>(StringComparer.Ordinal);
            using var reader = await command.ExecuteReaderAsync().ConfigureAwait(true);
            while (await reader.ReadAsync().ConfigureAwait(true))
            {
                columns.Add(reader.GetString(0));
            }

            return columns;
        }
        finally
        {
            if (shouldClose)
            {
                await connection.CloseAsync().ConfigureAwait(true);
            }
        }
    }

    private static async Task<HashSet<string>> GetRlsEnabledTablesAsync(AppDbContext context)
    {
        return await QuerySingleColumnAsync(context, "SELECT tablename FROM pg_tables WHERE schemaname = 'public' AND rowsecurity = true").ConfigureAwait(true);
    }

    private static async Task<List<string>> GetPoliciesAsync(AppDbContext context)
    {
        var set = await QuerySingleColumnAsync(context, "SELECT policyname FROM pg_policies WHERE schemaname = 'public'").ConfigureAwait(true);
        return [.. set];
    }

    private static async Task<HashSet<string>> QuerySingleColumnAsync(AppDbContext context, string sql)
    {
        var connection = context.Database.GetDbConnection();
        var shouldClose = false;
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync().ConfigureAwait(true);
            shouldClose = true;
        }

        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            var values = new HashSet<string>(StringComparer.Ordinal);
            using var reader = await command.ExecuteReaderAsync().ConfigureAwait(true);
            while (await reader.ReadAsync().ConfigureAwait(true))
            {
                values.Add(reader.GetString(0));
            }

            return values;
        }
        finally
        {
            if (shouldClose)
            {
                await connection.CloseAsync().ConfigureAwait(true);
            }
        }
    }

    private static bool IsInfrastructureUnavailable(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            var name = current.GetType().FullName ?? string.Empty;
            if (name.Contains("Docker", StringComparison.Ordinal) ||
                name.Contains("Testcontainers", StringComparison.Ordinal))
            {
                return true;
            }

            if (current is HttpRequestException
                or TimeoutException
                or ObjectDisposedException
                or UnauthorizedAccessException
                or IOException
                or SocketException
                or DbException
                or InvalidOperationException)
            {
                return true;
            }
        }

        return false;
    }
}
