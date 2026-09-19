using DubbingPlatform.Application.Abstractions.Providers.Dtos;

namespace DubbingPlatform.Application.Abstractions.Providers;

/// <summary>
/// Face/active-speaker video-intelligence provider.
/// </summary>
public interface IVideoIntelligenceProvider
{
    Task<VideoIntelligenceResponse> AnalyzeAsync(VideoIntelligenceRequest request, CancellationToken cancellationToken);
}
