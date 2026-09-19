using DubbingPlatform.Application.Errors;
using DubbingPlatform.Application.Exceptions;

namespace DubbingPlatform.Infrastructure.Providers;

/// <summary>
/// Generic long-running job poller. Flow: start returns an external job id,
/// the id is stored on <c>ProviderExecution.ExternalJobId</c> by the caller,
/// then <see cref="PollAsync"/> polls every <paramref name="pollInterval"/>
/// up to <paramref name="timeout"/> (defaults 10s / 30min). On lease loss the
/// caller reconciles by external job id via <see cref="ReconcileAsync"/> (single
/// status fetch, no waiting). Remote <c>Failed</c> (including expiry) throws
/// <c>PROVIDER_FAILED</c>; timeout throws <c>PROVIDER_TIMEOUT</c>.
/// </summary>
public sealed class ProviderJobPoller
{
    public static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(10);

    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(30);

    public sealed record JobStatus(bool Completed, bool Failed, string Status, string? Reason);

    /// <summary>
    /// Polls until completed/failed or timeout.
    /// </summary>
    public async Task<JobStatus> PollAsync(
        Func<string, CancellationToken, Task<JobStatus>> fetchStatus,
        string externalJobId,
        TimeSpan? pollInterval = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fetchStatus);
        ArgumentException.ThrowIfNullOrWhiteSpace(externalJobId);

        var interval = pollInterval ?? DefaultPollInterval;
        var limit = timeout ?? DefaultTimeout;
        if (interval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(pollInterval), "Poll interval must be positive.");
        }

        if (limit <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "Timeout must be positive.");
        }

        var started = DateTimeOffset.UtcNow;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var status = await fetchStatus(externalJobId, cancellationToken).ConfigureAwait(false);
            if (status.Completed)
            {
                return status;
            }

            if (status.Failed)
            {
                var reason = string.IsNullOrWhiteSpace(status.Reason) ? status.Status : status.Reason;
                throw new ErrorCodeException(
                    ErrorCodes.ProviderFailed,
                    $"Provider job '{externalJobId}' failed ({reason}).");
            }

            if (DateTimeOffset.UtcNow - started >= limit)
            {
                throw new ErrorCodeException(
                    ErrorCodes.ProviderTimeout,
                    $"Provider job '{externalJobId}' timed out after {limit.TotalMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture)} minutes.");
            }

            await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Single status fetch for lease-loss reconciliation by external job id.
    /// </summary>
    public Task<JobStatus> ReconcileAsync(
        Func<string, CancellationToken, Task<JobStatus>> fetchStatus,
        string externalJobId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fetchStatus);
        ArgumentException.ThrowIfNullOrWhiteSpace(externalJobId);
        return fetchStatus(externalJobId, cancellationToken);
    }

    /// <summary>
    /// Parses a status string into a <see cref="JobStatus"/>.
    /// Succeeded/completed → completed; failed/expired/cancelled → failed.
    /// </summary>
    public static JobStatus FromStatusString(string? status, string? reason = null)
    {
        var normalized = (status ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "succeeded" or "completed" or "done" => new JobStatus(true, false, status ?? "Succeeded", reason),
            "failed" or "expired" or "cancelled" or "canceled" => new JobStatus(false, true, status ?? "Failed", reason),
            _ => new JobStatus(false, false, status ?? "Running", reason),
        };
    }
}
