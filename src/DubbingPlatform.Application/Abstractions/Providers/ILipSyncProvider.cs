using DubbingPlatform.Application.Abstractions.Providers.Dtos;

namespace DubbingPlatform.Application.Abstractions.Providers;

/// <summary>
/// Lip-movement analysis provider (optional enrichment).
/// </summary>
public interface ILipSyncProvider
{
    Task<LipSyncResponse> AnalyzeAsync(LipSyncRequest request, CancellationToken cancellationToken);
}
