using DubbingPlatform.Application.Abstractions.Providers.Dtos;

namespace DubbingPlatform.Application.Abstractions.Providers;

/// <summary>
/// Speaker-diarization provider.
/// </summary>
public interface IDiarizationProvider
{
    Task<DiarizationResponse> DiarizeAsync(DiarizationRequest request, CancellationToken cancellationToken);
}
