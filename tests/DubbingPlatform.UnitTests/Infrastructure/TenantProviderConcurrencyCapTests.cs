// GAP-016: distinct per-tenant concurrent-provider-call fairness cap. The
// existing Redis `concurrency` dimension is per (tenant, provider); the tenant
// cap must span every provider so one tenant cannot consume the whole shared
// provider budget while other tenants wait.
using DubbingPlatform.Application.Options;
using DubbingPlatform.Infrastructure.Redis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DubbingPlatform.UnitTests.Infrastructure;

public sealed class TenantProviderConcurrencyCapTests
{
    private static RateLimiter CreateLimiter(int providerConcurrency, int tenantConcurrency) =>
        new(
            redis: null,
            Microsoft.Extensions.Options.Options.Create(new RateLimitOptions
            {
                Concurrency = providerConcurrency,
                TenantConcurrency = tenantConcurrency,
            }),
            NullLogger<RateLimiter>.Instance);

    [Fact]
    public void Tenant_Concurrency_Dimension_Is_Normalized_And_Budgeted()
    {
        Assert.Equal("tenantConcurrency", RateLimiter.NormalizeDimension("tenantConcurrency"));
        Assert.Equal("tenantConcurrency", RateLimiter.NormalizeDimension("TenantConcurrency"));
        Assert.Equal("tenantConcurrency", RateLimiter.NormalizeDimension(" tenant-concurrency "));

        var limits = new RateLimitOptions { Concurrency = 10, TenantConcurrency = 50 };
        Assert.Equal(50, RateLimiter.LimitForDimension(limits, "tenantConcurrency"));
        Assert.Equal(10, RateLimiter.LimitForDimension(limits, "concurrency"));
    }

    [Fact]
    public void Tenant_Cap_And_Provider_Cap_Are_Distinct_Keys()
    {
        var tenantId = Guid.NewGuid();
        var tenantKey = RateLimiter.KeyFor(tenantId, TenantFairnessGate.TenantConcurrencySegment, "tenantConcurrency");
        var providerKey = RateLimiter.KeyFor(tenantId, "azure", "concurrency");

        Assert.NotEqual(tenantKey, providerKey);
        Assert.Contains("tenantConcurrency", tenantKey, StringComparison.Ordinal);
        Assert.Contains("azure", providerKey, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Provider_Calls_Are_Capped_Per_Tenant_Across_All_Providers()
    {
        var limiter = CreateLimiter(providerConcurrency: 100, tenantConcurrency: 2);
        var gate = new TenantFairnessGate(
            redis: null,
            Microsoft.Extensions.Options.Options.Create(new QuotaOptions()),
            Microsoft.Extensions.Options.Options.Create(new RateLimitOptions { Concurrency = 100, TenantConcurrency = 2 }),
            scopes: new Microsoft.Extensions.DependencyInjection.ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            limiter,
            NullLogger<TenantFairnessGate>.Instance);

        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        // Tenant A may run two provider calls in the window, spanning providers.
        Assert.True(await gate.TryAcquireProviderCallAsync(tenantA, "azure"));
        Assert.True(await gate.TryAcquireProviderCallAsync(tenantA, "openai"));

        // Third call is over the tenant cap, even though each provider is under
        // its own per-provider window.
        Assert.False(await gate.TryAcquireProviderCallAsync(tenantA, "azure"));

        // A second tenant is unaffected: fairness, not a global throttle.
        Assert.True(await gate.TryAcquireProviderCallAsync(tenantB, "azure"));
        Assert.True(await gate.TryAcquireProviderCallAsync(tenantB, "google"));

        Assert.Equal(
            "tenantConcurrency",
            RateLimiter.NormalizeDimension("tenantConcurrency"));
    }

    [Fact]
    public async Task Per_Provider_Window_Still_Applies_Under_The_Tenant_Cap()
    {
        var limiter = CreateLimiter(providerConcurrency: 1, tenantConcurrency: 10);
        var gate = new TenantFairnessGate(
            redis: null,
            Microsoft.Extensions.Options.Options.Create(new QuotaOptions()),
            Microsoft.Extensions.Options.Options.Create(new RateLimitOptions { Concurrency = 1, TenantConcurrency = 10 }),
            scopes: new Microsoft.Extensions.DependencyInjection.ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            limiter,
            NullLogger<TenantFairnessGate>.Instance);

        var tenantId = Guid.NewGuid();
        Assert.True(await gate.TryAcquireProviderCallAsync(tenantId, "azure"));

        // Per-provider window exhausted; a different provider is still allowed
        // because the tenant cap has headroom.
        Assert.False(await gate.TryAcquireProviderCallAsync(tenantId, "azure"));
        Assert.True(await gate.TryAcquireProviderCallAsync(tenantId, "openai"));
    }

    [Fact]
    public void Defaults_Are_Ordered_And_Validated()
    {
        var options = new RateLimitOptions();
        Assert.True(options.TenantConcurrency >= options.Concurrency);

        var ok = new RateLimitOptionsValidator().Validate(null, new RateLimitOptions { TenantConcurrency = 25 });
        Assert.True(ok.Succeeded);

        var tooSmall = new RateLimitOptionsValidator().Validate(null, new RateLimitOptions { TenantConcurrency = 0 });
        Assert.True(tooSmall.Failed);
        Assert.Contains("TenantConcurrency", tooSmall.FailureMessage!, StringComparison.Ordinal);

        var tooLarge = new RateLimitOptionsValidator().Validate(null, new RateLimitOptions { TenantConcurrency = 10001 });
        Assert.True(tooLarge.Failed);
    }
}