namespace DubbingPlatform.Infrastructure.Orchestration;

/// <summary>
/// Frozen cost-gate contract for dispatch. Real budget enforcement lands in
/// Task 36; until then the allow-all stub keeps the pipeline flowing while the
/// call sites and signatures stay stable.
/// </summary>
public interface ICostGate
{
    /// <summary>
    /// Whether <paramref name="units"/> of <paramref name="stage"/> may be
    /// dispatched for the tenant/run under current cost budgets.
    /// </summary>
    Task<bool> CanProceedAsync(Guid tenantId, Guid runId, string stage, int units, CancellationToken cancellationToken = default);
}

/// <summary>
/// Frozen rate-gate contract for dispatch. Real Redis-backed enforcement lands
/// in Task 36; until then the allow-all stub keeps the pipeline flowing while
/// the call sites and signatures stay stable.
/// </summary>
public interface IRateGate
{
    /// <summary>
    /// Whether <paramref name="requested"/> units of <paramref name="stage"/>
    /// may be dispatched for the tenant under current rate limits.
    /// </summary>
    Task<bool> CanProceedAsync(Guid tenantId, string stage, int requested, CancellationToken cancellationToken = default);
}

/// <summary>
/// Allow-all cost gate. Task 36 replaces the registration, not the contract.
/// </summary>
public sealed class AllowAllCostGate : ICostGate
{
    public Task<bool> CanProceedAsync(Guid tenantId, Guid runId, string stage, int units, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(true);
    }
}

/// <summary>
/// Allow-all rate gate. Task 36 replaces the registration, not the contract.
/// </summary>
public sealed class AllowAllRateGate : IRateGate
{
    public Task<bool> CanProceedAsync(Guid tenantId, string stage, int requested, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(true);
    }
}
