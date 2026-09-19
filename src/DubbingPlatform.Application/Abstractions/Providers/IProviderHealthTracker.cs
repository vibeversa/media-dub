using DubbingPlatform.Domain.Enums;

namespace DubbingPlatform.Application.Abstractions.Providers;

/// <summary>
/// Snapshot of one provider's runtime health. Config validation is separate
/// (<see cref="ValidateConfig"/>) and never mixed into this runtime view.
/// </summary>
public sealed record ProviderHealthState(
    ProviderType Provider,
    bool ConfigValid,
    bool RuntimeAvailable,
    bool CircuitOpen,
    double ErrorRate,
    int TotalCalls,
    bool RateLimited,
    DateTimeOffset? RateLimitedUntil,
    bool IsHealthy);

/// <summary>
/// Runtime health gate for routing. Memory is authoritative per replica; Redis
/// (when configured) is a best-effort cross-instance mirror.
/// </summary>
public interface IProviderHealthTracker
{
    bool IsHealthy(ProviderType provider);

    ProviderHealthState GetState(ProviderType provider);

    void RecordSuccess(ProviderType provider);

    void RecordFailure(ProviderType provider, OutcomeClass outcome);

    void RecordRateLimited(ProviderType provider, TimeSpan? retryAfter = null);

    void OpenCircuit(ProviderType provider);

    void ResetCircuit(ProviderType provider);

    void ValidateConfig(ProviderType provider);
}
