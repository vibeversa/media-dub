using DubbingPlatform.Application.Abstractions.Providers.Dtos;

namespace DubbingPlatform.Application.Abstractions.Providers;

/// <summary>
/// Dialogue/background source-separation provider.
/// </summary>
public interface ISourceSeparationProvider
{
    Task<SeparationResponse> SeparateAsync(SeparationRequest request, CancellationToken cancellationToken);
}
