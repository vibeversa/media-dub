namespace DubbingPlatform.Application.Services;

/// <summary>
/// Result of recording one stage-unit completion in the barrier ledger.
/// <see cref="StageComplete"/> is true for exactly one call per stage barrier:
/// the call whose increment first reaches the expected unit count. Only that
/// caller may publish the stage-level completion event.
/// </summary>
public sealed record BarrierResult(
    bool IsDuplicate,
    bool StageComplete,
    int ExpectedUnits,
    int CompletedUnits,
    int SkippedUnits);
