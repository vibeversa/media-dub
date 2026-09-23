using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using DubbingPlatform.Api.Middleware;
using DubbingPlatform.Application.MultiTenancy;
using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Domain.Enums;
using DubbingPlatform.Infrastructure.Persistence;
using DubbingPlatform.Infrastructure.Persistence.Interceptors;
using EFCore.NamingConventions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using Testcontainers.PostgreSql;

namespace DubbingPlatform.IntegrationTests.Notifications;

/// <summary>
/// Task 012B: notification inbox over PG. Skips when Docker is unavailable.
/// Strict recipient scoping; cross-user/cross-tenant ids return 404.
/// </summary>
public sealed class NotificationsApiTests
{
    private const string TestSigningKey = "test-signing-key-0123456789abcdef-test-signing-key-01";

    private const string TestAudience = "dubbing-api";

    [SkippableFact]
    public async Task List_NewestFirst_Pagination()
    {
        var container = await StartPostgresAsync().ConfigureAwait(true);
        await using (container.ConfigureAwait(true))
        {
            var connectionString = container.GetConnectionString();
            await MigrateAsync(connectionString).ConfigureAwait(true);
            var tenantId = Guid.NewGuid();
            var recipient = Guid.NewGuid();
            await SeedTenantAsync(connectionString, tenantId).ConfigureAwait(true);
            var projectId = Guid.NewGuid();
            await SeedNotificationsAsync(connectionString, tenantId, recipient, projectId, 3).ConfigureAwait(true);

            using var factory = CreateFactory(connectionString);
            using var client = factory.CreateClient();
            UseToken(client, tenantId, recipient, "ProjectViewer");

            using var first = await client.GetAsync("/api/v1/notifications?page=1&pageSize=2").ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
            var firstDoc = JsonDocument.Parse(await first.Content.ReadAsStringAsync().ConfigureAwait(true));
            Assert.Equal(2, firstDoc.RootElement.GetProperty("items").EnumerateArray().Count());
            Assert.Equal(3, firstDoc.RootElement.GetProperty("total").GetInt64());
            Assert.True(firstDoc.RootElement.GetProperty("hasMore").GetBoolean());
            var firstCreated = firstDoc.RootElement.GetProperty("items").EnumerateArray().First().GetProperty("createdAt").GetDateTimeOffset();

            using var second = await client.GetAsync("/api/v1/notifications?page=2&pageSize=2").ConfigureAwait(true);
            var secondDoc = JsonDocument.Parse(await second.Content.ReadAsStringAsync().ConfigureAwait(true));
            Assert.Single(secondDoc.RootElement.GetProperty("items").EnumerateArray());
            var secondCreated = secondDoc.RootElement.GetProperty("items").EnumerateArray().First().GetProperty("createdAt").GetDateTimeOffset();
            Assert.True(firstCreated >= secondCreated);
        }
    }

    [SkippableFact]
    public async Task UnreadOnly_And_Count_Consistent()
    {
        var container = await StartPostgresAsync().ConfigureAwait(true);
        await using (container.ConfigureAwait(true))
        {
            var connectionString = container.GetConnectionString();
            await MigrateAsync(connectionString).ConfigureAwait(true);
            var tenantId = Guid.NewGuid();
            var recipient = Guid.NewGuid();
            await SeedTenantAsync(connectionString, tenantId).ConfigureAwait(true);
            var ids = await SeedNotificationsAsync(connectionString, tenantId, recipient, Guid.NewGuid(), 3).ConfigureAwait(true);
            await MarkReadDirectAsync(connectionString, tenantId, ids[0]).ConfigureAwait(true);

            using var factory = CreateFactory(connectionString);
            using var client = factory.CreateClient();
            UseToken(client, tenantId, recipient, "ProjectViewer");

            using var unread = await client.GetAsync("/api/v1/notifications?unreadOnly=true").ConfigureAwait(true);
            var unreadDoc = JsonDocument.Parse(await unread.Content.ReadAsStringAsync().ConfigureAwait(true));
            Assert.Equal(2, unreadDoc.RootElement.GetProperty("total").GetInt64());

            using var count = await client.GetAsync("/api/v1/notifications/unread-count").ConfigureAwait(true);
            var countDoc = JsonDocument.Parse(await count.Content.ReadAsStringAsync().ConfigureAwait(true));
            Assert.Equal(2, countDoc.RootElement.GetProperty("unreadCount").GetInt64());
        }
    }

