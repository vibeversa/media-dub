using DubbingPlatform.Application.Abstractions.Providers.Dtos;

namespace DubbingPlatform.Application.Abstractions.Providers;

/// <summary>
/// Text-to-speech provider.
/// </summary>
public interface ITtsProvider
{
    Task<TtsResponse> SynthesizeAsync(TtsRequest request, CancellationToken cancellationToken);
}
