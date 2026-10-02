using DubbingPlatform.Application.Abstractions.Providers;
using DubbingPlatform.Application.Abstractions.Providers.Dtos;
using DubbingPlatform.Domain.Enums;

namespace DubbingPlatform.Infrastructure.Providers;

/// <summary>
/// Opt-in long-running job (batch) contract for transcription providers that
/// expose an asynchronous API: <see cref="StartBatchAsync"/> returns an
/// external job id stored on <c>ProviderExecution.ExternalJobId</c>, and
/// <see cref="GetBatchStatusAsync"/> feeds <see cref="ProviderJobPoller"/> for
/// polling and lease-loss reconcile.
/// </summary>
/// <remarks>
/// GAP-012 scope: only Azure and OpenAI STT implement this contract. Google STT
/// (<c>:recognize</c>) and the local inference sidecar (<c>/infer</c>) are
/// synchronous by contract and deliberately do not implement it, so batch
/// support is compiler-checked instead of duck-typed. The descriptor's
/// <c>AsyncJob</c> flag is validated against this contract at startup — see
/// <see cref="BatchProviderContract"/>.
/// </remarks>
public interface IBatchTranscriptionProvider : ITranscriptionProvider
{
    /// <summary>Starts a batch job and returns the external job id.</summary>
    Task<string> StartBatchAsync(TranscriptionRequest request, CancellationToken cancellationToken);

    /// <summary>Fetches batch status for the poller/reconciler.</summary>
    Task<ProviderJobPoller.JobStatus> GetBatchStatusAsync(string externalJobId, CancellationToken cancellationToken);
}

/// <summary>
/// Helpers keeping the advertised <c>AsyncJob</c> capability honest against the
/// implemented <see cref="IBatchTranscriptionProvider"/> contract. Pure.
/// </summary>
public static class BatchProviderContract
{
    /// <summary>
    /// Whether <paramref name="providerType"/> implements the batch contract.
    /// </summary>
    public static bool SupportsBatch(Type? providerType) =>
        providerType is not null && typeof(IBatchTranscriptionProvider).IsAssignableFrom(providerType);

    /// <summary>
    /// Returns an error message when a descriptor advertises
    /// <c>AsyncJob</c> for a provider implementation that cannot honor it, else
    /// null. Pure.
    /// </summary>
    public static string? ValidateAsyncJobFlag(
        ProviderType provider,
        ProviderCapability capability,
        Type? providerType,
        bool asyncJob)
    {
        if (!asyncJob)
        {
            return null;
        }

        if (SupportsBatch(providerType))
        {
            return null;
        }

        var implemented = providerType is null ? "no provider implementation" : $"'{providerType.Name}'";
        return $"Providers:Descriptors advertises AsyncJob for '{provider}/{capability}' but {implemented} does not implement {nameof(IBatchTranscriptionProvider)}.";
    }
}