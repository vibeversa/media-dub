// Task 039C: exports unit gap closure (tenant scoping area).
using DubbingPlatform.Application.Exceptions;
using DubbingPlatform.Application.MultiTenancy;
using DubbingPlatform.Domain.Exceptions;

namespace DubbingPlatform.UnitTests.Security;

/// <summary>
/// Defense-in-depth tenant ownership guard. Every assertion here is a *negative*
/// one: a resource owned by another tenant must be rejected, never returned.
/// </summary>
public sealed class TenantGuardMatrixTests
{
    private static readonly Guid TenantA = Guid.Parse("aaaaaaaa-1111-1111-1111-111111111111");
    private static readonly Guid TenantB = Guid.Parse("bbbbbbbb-2222-2222-2222-222222222222");
    private static readonly Guid TenantC = Guid.Parse("cccccccc-3333-3333-3333-333333333333");

    [Fact]
    public void IsMatch_True_Only_For_The_Owning_Tenant()
    {
        Assert.True(TenantGuard.IsMatch(TenantA, TenantA));
        Assert.True(TenantGuard.IsMatch(TenantB, TenantB));
    }

    [Fact]
    public void IsMatch_Is_False_For_Every_Foreign_Tenant_Pair()
    {
        Assert.False(TenantGuard.IsMatch(TenantA, TenantB));
        Assert.False(TenantGuard.IsMatch(TenantB, TenantA));
        Assert.False(TenantGuard.IsMatch(TenantA, TenantC));
        Assert.False(TenantGuard.IsMatch(TenantC, TenantA));
        Assert.False(TenantGuard.IsMatch(TenantB, TenantC));
    }

    [Fact]
    public void IsMatch_Fails_Closed_On_Empty_Ids()
    {
        Assert.False(TenantGuard.IsMatch(Guid.Empty, TenantA));
        Assert.False(TenantGuard.IsMatch(TenantA, Guid.Empty));
        Assert.False(TenantGuard.IsMatch(Guid.Empty, Guid.Empty));
    }

    [Fact]
    public void IsMatch_Is_Symmetric()
    {
        var ids = new[] { TenantA, TenantB, TenantC, Guid.Empty };
        foreach (var a in ids)
        {
            foreach (var b in ids)
            {
                Assert.Equal(TenantGuard.IsMatch(a, b), TenantGuard.IsMatch(b, a));
            }
        }
    }

    [Fact]
    public void AssertMatch_Is_A_No_Op_For_The_Owner()
    {
        TenantGuard.AssertMatch(TenantA, TenantA);
        TenantGuard.AssertMatch(TenantB, TenantB);
    }

    [Fact]
    public void AssertMatch_Rejects_Cross_Tenant_Access_With_403()
    {
        var ex = Assert.Throws<ForbiddenException>(() => TenantGuard.AssertMatch(TenantB, TenantA));
        Assert.Equal(403, ex.StatusCode);
        Assert.Equal(
            "The requested resource does not belong to the current tenant.",
            ex.Message);
    }

    [Fact]
    public void AssertMatch_Is_Directional_And_Both_Directions_Are_Denied()
    {
        Assert.Throws<ForbiddenException>(() => TenantGuard.AssertMatch(TenantA, TenantB));
        Assert.Throws<ForbiddenException>(() => TenantGuard.AssertMatch(TenantB, TenantA));
    }

    [Fact]
    public void AssertMatch_Never_Leaks_The_Resource_Tenant_Id()
    {
        var ex = Assert.Throws<ForbiddenException>(() => TenantGuard.AssertMatch(TenantB, TenantA));

        Assert.DoesNotContain(TenantA.ToString(), ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(TenantA.ToString("N"), ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(TenantB.ToString(), ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AssertMatch_Fails_Closed_On_Empty_Ids()
    {
        Assert.Throws<ForbiddenException>(() => TenantGuard.AssertMatch(Guid.Empty, TenantA));
        Assert.Throws<ForbiddenException>(() => TenantGuard.AssertMatch(TenantA, Guid.Empty));
        Assert.Throws<ForbiddenException>(() => TenantGuard.AssertMatch(Guid.Empty, Guid.Empty));
    }

    [Fact]
    public void AssertMatch_Rejects_All_Nine_Cross_Tenant_Combinations()
    {
        var tenants = new[] { TenantA, TenantB, TenantC };
        var denials = 0;
        foreach (var caller in tenants)
        {
            foreach (var owner in tenants)
            {
                if (caller == owner)
                {
                    TenantGuard.AssertMatch(caller, owner);
                    continue;
                }

                Assert.Throws<ForbiddenException>(() => TenantGuard.AssertMatch(caller, owner));
                denials++;
            }
        }

        Assert.Equal(6, denials);
    }

    [Fact]
    public void RequireTenant_Accepts_A_Real_Tenant()
    {
        TenantGuard.RequireTenant(TenantA);
    }

    [Fact]
    public void RequireTenant_Rejects_Empty()
    {
        var ex = Assert.Throws<DomainException>(() => TenantGuard.RequireTenant(Guid.Empty));
        Assert.Equal("TenantId must not be empty.", ex.Message);
    }

    [Fact]
    public void Guard_And_IsMatch_Never_Disagree()
    {
        var ids = new[] { TenantA, TenantB, Guid.Empty, Guid.Parse("dddddddd-4444-4444-4444-444444444444") };
        foreach (var caller in ids)
        {
            foreach (var owner in ids)
            {
                var accepted = Record.Exception(() => TenantGuard.AssertMatch(caller, owner)) is null;
                Assert.Equal(TenantGuard.IsMatch(caller, owner), accepted);
            }
        }
    }
}
