using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using DubbingPlatform.Application.MultiTenancy;
using DubbingPlatform.Application.Options;
using DubbingPlatform.Application.Services;
using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Domain.Enums;
using DubbingPlatform.Domain.Exceptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace DubbingPlatform.Infrastructure.Redis;

/// <summary>
/// Rate-limit meters. Counter names are frozen: renaming breaks dashboards
/// (Task 38). Tags <c>provider</c> + <c>dimension</c> slice rejections.
/// </summary>
public static class RateLimitMeters
{
    public const string MeterName = "DubbingPlatform.RateLimit";

    public const string RejectionsMetricName = "ratelimit.rejections";

    private static readonly Meter Meter = new(MeterName);

    public static readonly Counter<long> Rejections =
        Meter.CreateCounter<long>(RejectionsMetricName);
}

/// <summary>
/// Redis fixed-window rate limiter with in-memory fallback (R3: Redis only for
/// rate/fairness; PG never consulted here). One key per
/// <c>ratelimit:{tenant:N}:{provider}:{dimension}</c>, 60s windows enforced by
/// a Lua <c>INCRBY + EXPIRE + compare</c> script so check-and-increment is
/// atomic across replicas. Dimensions map to <c>RateLimit</c> budgets:
/// <c>requests→RequestsPerMin, tokens→TokensPerMin, chars→CharsPerMin,
/// audioSecs→AudioSecondsPerMin, concurrency→Concurrency</c>.
/// Redis outage is fail-open (allow + warning log) so a cache outage never
/// blocks the pipeline; quota/cost stay fail-closed in PG services —
/// documented per the task edge decision. Only tenant/provider/dimension and
/// amounts are logged — never content or secrets.
/// </summary>
public sealed class RateLimiter
{
    public const string KeyPrefix = "ratelimit:";

    public const string LuaScript =
        "local current = redis.call('INCRBY', KEYS[1], ARGV[1]) " +
        "if current == tonumber(ARGV[1]) then redis.call('EXPIRE', KEYS[1], 60) end " +
        "if current > tonumber(ARGV[2]) then return 0 else return 1 end";

    private readonly IConnectionMultiplexer? _redis;
    private readonly RateLimitOptions _limits;
    private readonly ILogger<RateLimiter> _logger;
    private readonly ConcurrentDictionary<string, InMemoryWindow> _memory = new(StringComparer.Ordinal);

    public RateLimiter(
        IConnectionMultiplexer? redis,
        IOptions<RateLimitOptions> limits,
        ILogger<RateLimiter> logger)
    {
        _redis = redis;
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(logger);
        _limits = limits.Value;
        _logger = logger;
    }

    /// <summary>
    /// Normalizes a dimension name (<c>requests|tokens|chars|audiosecs|
    /// concurrency</c>, case-insensitive). Pure.
    /// </summary>
    public static string NormalizeDimension(string? dimension)
    {
        var normalized = (dimension ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "requests" or "request" or "req" => "requests",
            "tokens" or "token" or "tok" => "tokens",
            "chars" or "char" or "characters" => "chars",
            "audiosecs" or "audioseconds" or "audiosec" or "audio" or "seconds" => "audioSecs",
            "concurrency" or "concurrent" or "inflight" => "concurrency",
            _ => throw new DomainException($"Unknown rate dimension '{dimension}'."),
        };
    }

    /// <summary>
    /// Budget for a normalized dimension. Pure.
    /// </summary>
    public static long LimitForDimension(RateLimitOptions limits, string normalizedDimension)
    {
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentException.ThrowIfNullOrWhiteSpace(normalizedDimension);
        return normalizedDimension switch
        {
            "requests" => limits.RequestsPerMin,
            "tokens" => limits.TokensPerMin,
            "chars" => limits.CharsPerMin,
            "audioSecs" => limits.AudioSecondsPerMin,
            "concurrency" => limits.Concurrency,
            _ => throw new DomainException($"Unknown rate dimension '{normalizedDimension}'."),
        };
    }

