using DubbingPlatform.Application.Abstractions.Providers;
using DubbingPlatform.Application.Diagnostics.Dto;
using DubbingPlatform.Application.MultiTenancy;
using DubbingPlatform.Application.Options;
using DubbingPlatform.Application.Services;
using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DubbingPlatform.Application.Diagnostics;

/// <summary>
/// Read-only provider health aggregation over router health probes
/// (<see cref="IProviderHealthTracker"/>) plus tenant-scoped provider
/// execution history and route configuration. A provider that was never
/// probed reads as <c>Unknown</c>, never <c>Down</c>. Secret-free: route
/// names and aggregate statistics only — API keys (<c>Providers:*</c>) are
/// never read. All queries are <c>AsNoTracking</c>; no writes.
/// </summary>
public sealed class ProviderHealthQueryService
{
    /// <summary>Provider was never probed and has no samples.</summary>
    public const string StatusUnknown = "Unknown";

    /// <summary>Provider is serving within error budgets.</summary>
    public const string StatusHealthy = "Healthy";

    /// <summary>Provider is serving but elevated errors or rate limiting apply.</summary>
    public const string StatusDegraded = "Degraded";

    /// <summary>Provider circuit is open.</summary>
    public const string StatusDown = "Down";

    /// <summary>Circuit-breaker open state.</summary>
    public const string CircuitOpen = "Open";

    /// <summary>Circuit-breaker closed state.</summary>
    public const string CircuitClosed = "Closed";

    private readonly IStageExecutionContextFactory _contextFactory;
    private readonly IProviderHealthTracker _healthTracker;
    private readonly ProviderOptions _providers;
    private readonly DiagnosticsOptions _diagnostics;
    private readonly IDiagnosticsAccessChecker _access;
    private readonly ILogger<ProviderHealthQueryService> _logger;

