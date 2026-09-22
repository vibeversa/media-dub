using DubbingPlatform.Application.Auth;
using DubbingPlatform.Application.MultiTenancy;
using DubbingPlatform.Application.Options;
using DubbingPlatform.Application.Services;
using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Domain.Enums;
using DubbingPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace DubbingPlatform.UnitTests.Auth;

/// <summary>
/// Hermetic (InMemory, no Docker) coverage for session issuance: login pair,
/// bad credentials, rotation, reuse revokes the family, unknown refresh maps
/// to expired, and logout is idempotent. The Docker-backed
/// <c>MePreferencesTests</c> integration suite replays these paths over
/// PostgreSQL plus the HTTP controllers in CI.
/// </summary>
public sealed class AuthSessionTests
{
    private const string SigningKey = "test-signing-key-0123456789abcdef-test-signing-key-01";

    [Fact]
    public async Task Login_Issues_Pair_And_Audits()
    {
        using var factory = CreateFactory();
        var tenantId = Guid.NewGuid();
        var userId = SeedUser(factory, tenantId, "sub-hermetic", TenantUserStatus.Active);
        var service = CreateService(factory);

        var pair = await service.LoginAsync(tenantId, "sub-hermetic", "corr-1");

        Assert.False(string.IsNullOrWhiteSpace(pair.AccessToken));
        Assert.Contains(".", pair.RefreshToken, StringComparison.Ordinal);
        Assert.True(pair.AccessExpiresAt > pair.IssuedAt);
        Assert.True(pair.RefreshExpiresAt > pair.IssuedAt);

        var handler = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler();
        var jwt = handler.ReadJwtToken(pair.AccessToken);
        Assert.Equal(tenantId.ToString("D"), jwt.Claims.First(c => c.Type == "tid").Value);
        Assert.Equal(userId.ToString("D"), jwt.Claims.First(c => c.Type == "sub").Value);

        using (TenantContext.BeginScope(tenantId))
        {
            using var db = new AppDbContext(factory.Options);
            var audit = await db.Set<AuditEvent>().SingleAsync(e => e.Action == AuthService.AuditLoginAction);
            Assert.Contains("corr-1", audit.DetailsJson, StringComparison.Ordinal);
            Assert.DoesNotContain(pair.RefreshToken, audit.DetailsJson ?? string.Empty, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Login_Unknown_Subject_InvalidCredentials_Without_Enumeration()
    {
        using var factory = CreateFactory();
        var service = CreateService(factory);

        var ex = await Assert.ThrowsAsync<InvalidCredentialsException>(
            () => service.LoginAsync(Guid.NewGuid(), "sub-nobody", "corr-1"));
        Assert.Equal("INVALID_CREDENTIALS", ex.ErrorCode);
        Assert.Equal(401, ex.StatusCode);
    }

    [Fact]
    public async Task Login_Disabled_User_Denied()
    {
        using var factory = CreateFactory();
        var tenantId = Guid.NewGuid();
        SeedUser(factory, tenantId, "sub-off", TenantUserStatus.Disabled);
        var service = CreateService(factory);

        var ex = await Assert.ThrowsAsync<UserDisabledException>(
            () => service.LoginAsync(tenantId, "sub-off", "corr-1"));
        Assert.Equal("USER_DISABLED", ex.ErrorCode);
        Assert.Equal(403, ex.StatusCode);
    }

    [Fact]
    public async Task Refresh_Rotates_Reuse_Revokes_Family()
    {
        using var factory = CreateFactory();
        var tenantId = Guid.NewGuid();
        SeedUser(factory, tenantId, "sub-rotate", TenantUserStatus.Active);
        var service = CreateService(factory);
        var first = await service.LoginAsync(tenantId, "sub-rotate", "corr-1");

        var second = await service.RefreshAsync(first.RefreshToken);
        Assert.NotEqual(first.RefreshToken, second.RefreshToken);

        var reuse = await Assert.ThrowsAsync<TokenReusedException>(() => service.RefreshAsync(first.RefreshToken));
        Assert.Equal("TOKEN_REUSED", reuse.ErrorCode);

        var familyDead = await Assert.ThrowsAsync<TokenReusedException>(() => service.RefreshAsync(second.RefreshToken));
        Assert.Equal("TOKEN_REUSED", familyDead.ErrorCode);
    }

    [Fact]
    public async Task Refresh_Unknown_Maps_To_Expired()
    {
        using var factory = CreateFactory();
        var service = CreateService(factory);

        var ex = await Assert.ThrowsAsync<TokenExpiredException>(
            () => service.RefreshAsync(string.Concat(Guid.NewGuid().ToString("N"), ".bogus")));
        Assert.Equal("TOKEN_EXPIRED", ex.ErrorCode);
        Assert.Equal(401, ex.StatusCode);
    }

    [Fact]
    public async Task Logout_Is_Idempotent_And_Audits()
    {
        using var factory = CreateFactory();
        var tenantId = Guid.NewGuid();
        SeedUser(factory, tenantId, "sub-bye", TenantUserStatus.Active);
        var service = CreateService(factory);
        var pair = await service.LoginAsync(tenantId, "sub-bye", "corr-1");

        await service.LogoutAsync(pair.RefreshToken, "corr-2");
        await service.LogoutAsync(pair.RefreshToken, "corr-2");
        await service.LogoutAsync("bogus-token", "corr-2");
        await service.LogoutAsync(null, "corr-2");

        using (TenantContext.BeginScope(tenantId))
        {
            using var db = new AppDbContext(factory.Options);
            Assert.True(await db.Set<AuditEvent>().AnyAsync(e => e.Action == AuthService.AuditLogoutAction));
        }
    }

    [Fact]
    public void Refresh_Secret_Hash_Verify_RoundTrips()
    {
        var hash = AuthService.HashSecret("secret-value", "salt-value");
        Assert.True(AuthService.VerifySecret("secret-value", "salt-value", hash));
        Assert.False(AuthService.VerifySecret("other", "salt-value", hash));
        Assert.False(AuthService.VerifySecret("secret-value", "salt-value", "deadbeef"));
    }

    private sealed class TestFactory : IStageExecutionContextFactory, IDisposable
    {
        public TestFactory(DbContextOptions<AppDbContext> options)
        {
            Options = options;
        }

        public DbContextOptions<AppDbContext> Options { get; }

        public DbContext CreateDbContext() => new AppDbContext(Options);

        public void Dispose()
        {
        }
    }

    private static TestFactory CreateFactory()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase("AuthSessionTests-" + Guid.NewGuid().ToString("N"))
            .ReplaceService<IModelCacheKeyFactory, TenantModelCacheKeyFactory>()
            .Options;
        return new TestFactory(options);
    }

    private static AuthService CreateService(TestFactory factory)
    {
        var authOptions = MsOptions.Create(new AuthOptions { Audience = "dubbing-api", SigningKey = SigningKey });
        var audit = new AuditService(factory);
        return new AuthService(factory, authOptions, audit, NullLogger<AuthService>.Instance);
    }

    private static Guid SeedUser(TestFactory factory, Guid tenantId, string subject, TenantUserStatus status)
    {
        var userId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = new AppDbContext(factory.Options);
            db.Set<TenantUser>().Add(new TenantUser(
                userId, tenantId, subject, $"{subject}@example.com", subject,
                status, now, now));
            db.SaveChanges();
        }

        return userId;
    }
}
