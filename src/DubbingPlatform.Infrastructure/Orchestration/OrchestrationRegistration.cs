using DubbingPlatform.Application.Options;
using DubbingPlatform.Application.Services;
using DubbingPlatform.Infrastructure.Redis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace DubbingPlatform.Infrastructure.Orchestration;

/// <summary>
/// Orchestration service registration (saga workers only). The API host runs the
/// bus for publishing but hosts no saga or work consumers; the worker role owns
/// progression. Task 036: <c>ICostGate</c>/<c>IRateGate</c> resolve to the real
/// PG-backed cost gate and Redis rate gate (contracts frozen; the
/// <c>AllowAll*</c> stubs remain for explicit test harnesses only).
/// <see cref="RateLimiter"/> is Redis-backed with in-memory fallback when no
/// multiplexer is wired (fast/InMemory profile); <see cref="TenantFairnessGate"/>
/// enforces dispatch fairness before publish.
/// </summary>
public static class OrchestrationRegistration
{
    /// <summary>
    /// Registers barrier, dispatcher, real gates, rate limiter, fairness, and
    /// the deferred sender. The saga state machine and consumers are
    /// registered on the bus via
    /// <c>MassTransitConfig.AddDubbingMassTransit(services, configuration,
    /// includeOrchestration: true)</c>.
    /// </summary>
    public static void AddDubbingOrchestration(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<BarrierService>();
        services.AddScoped<WorkDispatcher>();
        services.AddSingleton<RateLimiter>(provider => new RateLimiter(
            provider.GetService<IConnectionMultiplexer>(),
            provider.GetRequiredService<IOptions<RateLimitOptions>>(),
            provider.GetRequiredService<ILogger<RateLimiter>>()));
        services.AddSingleton<TenantFairnessGate>(provider => new TenantFairnessGate(
            provider.GetService<IConnectionMultiplexer>(),
            provider.GetRequiredService<IOptions<QuotaOptions>>(),
            provider.GetRequiredService<IOptions<RateLimitOptions>>(),
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<RateLimiter>(),
            provider.GetRequiredService<ILogger<TenantFairnessGate>>()));
        services.AddSingleton<IRateGate, RateGate>();
        services.AddSingleton<ICostGate, CostGate>();
        services.AddScoped<IDeferredSender, MassTransitDeferredSender>();
    }
}
