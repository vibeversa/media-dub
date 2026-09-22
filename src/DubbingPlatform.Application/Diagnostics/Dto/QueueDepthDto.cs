namespace DubbingPlatform.Application.Diagnostics.Dto;

/// <summary>
/// Pending-message depth for one queue in the frozen queue taxonomy.
/// Counts only — never message bodies.
/// </summary>
public sealed record QueueDepthDto(
    string CorrelationId,
    string Queue,
    long Depth);
