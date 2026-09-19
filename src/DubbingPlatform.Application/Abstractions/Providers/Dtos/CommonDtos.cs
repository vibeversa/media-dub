namespace DubbingPlatform.Application.Abstractions.Providers.Dtos;

/// <summary>
/// Token/usage telemetry reported by providers. All fields optional because
/// providers differ in what they meter; recorders persist what is present.
/// </summary>
public sealed record ProviderUsage(
    int? TokensIn,
    int? TokensOut,
    double? AudioSeconds,
    double? EstimatedCostUsd);