    public ProviderHealthQueryService(
        IStageExecutionContextFactory contextFactory,
        IProviderHealthTracker healthTracker,
        IOptions<ProviderOptions> providerOptions,
        IOptions<DiagnosticsOptions> diagnosticsOptions,
        IDiagnosticsAccessChecker access,
        ILogger<ProviderHealthQueryService> logger)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        ArgumentNullException.ThrowIfNull(healthTracker);
        ArgumentNullException.ThrowIfNull(providerOptions);
        ArgumentNullException.ThrowIfNull(diagnosticsOptions);
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(logger);
        _contextFactory = contextFactory;
        _healthTracker = healthTracker;
        _providers = providerOptions.Value;
        _diagnostics = diagnosticsOptions.Value;
        _access = access;
        _logger = logger;
    }

    /// <summary>
    /// Resolves the aggregate status for one provider. Pure. A provider with
    /// no execution samples and no probe calls is <c>Unknown</c> (never
    /// <c>Down</c>); an open circuit is <c>Down</c>; an unhealthy-but-closed
    /// tracker (error budget exceeded or rate limited) is <c>Degraded</c>.
    /// </summary>
    public static string ResolveStatus(bool hasSamples, ProviderHealthState trackerState)
    {
        ArgumentNullException.ThrowIfNull(trackerState);

        if (!hasSamples && trackerState.TotalCalls == 0)
        {
            return StatusUnknown;
        }

        if (trackerState.CircuitOpen)
        {
            return StatusDown;
        }

        if (!trackerState.IsHealthy)
        {
            return StatusDegraded;
        }

        return StatusHealthy;
    }

    /// <summary>
    /// Computes the p95 of latency samples (nearest-rank). Pure. Returns null
    /// for an empty sample.
    /// </summary>
    public static double? ComputeP95(IReadOnlyList<long> latencyMs)
    {
        ArgumentNullException.ThrowIfNull(latencyMs);

        if (latencyMs.Count == 0)
        {
            return null;
        }

        var sorted = latencyMs.OrderBy(v => v).ToArray();
        var rank = (int)Math.Ceiling(0.95 * sorted.Length);
        return (double)sorted[Math.Clamp(rank, 1, sorted.Length) - 1];
    }

    /// <summary>
    /// Returns one secret-free health snapshot per provider type.
    /// </summary>
    public async Task<IReadOnlyList<ProviderHealthDto>> GetProviderHealthAsync(
        Guid tenantId,
        Guid userId,
        string? correlationId = null,
        CancellationToken cancellationToken = default)
    {
        await _access.RequireDiagnosticsViewerAsync(tenantId, userId, cancellationToken).ConfigureAwait(false);
        var correlation = DiagnosticsCorrelation.Normalize(correlationId);

        var providers = Enum.GetValues<ProviderType>();
        var result = new List<ProviderHealthDto>(providers.Length);
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            foreach (var provider in providers)
            {
                var executions = await db.Set<ProviderExecution>()
                    .AsNoTracking()
                    .Where(e => e.Provider == provider)
                    .OrderByDescending(e => e.CreatedAt)
                    .Take(_diagnostics.MaxProviderExecutionSample)
                    .Select(e => new { e.LatencyMs, e.Outcome, e.CreatedAt })
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false);

                var state = _healthTracker.GetState(provider);
                var latencies = executions.Select(e => e.LatencyMs).ToList();
                var errorCount = executions.Count(e => e.Outcome != OutcomeClass.Success);
                var successes = executions.Where(e => e.Outcome == OutcomeClass.Success).ToList();

                result.Add(new ProviderHealthDto(
                    correlation,
                    provider.ToString(),
                    ResolveStatus(executions.Count > 0, state),
                    ComputeP95(latencies),
                    executions.Count == 0 ? 0.0 : (double)errorCount / executions.Count,
                    successes.Count == 0 ? null : successes.Max(e => (DateTimeOffset?)e.CreatedAt),
                    ActiveRoutesFor(provider),
                    state.CircuitOpen ? CircuitOpen : CircuitClosed));
            }
        }

        _logger.LogInformation(
            "Diagnostics provider health queried. {CorrelationId} {TenantId} {ProviderCount}",
            correlation,
            tenantId,
            result.Count);
        return result;
    }

    /// <summary>
    /// Returns the configured provider routes (capability to provider with
    /// priority and enablement). Reads route configuration only — never keys.
    /// </summary>
    public async Task<IReadOnlyList<ProviderRouteDto>> GetProviderRoutesAsync(
        Guid tenantId,
        Guid userId,
        string? correlationId = null,
        CancellationToken cancellationToken = default)
    {
        await _access.RequireDiagnosticsViewerAsync(tenantId, userId, cancellationToken).ConfigureAwait(false);
        var correlation = DiagnosticsCorrelation.Normalize(correlationId);

        var routes = BuildRoutes(_providers, correlation);

        _logger.LogInformation(
            "Diagnostics provider routes queried. {CorrelationId} {TenantId} {RouteCount}",
            correlation,
            tenantId,
            routes.Count);
        return routes;
    }

    /// <summary>
    /// Builds route DTOs from provider options. Pure. Reads
    /// <c>RoutePriority</c>/<c>Enabled</c> only — never API keys.
    /// </summary>
    public static IReadOnlyList<ProviderRouteDto> BuildRoutes(ProviderOptions providers, string correlationId)
    {
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);

        var routes = new List<ProviderRouteDto>();
        var routePriority = providers.RoutePriority ?? new Dictionary<string, string[]>(StringComparer.Ordinal);
        var enabled = providers.Enabled ?? new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var pair in routePriority.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (pair.Value is null)
            {
                continue;
            }

            for (var index = 0; index < pair.Value.Length; index++)
            {
                var providerName = pair.Value[index]?.Trim();
                if (string.IsNullOrWhiteSpace(providerName))
                {
                    continue;
                }

                var isEnabled = true;
                foreach (var flag in enabled)
                {
                    if (string.Equals(flag.Key?.Trim(), providerName, StringComparison.OrdinalIgnoreCase))
                    {
                        isEnabled = flag.Value;
                        break;
                    }
                }

                routes.Add(new ProviderRouteDto(correlationId, pair.Key, providerName, index, isEnabled));
            }
        }

        return routes;
    }

    private IReadOnlyList<string> ActiveRoutesFor(ProviderType provider)
    {
        var routes = new List<string>();
        var routePriority = _providers.RoutePriority ?? new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var pair in routePriority.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (pair.Value is null)
            {
                continue;
            }

            foreach (var entry in pair.Value)
            {
                if (ProviderOptionNames.TryParseProvider(entry, out var parsed) && parsed == provider)
                {
                    routes.Add(pair.Key);
                    break;
                }
            }
        }

        return routes;
    }
}
