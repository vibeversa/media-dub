using DubbingPlatform.Application.Abstractions.Providers.Dtos;

namespace DubbingPlatform.Application.Abstractions.Providers;

/// <summary>
/// Voice-activity-detection provider.
/// </summary>
public interface IVadProvider
{
    Task<VadResponse> DetectAsync(VadRequest request, CancellationToken cancellationToken);
}
