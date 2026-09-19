using System.Net.Sockets;
using DubbingPlatform.Application.MultiTenancy;
using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Domain.Enums;
using DubbingPlatform.Infrastructure.Persistence;
using DubbingPlatform.Infrastructure.Persistence.Interceptors;
using EFCore.NamingConventions;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using Xunit.Abstractions;

namespace DubbingPlatform.IntegrationTests.Persistence;

public sealed class SchemaSmokeTests
{
    private readonly ITestOutputHelper _output;

    public SchemaSmokeTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [SkippableFact]
    public async Task Can_Create_Schema_And_Insert_Tenant_Project()
    {
        PostgreSqlContainer container;
        try
        {
            container = new PostgreSqlBuilder("postgres:16-alpine").Build();
            await container.StartAsync().ConfigureAwait(true);
        }
#pragma warning disable CA1031 // Docker availability probe: any transport-level failure means "skip", domain failures surface later.
        catch (Exception ex) when (IsInfrastructureUnavailable(ex))
#pragma warning restore CA1031
        {
            _output.WriteLine($"Docker/PostgreSQL unavailable, skipping persistence smoke test: {ex.Message}");
            Skip.If(true, $"Docker/PostgreSQL unavailable: {ex.Message}");
            return;
        }

        await using (container.ConfigureAwait(true))
        {
            var tenantId = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;
            using (TenantContext.BeginScope(tenantId))
            {
                var options = new DbContextOptionsBuilder<AppDbContext>()
                    .UseNpgsql(container.GetConnectionString())
                    .UseSnakeCaseNamingConvention()
                    .AddInterceptors(new TenantSessionInterceptor())
                    .Options;
                using var context = new AppDbContext(options);
                await context.Database.EnsureCreatedAsync().ConfigureAwait(true);

                var tenant = new Tenant(tenantId, "Smoke", "smoke", now);
                context.Tenants.Add(tenant);
                await context.SaveChangesAsync().ConfigureAwait(true);

                var project = new DubbingProject(
                    Guid.NewGuid(),
                    tenantId,
                    "en",
                    "de",
                    ProjectStatus.Created,
                    "{}",
                    new string('a', 64),
                    null,
                    null,
                    now,
                    now);
                context.DubbingProjects.Add(project);
                await context.SaveChangesAsync().ConfigureAwait(true);

                var found = await context.DubbingProjects.SingleAsync(p => p.Id == project.Id).ConfigureAwait(true);
                Assert.Equal(tenantId, found.TenantId);
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
                or SocketException)
            {
                return true;
            }
        }

        return false;
    }
}
