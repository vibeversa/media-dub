using DubbingPlatform.Domain.Entities;

namespace DubbingPlatform.Application.Services;

/// <summary>
/// Result of an atomic stage claim. <see cref="IsNew"/> is true when this call
/// inserted the execution; false when a duplicate insert lost the unique race
/// and the pre-existing row was returned.
/// </summary>
public sealed record StageClaimResult(StageExecution Execution, bool IsNew);