    /// <summary>
    /// Builds the Redis key. Pure. Tenant-scoped (no cross-tenant sharing).
    /// </summary>
    public static string KeyFor(Guid tenantId, string provider, string normalizedDimension)
    {
        if (tenantId == Guid.Empty)
        {
            throw new DomainException("TenantId must not be empty.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(normalizedDimension);
        return string.Concat(
            KeyPrefix,
            tenantId.ToString("N"),
            ":",
            provider.Trim().ToLowerInvariant(),
            ":",
            normalizedDimension.Trim());
    }

    /// <summary>
    /// Pure window check: <c>used + amount &lt;= limit</c>.
    /// </summary>
    public static bool IsAllowed(long usedInWindow, long limit, long amount)
    {
        if (usedInWindow < 0 || limit < 0 || amount <= 0)
        {
            throw new DomainException("Rate window values must be non-negative and amount positive.");
        }

        return checked(usedInWindow + amount) <= limit;
    }

    /// <summary>
    /// Attempts to consume <paramref name="amount"/> of
    /// (<paramref name="provider"/>, <paramref name="dimension"/>). Returns
    /// false when the 60s budget is exhausted (caller throws
    /// <c>RateLimitedException</c> with Retry-After 60s and the rejection
    /// metric is recorded). Fail-open (true + warning) on Redis outage or
    /// when no multiplexer is wired (fast/InMemory profile, hermetic tests).
    /// </summary>
    public async Task<bool> TryAcquireAsync(
        Guid tenantId,
        string provider,
        string dimension,
        long amount,
        CancellationToken cancellationToken = default)
    {
        if (tenantId == Guid.Empty)
        {
            throw new DomainException("TenantId must not be empty.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        if (amount <= 0)
        {
            throw new DomainException("Amount must be positive.");
        }

        var normalized = NormalizeDimension(dimension);
        var limit = LimitForDimension(_limits, normalized);
        var key = KeyFor(tenantId, provider, normalized);
        var providerName = provider.Trim().ToLowerInvariant();

        var redis = _redis;
        if (redis is null)
        {
            return TryAcquireInMemory(key, providerName, normalized, limit, amount);
        }

        try
        {
            var database = redis.GetDatabase();
            var result = await database.ScriptEvaluateAsync(
                LuaScript,
                [new RedisKey(key)],
                [new RedisValue(amount.ToString(System.Globalization.CultureInfo.InvariantCulture)), new RedisValue(limit.ToString(System.Globalization.CultureInfo.InvariantCulture))]).ConfigureAwait(false);
            var allowed = result.ToString() == "1";
            if (!allowed)
            {
                RateLimitMeters.Rejections.Add(
                    1,
                    new KeyValuePair<string, object?>("provider", providerName),
                    new KeyValuePair<string, object?>("dimension", normalized));
                _logger.LogWarning(
                    "Rate limit hit for tenant {TenantId} provider {Provider} dimension {Dimension}.",
                    tenantId, providerName, normalized);
            }

            return allowed;
        }
#pragma warning disable CA1031 // Fail-open: Redis outage must never block the pipeline; quota/cost stay fail-closed in PG.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            _logger.LogWarning(
                ex,
                "Redis unavailable for rate limiting tenant {TenantId} provider {Provider}; allowing (fail-open).",
                tenantId, providerName);
            return true;
        }
    }

    private bool TryAcquireInMemory(
        string key,
        string provider,
        string normalized,
        long limit,
        long amount)
    {
        var now = DateTimeOffset.UtcNow;
        var window = _memory.GetOrAdd(key, _ => new InMemoryWindow());
        lock (window.Sync)
        {
            if (now - window.WindowStart >= TimeSpan.FromMinutes(1))
            {
                window.WindowStart = now;
                window.Used = 0;
            }

            if (!IsAllowed(window.Used, limit, amount))
            {
                RateLimitMeters.Rejections.Add(
                    1,
                    new KeyValuePair<string, object?>("provider", provider),
                    new KeyValuePair<string, object?>("dimension", normalized));
                return false;
            }

            window.Used = checked(window.Used + amount);
            return true;
        }
    }

    private sealed class InMemoryWindow
    {
        public readonly object Sync = new();

        public DateTimeOffset WindowStart = DateTimeOffset.UtcNow;

        public long Used;
    }
}

/// <summary>
/// Tenant fairness gate (same Redis as rate limiting; R3). Two caps:
/// <c>maxActiveSegmentStages = Quota:MaxConcurrentStagesPerTenant</c> (PG
/// execution counts — durable, correct across restarts) and
/// <c>maxConcurrentProviderCalls = RateLimit:Concurrency</c> (Redis admissions
/// per minute — ephemeral, never blocks on outage). The dispatcher calls
/// <see cref="TryAcquireDispatchAsync"/> before publish; workers call
/// <see cref="TryAcquireProviderCallAsync"/> before provider calls. Denials
/// surface as the frozen <c>concurrency-exhausted</c> / <c>rate-limited</c>
/// dispatcher reasons and 429 RATE_LIMITED at the API boundary.
/// </summary>
public sealed class TenantFairnessGate
{
    private readonly IConnectionMultiplexer? _redis;
    private readonly QuotaOptions _quota;
    private readonly RateLimitOptions _rates;
    private readonly IServiceScopeFactory _scopes;
    private readonly RateLimiter _limiter;
    private readonly ILogger<TenantFairnessGate> _logger;

    public TenantFairnessGate(
        IConnectionMultiplexer? redis,
        IOptions<QuotaOptions> quotaOptions,
        IOptions<RateLimitOptions> rateOptions,
        IServiceScopeFactory scopes,
        RateLimiter limiter,
        ILogger<TenantFairnessGate> logger)
    {
        _redis = redis;
        ArgumentNullException.ThrowIfNull(quotaOptions);
        ArgumentNullException.ThrowIfNull(rateOptions);
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(limiter);
        ArgumentNullException.ThrowIfNull(logger);
        _quota = quotaOptions.Value;
        _rates = rateOptions.Value;
        _scopes = scopes;
        _limiter = limiter;
        _logger = logger;
        _ = _redis;
        _ = _rates;
    }

    /// <summary>
    /// Whether the tenant may dispatch <paramref name="units"/> more segment
    /// stages (PG active executions + units vs
    /// <c>MaxConcurrentStagesPerTenant</c>). Pure threshold via
    /// <see cref="IsDispatchAllowed"/>.
    /// </summary>
    public async Task<bool> TryAcquireDispatchAsync(
        Guid tenantId,
        int units,
        CancellationToken cancellationToken = default)
    {
        if (tenantId == Guid.Empty)
        {
            throw new DomainException("TenantId must not be empty.");
        }

        if (units < 0)
        {
            throw new DomainException("Units must be >= 0.");
        }

        using var scope = _scopes.CreateScope();
        var factories = scope.ServiceProvider.GetService(typeof(IStageExecutionContextFactory)) as IStageExecutionContextFactory;
        if (factories is null)
        {
            return true;
        }

        try
        {
            using (TenantContext.BeginScope(tenantId))
            {
                using var db = factories.CreateDbContext();
                var active = await db.Set<StageExecution>()
                    .Where(e => e.Status == StageStatus.Scheduled
                        || e.Status == StageStatus.Running
                        || e.Status == StageStatus.RetryPending)
                    .CountAsync(cancellationToken).ConfigureAwait(false);
                var allowed = IsDispatchAllowed(active, units, _quota.MaxConcurrentStagesPerTenant);
                if (!allowed)
                {
                    _logger.LogWarning(
                        "Fairness cap hit for tenant {TenantId}: {Active} active stages (max {Max}).",
                        tenantId, active, _quota.MaxConcurrentStagesPerTenant);
                }

                return allowed;
            }
        }
#pragma warning disable CA1031 // Fairness is advisory: DB outage must not block dispatch; quota/cost gates stay fail-closed.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            _logger.LogWarning(ex, "Fairness dispatch check failed for tenant {TenantId}; allowing.", tenantId);
            return true;
        }
    }

    /// <summary>
    /// Whether the tenant may start one more provider call (Redis
    /// <c>concurrency</c> admissions; fail-open). Delegates to
    /// <see cref="RateLimiter"/> so Redis + in-memory behavior stay uniform.
    /// </summary>
    public Task<bool> TryAcquireProviderCallAsync(
        Guid tenantId,
        string provider,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        return _limiter.TryAcquireAsync(tenantId, provider, "concurrency", 1, cancellationToken);
    }

    /// <summary>
    /// Pure dispatch threshold. Pure for hermetic tests.
    /// </summary>
    public static bool IsDispatchAllowed(int activeStages, int units, int maxConcurrent)
    {
        if (activeStages < 0 || units < 0 || maxConcurrent < 1)
        {
            throw new DomainException("Fairness counts must be non-negative and max positive.");
        }

        return checked(activeStages + units) <= maxConcurrent;
    }
}
