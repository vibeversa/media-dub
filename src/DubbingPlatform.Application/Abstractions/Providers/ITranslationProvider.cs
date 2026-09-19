using DubbingPlatform.Application.Abstractions.Providers.Dtos;

namespace DubbingPlatform.Application.Abstractions.Providers;

/// <summary>
/// Text-translation provider.
/// </summary>
public interface ITranslationProvider
{
    Task<TranslationResponse> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken);
}
