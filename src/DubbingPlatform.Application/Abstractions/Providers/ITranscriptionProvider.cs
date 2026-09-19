using DubbingPlatform.Application.Abstractions.Providers.Dtos;

namespace DubbingPlatform.Application.Abstractions.Providers;

/// <summary>
/// Speech-to-text provider.
/// </summary>
public interface ITranscriptionProvider
{
    Task<TranscriptionResponse> TranscribeAsync(TranscriptionRequest request, CancellationToken cancellationToken);
}
