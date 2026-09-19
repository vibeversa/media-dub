namespace DubbingPlatform.Application.Services;

/// <summary>
/// Endpoint-class retention for idempotency rows. Matches the binding:
/// project-create 7d, upload-create 7d, upload-complete 7d, processing-start 7d,
/// cancel 24h, retry 24h, export 24h; everything else defaults to 24h.
/// Endpoint keys are <c>METHOD path</c> (for example <c>POST /api/v1/projects</c>).
/// </summary>
public static class IdempotencyRetention
{
    public static readonly TimeSpan ProjectCreate = TimeSpan.FromDays(7);

    public static readonly TimeSpan UploadCreate = TimeSpan.FromDays(7);

    public static readonly TimeSpan UploadComplete = TimeSpan.FromDays(7);

    public static readonly TimeSpan ProcessingStart = TimeSpan.FromDays(7);

    public static readonly TimeSpan Cancel = TimeSpan.FromHours(24);

    public static readonly TimeSpan Retry = TimeSpan.FromHours(24);

    public static readonly TimeSpan Export = TimeSpan.FromHours(24);

    public static readonly TimeSpan Default = TimeSpan.FromHours(24);

    /// <summary>
    /// Resolves the expiry for an endpoint key.
    /// </summary>
    public static TimeSpan ExpiryFor(string endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return Default;
        }

        var normalized = endpoint.Trim().ToLowerInvariant();
        if (normalized.Contains("/cancel", StringComparison.Ordinal))
        {
            return Cancel;
        }

        if (normalized.Contains("/retry", StringComparison.Ordinal))
        {
            return Retry;
        }

        if (normalized.Contains("/export", StringComparison.Ordinal))
        {
            return Export;
        }

        if (normalized.Contains("/upload", StringComparison.Ordinal))
        {
            return normalized.Contains("complete", StringComparison.Ordinal) ? UploadComplete : UploadCreate;
        }

        if (normalized.Contains("/processing", StringComparison.Ordinal))
        {
            return ProcessingStart;
        }

        if (normalized.Contains("/project", StringComparison.Ordinal))
        {
            return ProjectCreate;
        }

        return Default;
    }
}
