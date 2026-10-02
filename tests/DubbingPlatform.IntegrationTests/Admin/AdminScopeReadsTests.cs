using DubbingPlatform.Application.Services;
using DubbingPlatform.Application.Diagnostics;
using DubbingPlatform.Application.Diagnostics.Dto;
using DubbingPlatform.Application.MultiTenancy;
using DubbingPlatform.Application.Options;
using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Domain.Enums;
using DubbingPlatform.Infrastructure.Persistence;
using DubbingPlatform.Infrastructure.Persistence.Interceptors;
using DubbingPlatform.IntegrationTests.Fixtures;
using EFCore.NamingConventions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Testcontainers.PostgreSql;
using Xunit.Abstractions;

namespace DubbingPlatform.IntegrationTests.Admin;

/// <summary>
/// GAP-024: the admin scope reads behind the operator panels (tenants, users,
/// retention, feature flags, audit) are provisioned, so the panels no longer
/// render "not provisioned on this backend yet". Proves the data path over PG:
/// tenant scoping, role resolution without leaking emails, effective windows,
/// effective flags, and paged newest-first audit reads with no payload details.
/// </summary>
public sealed class AdminScopeReadsTests : TestFixtureBase
{
    private readonly ITestOutputHelper _output;

    public AdminScopeReadsTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [SkippableFact]
    public async Task Reads_Return_Scoped_Operator_Data()
    {
        var pg = await StartPostgresAsync().ConfigureAwait(true);
        await using (pg.ConfigureAwait(true))
        {
            var options = CreatePgOptions(pg.GetConnectionString());
            await MigrateAsync(pg.GetConnectionString()).ConfigureAwait(true);
            var tenantId = Guid.NewGuid();
            var otherTenantId = Guid.NewGuid();
            var userId = await SeedAsync(options, tenantId, otherTenantId).ConfigureAwait(true);
            var service = CreateService(options);

            // --- tenants: the caller's tenant only, never another tenant -----
            var tenants = await service.GetTenantsAsync(tenantId).ConfigureAwait(true);
            var tenant = Assert.Single(tenants);
            Assert.Equal(tenantId, tenant.Id);
            Assert.Equal("Tenant One", tenant.Name);
            Assert.Equal("tenant-one", tenant.Slug);
            Assert.Empty(await service.GetTenantsAsync(Guid.NewGuid()).ConfigureAwait(true));

            // --- users: roles resolved, no email/external subject ----------
            var users = await service.GetUsersAsync(tenantId).ConfigureAwait(true);
            var row = Assert.Single(users);
            Assert.Equal(userId, row.Id);
            Assert.Equal("Operator One", row.DisplayName);
            Assert.Equal(["ProjectOwner"], row.Roles);
            Assert.DoesNotContain(users, u => u.Roles.Any(r => r.Contains("@", StringComparison.Ordinal)));

            // Tenant isolation: the other tenant has no users here.
            Assert.Empty(await service.GetUsersAsync(otherTenantId).ConfigureAwait(true));

            // --- retention: effective windows from configuration ------------
            var retention = service.GetRetentionPolicies();
            Assert.Equal(3, retention.Policies.Count);
            Assert.Contains(retention.Policies, p => p.Scope == "artifact-intermediate" && p.RetentionDays == 30);
            Assert.Contains(retention.Policies, p => p.Scope == "artifact-final" && p.RetentionDays == 90);
            Assert.Contains(retention.Policies, p => p.Scope == "audit" && p.RetentionDays == 365);
            Assert.All(retention.Policies, p => Assert.False(string.IsNullOrWhiteSpace(p.Description)));

            // --- feature flags: optional capabilities off by default -------
            var flags = service.GetFeatureFlags();
            Assert.Equal(3, flags.Flags.Count);

            // The read reports effective configuration: the one flag the service
            // was built with is on, the untouched optional ones stay off.
            Assert.True(flags.Flags.Single(f => f.Key == "videoIntelligence").Enabled);
            Assert.False(flags.Flags.Single(f => f.Key == "lipSync").Enabled);
            Assert.False(flags.Flags.Single(f => f.Key == "localInference").Enabled);

            // No rollout freeze in this deployment, and no flag write route.
            Assert.All(flags.Flags, f => Assert.False(f.Frozen));
            Assert.All(flags.Flags, f => Assert.False(string.IsNullOrWhiteSpace(f.Description)));

            // --- audit: newest first, paged, no payload details -------------
            var page1 = await service.GetAuditEventsAsync(tenantId, 1, 2).ConfigureAwait(true);
            Assert.Equal(3, page1.Total);
            Assert.True(page1.HasMore);
            Assert.Equal(2, page1.Items.Count);
            Assert.True(page1.Items[0].Timestamp >= page1.Items[1].Timestamp);

            var page2 = await service.GetAuditEventsAsync(tenantId, 2, 2).ConfigureAwait(true);
            Assert.Single(page2.Items);
            Assert.False(page2.HasMore);

            var all = page1.Items.Concat(page2.Items).Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
            Assert.Equal(3, all.Count);

            // No details payload is exposed on the row type at all.
            Assert.DoesNotContain(
                typeof(AdminAuditEventDto).GetProperties().Select(p => p.Name),
                n => n.Contains("details", StringComparison.OrdinalIgnoreCase));

            var foreign = await service.GetAuditEventsAsync(otherTenantId, 1, 20).ConfigureAwait(true);
            Assert.Equal(1, foreign.Total);

            _output.WriteLine($"Admin scope reads verified for tenant {tenantId}.");
        }
    }

