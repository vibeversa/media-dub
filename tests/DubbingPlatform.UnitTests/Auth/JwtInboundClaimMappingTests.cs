using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using DubbingPlatform.Application.Auth;
using DubbingPlatform.Application.Authorization;
using DubbingPlatform.Application.MultiTenancy;
using DubbingPlatform.Application.Options;
using DubbingPlatform.Application.Services;
using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Domain.Enums;
using DubbingPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.Tokens;
using MsOptions = Microsoft.Extensions.Options.Options;
using PlatformClaimTypes = DubbingPlatform.Application.Authorization.ClaimTypes;

namespace DubbingPlatform.UnitTests.Auth;

/// <summary>
/// Regression guard for the inbound claim mapping that made real HTTP auth fail
/// end to end (fixed in Task 040A; see the comment on
/// <c>JwtBearerOptions.MapInboundClaims</c> in
/// <c>src/DubbingPlatform.Api/Program.cs</c>).
///
/// <para>
/// <c>AuthSessionTests</c> decodes the access token with
/// <c>JwtSecurityTokenHandler.ReadJwtToken</c>, which applies neither claim
/// mapping nor validation. That is how it could assert <c>tid</c> and
/// <c>sub</c> while every real request returned 401 <c>TENANT_REQUIRED</c>:
/// the <c>JwtBearer</c> handler validated the very same token with
/// <c>MapInboundClaims</c> left at its default <c>true</c>, which rewrote the
/// short platform names to long URIs, so <c>ClaimsPrincipalExtensions</c> -
/// which looks claims up by short name - found nothing.
/// </para>
/// <para>
/// These tests validate rather than decode, and assert the mapping behaviour in
/// both directions so nobody re-enables mapping without seeing why it breaks.
/// </para>
/// </summary>
public sealed class JwtInboundClaimMappingTests
{
    private const string SigningKey = "test-signing-key-0123456789abcdef-test-signing-key-01";
    private const string Audience = "dubbing-api";

    [Fact]
    public async Task MintedToken_Is_Readable_By_Platform_Readers_When_Mapping_Is_Disabled()
    {
        var (token, tenantId, userId) = await IssueTokenAsync();

        var principal = Validate(token, mapInboundClaims: false);

        Assert.True(principal.TryGetTenantId(out var resolved));
        Assert.Equal(tenantId, resolved);
        Assert.Equal(userId.ToString("D"), principal.GetSubject());
        Assert.Contains(principal.GetRoles(), role => role == nameof(ProjectRole.ProjectOwner));
    }

    /// <summary>
    /// Pins the runtime behaviour that caused the defect. The assertion that
    /// actually matters is the test above; this one exists so that a runtime
    /// upgrade which stops rewriting <c>tid</c> surfaces as a deliberate review
    /// of the comment in <c>Program.cs</c> rather than as a silent behaviour
    /// change.
    /// </summary>
    [Fact]
    public void Default_Inbound_Map_Rewrites_Short_Names_To_Long_Uris_And_Loses_The_Tenant_Claim()
    {
        Assert.Equal(
            "http://schemas.microsoft.com/identity/claims/tenantid",
            JwtSecurityTokenHandler.DefaultInboundClaimTypeMap[PlatformClaimTypes.TenantId]);

        var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
        var validated = handler.ValidateToken(UnsignedToken(), Parameters(), out _);

        // Reproduce the pre-fix behaviour by applying the default map, which is
        // what `JwtBearer` did while `MapInboundClaims` was unset.
        var map = JwtSecurityTokenHandler.DefaultInboundClaimTypeMap;
        var mappedClaims = validated.Claims
            .Select(claim => new Claim(
                map.TryGetValue(claim.Type, out var longName) ? longName : claim.Type,
                claim.Value));
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            mappedClaims, authenticationType: "Bearer", nameType: "sub", roleType: "roles"));

        // The tenant claim is in the token but invisible to the platform readers.
        Assert.Contains(validated.Claims, claim => claim.Type == PlatformClaimTypes.TenantId);
        Assert.DoesNotContain(principal.Claims, claim => claim.Type == PlatformClaimTypes.TenantId);
        Assert.False(principal.TryGetTenantId(out _));
    }

    private static async Task<(string Token, Guid TenantId, Guid UserId)> IssueTokenAsync()
    {
        using var factory = CreateFactory();
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        using (TenantContext.BeginScope(tenantId))
        {
            using var db = new AppDbContext(factory.Options);
            db.Set<TenantUser>().Add(new TenantUser(
                userId, tenantId, "sub-mapping", "sub-mapping@example.com", "Sub Mapping",
                TenantUserStatus.Active, now, now));
            db.Set<DubbingProject>().Add(new DubbingProject(
                projectId, tenantId, "en", "es", ProjectStatus.Created, "{}", new string('a', 64),
                null, null, now, now));
            // The membership is what puts `roles` in the token, and `roles` was
            // rewritten to the long role URI by the same mapping that lost `tid`,
            // so the role path needs coverage too.
            db.Set<ProjectMembership>().Add(new ProjectMembership(
                Guid.NewGuid(), tenantId, projectId, userId,
                ProjectRole.ProjectOwner, userId, now));
            db.SaveChanges();
        }

        var authOptions = MsOptions.Create(new AuthOptions { Audience = Audience, SigningKey = SigningKey });
        var service = new AuthService(
            factory, authOptions, new AuditService(factory), NullLogger<AuthService>.Instance);
        var pair = await service.LoginAsync(tenantId, "sub-mapping", "corr-mapping");
        return (pair.AccessToken, tenantId, userId);
    }

    private static ClaimsPrincipal Validate(string token, bool mapInboundClaims)
    {
        var handler = new JwtSecurityTokenHandler { MapInboundClaims = mapInboundClaims };
        return handler.ValidateToken(token, Parameters(), out _);
    }

    /// <summary>
    /// Mirrors the validation parameters configured in
    /// <c>src/DubbingPlatform.Api/Program.cs</c>. Keep the two in step: this test
    /// only means anything if it validates the way the API does.
    /// </summary>
    private static TokenValidationParameters Parameters() => new()
    {
        ValidateIssuer = false,
        ValidateAudience = true,
        ValidAudience = Audience,
        ValidateLifetime = true,
        ClockSkew = TimeSpan.FromSeconds(30),
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SigningKey)),
        NameClaimType = "sub",
        RoleClaimType = "roles",
    };

    private static string UnsignedToken()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var descriptor = new JwtSecurityToken(
            issuer: null,
            audience: Audience,
            claims:
            [
                new Claim(PlatformClaimTypes.TenantId, Guid.NewGuid().ToString("D")),
                new Claim(PlatformClaimTypes.Subject, Guid.NewGuid().ToString("D")),
                new Claim(PlatformClaimTypes.Roles, nameof(ProjectRole.ProjectOwner)),
            ],
            notBefore: DateTime.UtcNow,
            expires: DateTime.UtcNow.AddMinutes(15),
            signingCredentials: new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SigningKey)),
                SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(descriptor);
    }

    private static TestFactory CreateFactory()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase("JwtInboundClaimMappingTests-" + Guid.NewGuid().ToString("N"))
            .ReplaceService<IModelCacheKeyFactory, TenantModelCacheKeyFactory>()
            .Options;
        return new TestFactory(options);
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
}
