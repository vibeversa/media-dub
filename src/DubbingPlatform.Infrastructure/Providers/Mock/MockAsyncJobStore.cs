using System.Collections.Concurrent;

namespace DubbingPlatform.Infrastructure.Providers.Mock;

/// <summary>
/// In-memory async-job simulator. <c>StartJob</c> returns a deterministic
/// <c>job_&lt;hash&gt;</c> id; <c>PollJob</c> returns <c>Running</c> twice then
/// <c>Succeeded</c> (counter keyed by job id). Each provider instance owns one
/// store so job ids never collide across capabilities.
/// </summary>
internal sealed class MockAsyncJobStore
{
    public const string Running = "Running";

    public const string Succeeded = "Succeeded";

    private readonly ConcurrentDictionary<string, int> _polls = new(StringComparer.Ordinal);

    /// <summary>
    /// Starts (or restarts) a job for the given stable key.
    /// </summary>
    public string StartJob(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var jobId = string.Concat("job_", MockDeterminism.StableHex(16, key));
        _polls[jobId] = 0;
        return jobId;
    }

    /// <summary>
    /// Polls a job. First two polls return <c>Running</c>, third and later
    /// return <c>Succeeded</c>. Unknown ids start at poll 1 (defensive).
    /// </summary>
    public (string Status, int PollCount) PollJob(string jobId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        var count = _polls.AddOrUpdate(jobId, 1, (_, current) => current + 1);
        return count <= 2 ? (Running, count) : (Succeeded, count);
    }

    /// <summary>
    /// Number of polls observed for a job (0 when never polled/started).
    /// </summary>
    public int PollCountFor(string jobId)
    {
        return _polls.TryGetValue(jobId, out var count) ? count : 0;
    }
}
