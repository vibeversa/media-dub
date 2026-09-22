using System.Data.Common;
using System.Net.Sockets;
using DubbingPlatform.Application.MultiTenancy;
using DubbingPlatform.Application.Validation;
using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Domain.Enums;
using DubbingPlatform.Domain.Exceptions;
using DubbingPlatform.Infrastructure.Persistence;
using DubbingPlatform.Infrastructure.Persistence.Interceptors;
using EFCore.NamingConventions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit.Abstractions;

namespace DubbingPlatform.IntegrationTests.Identity;

public sealed class IdentityPreferencesTests
{
    private readonly ITestOutputHelper _output;

    public IdentityPreferencesTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [SkippableFact]
    public async Task Unique_Subject_Enforced()
    {
        var container = await StartContainerAsync().ConfigureAwait(true);
        await using (container.ConfigureAwait(true))
        {
            var tenantId = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;
            var options = CreateOptions(container);

            using (TenantContext.BeginMaintenanceScope())
            {
                using var migrate = new AppDbContext(options);
                await migrate.Database.MigrateAsync().ConfigureAwait(true);
            }

            using (TenantContext.BeginScope(tenantId))
            {
                using (var context = new AppDbContext(options))
                {
                    context.Tenants.Add(new Tenant(tenantId, "Identity", $"identity-{tenantId:N}", now));
                    await context.SaveChangesAsync().ConfigureAwait(true);
                }

                using (var context = new AppDbContext(options))
                {
                    context.Set<TenantUser>().Add(new TenantUser(
                        Guid.NewGuid(), tenantId, "sub-123", "user@example.com", "User One",
                        TenantUserStatus.Active, now, now));
                    await context.SaveChangesAsync().ConfigureAwait(true);
                }

                using (var context = new AppDbContext(options))
                {
                    context.Set<TenantUser>().Add(new TenantUser(
                        Guid.NewGuid(), tenantId, "sub-123", "other@example.com", "User Two",
                        TenantUserStatus.Active, now, now));
                    var ex = await Assert.ThrowsAnyAsync<Exception>(() => context.SaveChangesAsync()).ConfigureAwait(true);
                    AssertUniqueViolation(ex);
                }
            }
        }
    }

    [SkippableFact]
    public async Task Preference_RoundTrip_Persists()
    {
        var container = await StartContainerAsync().ConfigureAwait(true);
        await using (container.ConfigureAwait(true))
        {
            var tenantId = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;
            var options = CreateOptions(container);

            using (TenantContext.BeginMaintenanceScope())
            {
                using var migrate = new AppDbContext(options);
                await migrate.Database.MigrateAsync().ConfigureAwait(true);
            }

            var userId = Guid.NewGuid();
            using (TenantContext.BeginScope(tenantId))
            {
                using (var context = new AppDbContext(options))
                {
                    context.Tenants.Add(new Tenant(tenantId, "Prefs", $"prefs-{tenantId:N}", now));
                    context.Set<TenantUser>().Add(new TenantUser(
                        userId, tenantId, "sub-prefs", "prefs@example.com", "Prefs User",
                        TenantUserStatus.Active, now, now));
                    await context.SaveChangesAsync().ConfigureAwait(true);
                }

                using (var context = new AppDbContext(options))
                {
                    context.Set<UserPreference>().Add(new UserPreference(
                        tenantId, userId, "locale", "\"en-US\"", now));
                    await context.SaveChangesAsync().ConfigureAwait(true);
                }

                using (var context = new AppDbContext(options))
                {
                    var found = await context.Set<UserPreference>()
                        .SingleAsync(p => p.TenantId == tenantId && p.UserId == userId && p.Key == "locale")
                        .ConfigureAwait(true);
                    Assert.Equal("\"en-US\"", found.ValueJson);
                }
            }
        }
    }