    [SkippableFact]
    public async Task Read_Idempotent()
    {
        var container = await StartPostgresAsync().ConfigureAwait(true);
        await using (container.ConfigureAwait(true))
        {
            var connectionString = container.GetConnectionString();
            await MigrateAsync(connectionString).ConfigureAwait(true);
            var tenantId = Guid.NewGuid();
            var recipient = Guid.NewGuid();
            await SeedTenantAsync(connectionString, tenantId).ConfigureAwait(true);
            var ids = await SeedNotificationsAsync(connectionString, tenantId, recipient, Guid.NewGuid(), 1).ConfigureAwait(true);
            var publicId = string.Concat("ntf_", ids[0].ToString("N"));

            using var factory = CreateFactory(connectionString);
            using var client = factory.CreateClient();
            UseToken(client, tenantId, recipient, "ProjectViewer");

            using var first = await client.PostAsync($"/api/v1/notifications/{publicId}/read", null).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);

            using var second = await client.PostAsync($"/api/v1/notifications/{publicId}/read", null).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.OK, second.StatusCode);

            using var count = await client.GetAsync("/api/v1/notifications/unread-count").ConfigureAwait(true);
            var countDoc = JsonDocument.Parse(await count.Content.ReadAsStringAsync().ConfigureAwait(true));
            Assert.Equal(0, countDoc.RootElement.GetProperty("unreadCount").GetInt64());
        }
    }

    [SkippableFact]
    public async Task ReadAll_ReturnsCount_ZeroWhenNone()
    {
        var container = await StartPostgresAsync().ConfigureAwait(true);
        await using (container.ConfigureAwait(true))
        {
            var connectionString = container.GetConnectionString();
            await MigrateAsync(connectionString).ConfigureAwait(true);
            var tenantId = Guid.NewGuid();
            var recipient = Guid.NewGuid();
            await SeedTenantAsync(connectionString, tenantId).ConfigureAwait(true);
            await SeedNotificationsAsync(connectionString, tenantId, recipient, Guid.NewGuid(), 2).ConfigureAwait(true);

            using var factory = CreateFactory(connectionString);
            using var client = factory.CreateClient();
            UseToken(client, tenantId, recipient, "ProjectViewer");

            using var first = await client.PostAsync("/api/v1/notifications/read-all", null).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
            var firstDoc = JsonDocument.Parse(await first.Content.ReadAsStringAsync().ConfigureAwait(true));
            Assert.Equal(2, firstDoc.RootElement.GetProperty("markedCount").GetInt32());
            Assert.Equal(2, firstDoc.RootElement.GetProperty("marked").GetInt32());

            using var second = await client.PostAsync("/api/v1/notifications/read-all", null).ConfigureAwait(true);
            var secondDoc = JsonDocument.Parse(await second.Content.ReadAsStringAsync().ConfigureAwait(true));
            Assert.Equal(0, secondDoc.RootElement.GetProperty("markedCount").GetInt32());
            Assert.Equal(0, secondDoc.RootElement.GetProperty("marked").GetInt32());
        }
    }

    [SkippableFact]
    public async Task Expired_Excluded_And_Read404()
    {
        var container = await StartPostgresAsync().ConfigureAwait(true);
        await using (container.ConfigureAwait(true))
        {
            var connectionString = container.GetConnectionString();
            await MigrateAsync(connectionString).ConfigureAwait(true);
            var tenantId = Guid.NewGuid();
            var recipient = Guid.NewGuid();
            await SeedTenantAsync(connectionString, tenantId).ConfigureAwait(true);
            var expired = await SeedExpiredAsync(connectionString, tenantId, recipient).ConfigureAwait(true);
            var publicId = string.Concat("ntf_", expired.ToString("N"));

            using var factory = CreateFactory(connectionString);
            using var client = factory.CreateClient();
            UseToken(client, tenantId, recipient, "ProjectViewer");

            using var list = await client.GetAsync("/api/v1/notifications").ConfigureAwait(true);
            var listDoc = JsonDocument.Parse(await list.Content.ReadAsStringAsync().ConfigureAwait(true));
            Assert.Equal(0, listDoc.RootElement.GetProperty("total").GetInt64());

            using var read = await client.PostAsync($"/api/v1/notifications/{publicId}/read", null).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);
        }
    }

    [SkippableFact]
    public async Task NoSensitivePayload()
    {
        var container = await StartPostgresAsync().ConfigureAwait(true);
        await using (container.ConfigureAwait(true))
        {
            var connectionString = container.GetConnectionString();
            await MigrateAsync(connectionString).ConfigureAwait(true);
            var tenantId = Guid.NewGuid();
            var recipient = Guid.NewGuid();
            await SeedTenantAsync(connectionString, tenantId).ConfigureAwait(true);
            await SeedNotificationsAsync(connectionString, tenantId, recipient, Guid.NewGuid(), 2).ConfigureAwait(true);

            using var factory = CreateFactory(connectionString);
            using var client = factory.CreateClient();
            UseToken(client, tenantId, recipient, "ProjectViewer");

            using var response = await client.GetAsync("/api/v1/notifications").ConfigureAwait(true);
            var payload = await response.Content.ReadAsStringAsync().ConfigureAwait(true);
            Assert.DoesNotContain("http://", payload, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("https://", payload, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("bearer ", payload, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("storageKey", payload, StringComparison.OrdinalIgnoreCase);
            var doc = JsonDocument.Parse(payload);
            foreach (var item in doc.RootElement.GetProperty("items").EnumerateArray())
            {
                Assert.False(string.IsNullOrWhiteSpace(item.GetProperty("resourceType").GetString()));
                Assert.False(string.IsNullOrWhiteSpace(item.GetProperty("resourceId").GetString()));
            }
        }
    }

    [SkippableFact]
    public async Task CrossTenant_404()
    {
        var container = await StartPostgresAsync().ConfigureAwait(true);
        await using (container.ConfigureAwait(true))
        {
            var connectionString = container.GetConnectionString();
            await MigrateAsync(connectionString).ConfigureAwait(true);
            var tenantA = Guid.NewGuid();
            var tenantB = Guid.NewGuid();
            var recipient = Guid.NewGuid();
            await SeedTenantAsync(connectionString, tenantA).ConfigureAwait(true);
            await SeedTenantAsync(connectionString, tenantB).ConfigureAwait(true);
            var ids = await SeedNotificationsAsync(connectionString, tenantA, recipient, Guid.NewGuid(), 1).ConfigureAwait(true);
            var publicId = string.Concat("ntf_", ids[0].ToString("N"));

            using var factory = CreateFactory(connectionString);
            using var clientB = factory.CreateClient();
            UseToken(clientB, tenantB, Guid.NewGuid(), "TenantAdmin");

            using var list = await clientB.GetAsync("/api/v1/notifications").ConfigureAwait(true);
            var listDoc = JsonDocument.Parse(await list.Content.ReadAsStringAsync().ConfigureAwait(true));
            Assert.Equal(0, listDoc.RootElement.GetProperty("total").GetInt64());

            using var read = await clientB.PostAsync($"/api/v1/notifications/{publicId}/read", null).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);
        }
    }

    [SkippableFact]
    public async Task CrossUser_404_And_NullProject_Visible()
    {
        var container = await StartPostgresAsync().ConfigureAwait(true);
        await using (container.ConfigureAwait(true))
        {
            var connectionString = container.GetConnectionString();
            await MigrateAsync(connectionString).ConfigureAwait(true);
            var tenantId = Guid.NewGuid();
            var recipientA = Guid.NewGuid();
            var recipientB = Guid.NewGuid();
            await SeedTenantAsync(connectionString, tenantId).ConfigureAwait(true);
            var ids = await SeedNotificationsAsync(connectionString, tenantId, recipientA, Guid.NewGuid(), 1).ConfigureAwait(true);
            var quotaId = await SeedQuotaAsync(connectionString, tenantId, recipientB).ConfigureAwait(true);
            var publicId = string.Concat("ntf_", ids[0].ToString("N"));

            using var factory = CreateFactory(connectionString);
            using var clientB = factory.CreateClient();
            UseToken(clientB, tenantId, recipientB, "ProjectViewer");

            using var forbidden = await clientB.PostAsync($"/api/v1/notifications/{publicId}/read", null).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.NotFound, forbidden.StatusCode);

            using var list = await clientB.GetAsync("/api/v1/notifications").ConfigureAwait(true);
            var listDoc = JsonDocument.Parse(await list.Content.ReadAsStringAsync().ConfigureAwait(true));
            Assert.Equal(1, listDoc.RootElement.GetProperty("total").GetInt64());
            var item = listDoc.RootElement.GetProperty("items").EnumerateArray().First();
            Assert.Equal(string.Concat("ntf_", quotaId.ToString("N")), item.GetProperty("id").GetString());
            Assert.True(item.GetProperty("projectId").ValueKind == JsonValueKind.Null);
        }
    }

    private static string NewKey()
    {
        return Guid.NewGuid().ToString("N");
    }

    private static WebApplicationFactory<CorrelationIdMiddleware> CreateFactory(string connectionString)
    {
        var factory = new WebApplicationFactory<CorrelationIdMiddleware>();
        return factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) =>
            {
                var values = new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Default"] = connectionString,
                    ["Auth:SigningKey"] = TestSigningKey,
                    ["Auth:Audience"] = TestAudience,
                    ["Auth:RequireHttps"] = "false",
                    ["Transport:Provider"] = "InMemory",
                };
                config.AddInMemoryCollection(values);
            });
        });
    }

    private static void UseToken(HttpClient client, Guid tenantId, Guid userId, params string[] roles)
    {
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", CreateToken(tenantId, userId, roles));
    }

    private static string CreateToken(Guid tenantId, Guid userId, params string[] roles)
    {
        var handler = new JwtSecurityTokenHandler();
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(TestSigningKey));
        var claims = new List<Claim>
        {
            new("tid", tenantId.ToString()),
            new("sub", userId.ToString("D")),
        };
        foreach (var role in roles)
        {
            claims.Add(new Claim("roles", role));
        }

        var token = new JwtSecurityToken(
            audience: TestAudience,
            claims: claims,
            expires: DateTime.UtcNow.AddHours(1),
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));
        return handler.WriteToken(token);
    }

    private static async Task<PostgreSqlContainer> StartPostgresAsync()
    {
        try
        {
            var container = new PostgreSqlBuilder("postgres:16-alpine").Build();
            await container.StartAsync().ConfigureAwait(true);
            return container;
        }
#pragma warning disable CA1031 // Docker availability probe: any failure means skip.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Skip.If(true, $"Docker/PostgreSQL unavailable: {ex.Message}");
            throw new InvalidOperationException("Unreachable: Skip.If always throws.", ex);
        }
    }

    private static DbContextOptions<AppDbContext> CreateOptions(string connectionString)
    {
        return new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention()
            .AddInterceptors(new TenantSessionInterceptor())
            .Options;
    }

    private static async Task MigrateAsync(string connectionString)
    {
        using (TenantContext.BeginMaintenanceScope())
        {
            using var context = new AppDbContext(CreateOptions(connectionString));
            await context.Database.MigrateAsync().ConfigureAwait(true);
        }
    }

    private static async Task SeedTenantAsync(string connectionString, Guid tenantId)
    {
        using (TenantContext.BeginScope(tenantId))
        {
            using var context = new AppDbContext(CreateOptions(connectionString));
            if (!await context.Tenants.AnyAsync(t => t.Id == tenantId).ConfigureAwait(true))
            {
                context.Tenants.Add(new Tenant(tenantId, $"Tenant {tenantId:N}", $"tenant-{tenantId:N}", DateTimeOffset.UtcNow));
                await context.SaveChangesAsync().ConfigureAwait(true);
            }
        }
    }

    private static async Task<List<Guid>> SeedNotificationsAsync(
        string connectionString, Guid tenantId, Guid recipient, Guid projectId, int count)
    {
        var ids = new List<Guid>(count);
        var now = DateTimeOffset.UtcNow;
        using (TenantContext.BeginScope(tenantId))
        {
            using var context = new AppDbContext(CreateOptions(connectionString));
            for (var i = 0; i < count; i++)
            {
                var id = Guid.NewGuid();
                ids.Add(id);
                context.Set<Notification>().Add(new Notification(
                    id, tenantId, recipient, projectId,
                    NotificationType.ProcessingCompleted, NotificationSeverity.Info,
                    $"Run completed {i}", $"Run finished for project {projectId:N}.",
                    "ProcessingRun", Guid.NewGuid().ToString("N"), Guid.NewGuid(),
                    null, now.AddMinutes(i), null));
            }

            await context.SaveChangesAsync().ConfigureAwait(true);
        }

        return ids;
    }

    private static async Task<Guid> SeedQuotaAsync(string connectionString, Guid tenantId, Guid recipient)
    {
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        using (TenantContext.BeginScope(tenantId))
        {
            using var context = new AppDbContext(CreateOptions(connectionString));
            context.Set<Notification>().Add(new Notification(
                id, tenantId, recipient, null,
                NotificationType.QuotaWarning, NotificationSeverity.Warning,
                "Quota warning", "Quota near limit.",
                "Tenant", tenantId.ToString("N"), Guid.NewGuid(),
                null, now, now.AddDays(30)));
            await context.SaveChangesAsync().ConfigureAwait(true);
        }

        return id;
    }

    private static async Task<Guid> SeedExpiredAsync(string connectionString, Guid tenantId, Guid recipient)
    {
        var id = Guid.NewGuid();
        var created = DateTimeOffset.UtcNow.AddDays(-2);
        using (TenantContext.BeginScope(tenantId))
        {
            using var context = new AppDbContext(CreateOptions(connectionString));
            context.Set<Notification>().Add(new Notification(
                id, tenantId, recipient, Guid.NewGuid(),
                NotificationType.ProcessingCompleted, NotificationSeverity.Info,
                "Old run", "Old run finished.",
                "ProcessingRun", Guid.NewGuid().ToString("N"), null,
                null, created, created.AddHours(1)));
            await context.SaveChangesAsync().ConfigureAwait(true);
        }

        return id;
    }

    private static async Task MarkReadDirectAsync(string connectionString, Guid tenantId, Guid notificationId)
    {
        using (TenantContext.BeginScope(tenantId))
        {
            using var context = new AppDbContext(CreateOptions(connectionString));
            var row = await context.Set<Notification>().FirstAsync(n => n.Id == notificationId).ConfigureAwait(true);
            row.MarkAsRead(DateTimeOffset.UtcNow);
            await context.SaveChangesAsync().ConfigureAwait(true);
        }
    }
}
