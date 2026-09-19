using DubbingPlatform.Application.Abstractions.Providers.Dtos;

namespace DubbingPlatform.Application.Abstractions.Providers;

/// <summary>
/// On-device / GPU-local inference provider.
/// </summary>
public interface ILocalInferenceProvider
{
    Task<LocalInferenceResponse> InferAsync(LocalInferenceRequest request, CancellationToken cancellationToken);
}