    [Fact]
    public void Preference_UnknownKey_Rejected()
    {
        var ex = Assert.Throws<DomainException>(() => new UserPreference(
            Guid.NewGuid(), Guid.NewGuid(), "apiSecret", "\"hunter2\"", DateTimeOffset.UtcNow));
        Assert.Contains("not allowed", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Preference_Oversize_Value_Rejected()
    {
        var big = new string('x', UserPreference.MaxValueBytes + 1);
        var ex = Assert.Throws<DomainException>(() => new UserPreference(
            Guid.NewGuid(), Guid.NewGuid(), "locale", big, DateTimeOffset.UtcNow));
        Assert.Contains("at most", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Preference_Secret_Property_Rejected()
    {
        var ex = Assert.Throws<DomainException>(() => new UserPreference(
            Guid.NewGuid(), Guid.NewGuid(), "notificationPreferences",
            """{"password":"hunter2"}""", DateTimeOffset.UtcNow));
        Assert.Contains("secret", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task Membership_Uniqueness_Enforced()
    {
        var container = await StartContainerAsync().ConfigureAwait(true);
        await using (container.ConfigureAwait(true))
        {
            var tenantId = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;
            var options = CreateOptions(container);

            using (TenantContext.BeginMaintenanceScope())
            {
                using var migrate = new AppDbContext(options);
                await migrate.Database.MigrateAsync().ConfigureAwait(true);
            }

            var projectId = Guid.NewGuid();
            var userId = Guid.NewGuid();
            using (TenantContext.BeginScope(tenantId))
            {
                using (var context = new AppDbContext(options))
                {
                    context.Tenants.Add(new Tenant(tenantId, "Members", $"members-{tenantId:N}", now));
                    context.DubbingProjects.Add(new DubbingProject(
                        projectId, tenantId, "en", "de", ProjectStatus.Created,
                        "{}", new string('a', 64), null, null, now, now));
                    context.Set<TenantUser>().Add(new TenantUser(
                        userId, tenantId, "sub-member", "member@example.com", "Member",
                        TenantUserStatus.Active, now, now));
                    await context.SaveChangesAsync().ConfigureAwait(true);
                }

                using (var context = new AppDbContext(options))
                {
                    context.Set<ProjectMembership>().Add(new ProjectMembership(
                        Guid.NewGuid(), tenantId, projectId, userId, ProjectRole.ProjectEditor, null, now));
                    await context.SaveChangesAsync().ConfigureAwait(true);
                }

                using (var context = new AppDbContext(options))
                {
                    context.Set<ProjectMembership>().Add(new ProjectMembership(
                        Guid.NewGuid(), tenantId, projectId, userId, ProjectRole.Reviewer, null, now));
                    var ex = await Assert.ThrowsAnyAsync<Exception>(() => context.SaveChangesAsync()).ConfigureAwait(true);
                    AssertUniqueViolation(ex);
                }
            }
        }
    }

    [SkippableFact]
    public async Task Rls_Blocks_CrossTenant_Read()
    {
        var container = await StartContainerAsync().ConfigureAwait(true);
        await using (container.ConfigureAwait(true))
        {
            var tenantA = Guid.NewGuid();
            var tenantB = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;
            var options = CreateOptions(container);

            using (TenantContext.BeginMaintenanceScope())
            {
                using var migrate = new AppDbContext(options);
                await migrate.Database.MigrateAsync().ConfigureAwait(true);
            }

            var userId = Guid.NewGuid();
            using (TenantContext.BeginMaintenanceScope())
            {
                using var context = new AppDbContext(options);
                context.Tenants.Add(new Tenant(tenantA, "Tenant A", $"tenant-a-{tenantA:N}", now));
                context.Tenants.Add(new Tenant(tenantB, "Tenant B", $"tenant-b-{tenantB:N}", now));
                context.Set<TenantUser>().Add(new TenantUser(
                    userId, tenantA, "sub-rls", "rls@example.com", "RLS User",
                    TenantUserStatus.Active, now, now));
                await context.SaveChangesAsync().ConfigureAwait(true);
            }

            using (TenantContext.BeginScope(tenantA))
            {
                using var context = new AppDbContext(options);
                Assert.Equal(1, await context.Set<TenantUser>().CountAsync().ConfigureAwait(true));
            }

            using (TenantContext.BeginScope(tenantB))
            {
                using var context = new AppDbContext(options);
                Assert.Equal(0, await context.Set<TenantUser>().CountAsync().ConfigureAwait(true));
                Assert.Equal(0, await context.Set<UserPreference>().CountAsync().ConfigureAwait(true));
                Assert.Equal(0, await context.Set<ProjectMembership>().CountAsync().ConfigureAwait(true));
            }

            await using var connection = new NpgsqlConnection(container.GetConnectionString());
            await connection.OpenAsync().ConfigureAwait(true);
            await EnsureAppRoleAsync(connection).ConfigureAwait(true);
            Assert.Equal(0, await CountAsRoleAsync(connection, "app_role", "tenant_users", tenantB).ConfigureAwait(true));
            Assert.Equal(1, await CountAsRoleAsync(connection, "app_role", "tenant_users", tenantA).ConfigureAwait(true));

            using (TenantContext.BeginMaintenanceScope())
            {
                using var context = new AppDbContext(options);
                var policies = await QuerySingleColumnAsync(
                    context, "SELECT policyname FROM pg_policies WHERE schemaname = 'public' AND tablename IN ('tenant_users','user_preferences','project_memberships')")
                    .ConfigureAwait(true);
                Assert.Equal(3, policies.Count(p => string.Equals(p, "tenant_isolation", StringComparison.Ordinal)));

                var rls = await QuerySingleColumnAsync(
                    context, "SELECT tablename FROM pg_tables WHERE schemaname = 'public' AND rowsecurity = true AND tablename IN ('tenant_users','user_preferences','project_memberships')")
                    .ConfigureAwait(true);
                Assert.Equal(3, rls.Count);
            }
        }
    }

    [SkippableFact]
    public async Task Archival_Flag_Persists()
    {
        var container = await StartContainerAsync().ConfigureAwait(true);
        await using (container.ConfigureAwait(true))
        {
            var tenantId = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;
            var options = CreateOptions(container);

            using (TenantContext.BeginMaintenanceScope())
            {
                using var migrate = new AppDbContext(options);
                await migrate.Database.MigrateAsync().ConfigureAwait(true);
            }

            var projectId = Guid.NewGuid();
            using (TenantContext.BeginScope(tenantId))
            {
                using (var context = new AppDbContext(options))
                {
                    context.Tenants.Add(new Tenant(tenantId, "Archive", $"archive-{tenantId:N}", now));
                    context.DubbingProjects.Add(new DubbingProject(
                        projectId, tenantId, "en", "de", ProjectStatus.Created,
                        "{}", new string('a', 64), null, null, now, now,
                        "My project", "A description"));
                    await context.SaveChangesAsync().ConfigureAwait(true);
                }

                using (var context = new AppDbContext(options))
                {
                    var project = await context.DubbingProjects.SingleAsync(p => p.Id == projectId).ConfigureAwait(true);
                    Assert.False(project.IsArchived);
                    Assert.Equal("My project", project.Name);
                    project.Archive(DateTimeOffset.UtcNow);
                    await context.SaveChangesAsync().ConfigureAwait(true);
                }

                using (var context = new AppDbContext(options))
                {
                    var reloaded = await context.DubbingProjects.AsNoTracking().SingleAsync(p => p.Id == projectId).ConfigureAwait(true);
                    Assert.True(reloaded.IsArchived);
                    Assert.NotNull(reloaded.ArchivedAt);
                }
            }
        }
    }

    [Fact]
    public void Project_Name_Overlong_Rejected()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.Throws<DomainException>(() => new DubbingProject(
            Guid.NewGuid(), Guid.NewGuid(), "en", "de", ProjectStatus.Created,
            "{}", new string('a', 64), null, null, now, now,
            new string('n', 201)));
    }

    [Fact]
    public void ProcessingSettings_SchemaVersion1_Accepted()
    {
        var validator = new ProjectProcessingSettingsValidator();
        var json = """{"schemaVersion":1,"sourceSeparationPolicy":"auto","reviewThreshold":0.5,"glossary":[{"sourceTerm":"hello","targetTerm":"hallo"}],"styleInstructions":"formal"}""";
        var result = validator.Validate(json);
        Assert.True(result.IsValid);
    }

    [Fact]
    public void ProcessingSettings_UnknownVersion_Rejected_With_Code()
    {
        var validator = new ProjectProcessingSettingsValidator();
        var result = validator.Validate("""{"schemaVersion":2}""");
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => string.Equals(e.ErrorCode, ProjectProcessingSettingsValidator.UnsupportedVersionCode, StringComparison.Ordinal));
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
            _output.WriteLine($"Docker/PostgreSQL unavailable, skipping identity test: {ex.Message}");
            Skip.If(true, $"Docker/PostgreSQL unavailable: {ex.Message}");
            throw new InvalidOperationException("Unreachable: Skip.If always throws.", ex);
        }
    }

    private static async Task EnsureAppRoleAsync(NpgsqlConnection connection)
    {
        using var role = connection.CreateCommand();
        role.CommandText = "DO $$ BEGIN CREATE ROLE app_role NOLOGIN; EXCEPTION WHEN duplicate_object THEN NULL; END $$;";
        await role.ExecuteNonQueryAsync().ConfigureAwait(true);

        using var grants = connection.CreateCommand();
        grants.CommandText = "GRANT USAGE ON SCHEMA public TO app_role; GRANT SELECT ON ALL TABLES IN SCHEMA public TO app_role;";
        await grants.ExecuteNonQueryAsync().ConfigureAwait(true);
    }

    private static async Task<long> CountAsRoleAsync(NpgsqlConnection connection, string role, string table, Guid tenantId)
    {
        using var setRole = connection.CreateCommand();
        setRole.CommandText = string.Concat("SET ROLE ", role);
        await setRole.ExecuteNonQueryAsync().ConfigureAwait(true);

        try
        {
            using var setTenant = connection.CreateCommand();
            setTenant.CommandText = "SELECT set_config('app.tenant_id', $1, false)";
            setTenant.Parameters.Add(new NpgsqlParameter { Value = tenantId.ToString("D") });
            await setTenant.ExecuteNonQueryAsync().ConfigureAwait(true);

            using var count = connection.CreateCommand();
            count.CommandText = string.Concat("SELECT COUNT(*) FROM ", table);
            var result = await count.ExecuteScalarAsync().ConfigureAwait(true);
            return (long)(result ?? 0L);
        }
        finally
        {
            using var reset = connection.CreateCommand();
            reset.CommandText = "RESET ROLE";
            await reset.ExecuteNonQueryAsync().ConfigureAwait(true);
        }
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