    [SkippableFact]
    public async Task Page_And_Page_Size_Are_Clamped()
    {
        var pg = await StartPostgresAsync().ConfigureAwait(true);
        await using (pg.ConfigureAwait(true))
        {
            var options = CreatePgOptions(pg.GetConnectionString());
            await MigrateAsync(pg.GetConnectionString()).ConfigureAwait(true);
            var tenantId = Guid.NewGuid();
            await SeedAsync(options, tenantId, Guid.NewGuid()).ConfigureAwait(true);
            var service = CreateService(options);

            var clamped = await service.GetAuditEventsAsync(tenantId, 0, 5000).ConfigureAwait(true);
            Assert.Equal(1, clamped.Page);
            Assert.Equal(100, clamped.PageSize);
        }
    }

    private static AdminScopeReadsService CreateService(DbContextOptions<AppDbContext> options) =>
        new(
            new TestFactory(options),
            Microsoft.Extensions.Options.Options.Create(new RetentionOptions()),
            Microsoft.Extensions.Options.Options.Create(new FeatureOptions
            {
                VideoIntelligenceEnabled = true,
            }),
            NullLogger<AdminScopeReadsService>.Instance);

    private static async Task<Guid> SeedAsync(
        DbContextOptions<AppDbContext> options,
        Guid tenantId,
        Guid otherTenantId)
    {
        var now = DateTimeOffset.UtcNow;
        var userId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = new AppDbContext(options);
            db.Tenants.Add(new Tenant(tenantId, "Tenant One", "tenant-one", now));
            db.Tenants.Add(new Tenant(otherTenantId, "Tenant Two", "tenant-two", now));
            db.Set<TenantUser>().Add(new TenantUser(
                userId, tenantId, "sub-one", "operator@example.test", "Operator One", TenantUserStatus.Active, now, now));
            db.DubbingProjects.Add(new DubbingProject(
                projectId, tenantId, "en", "es", ProjectStatus.Created, "{}", new string('a', 64), null, null, now, now));
            db.Set<ProjectMembership>().Add(new ProjectMembership(
                Guid.NewGuid(), tenantId, projectId, userId, ProjectRole.ProjectOwner, null, now));
            db.Set<AuditEvent>().Add(new AuditEvent(
                Guid.NewGuid(), tenantId, null, "operator@example.test", "processing.start", "processing-run", "run-1", "{\"correlationId\":\"c1\"}", now));
            db.Set<AuditEvent>().Add(new AuditEvent(
                Guid.NewGuid(), tenantId, null, "operator@example.test", "admin.access", "admin", "usage", null, now.AddMinutes(1)));
            db.Set<AuditEvent>().Add(new AuditEvent(
                Guid.NewGuid(), tenantId, null, "operator@example.test", "review.approve", "review-item", "rev-1", null, now.AddMinutes(2)));
            await db.SaveChangesAsync().ConfigureAwait(true);
        }

        using (TenantContext.BeginScope(otherTenantId))
        {
            using var db = new AppDbContext(options);
            db.Set<AuditEvent>().Add(new AuditEvent(
                Guid.NewGuid(), otherTenantId, null, "other@example.test", "auth.login", "tenant-user", "u-1", null, now));
            await db.SaveChangesAsync().ConfigureAwait(true);
        }

        return userId;
    }

    private sealed class TestFactory : IStageExecutionContextFactory
    {
        private readonly DbContextOptions<AppDbContext> _options;

        public TestFactory(DbContextOptions<AppDbContext> options)
        {
            _options = options;
        }

        public DbContext CreateDbContext() => new AppDbContext(_options);
    }
}