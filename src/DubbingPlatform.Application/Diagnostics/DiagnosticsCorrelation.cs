namespace DubbingPlatform.Application.Diagnostics;

/// <summary>
/// Correlation-id helper for the diagnostics read layer. Every diagnostics
/// DTO carries a <c>CorrelationId</c>; callers may supply one per call and a
/// fresh id is generated when they omit it. All DTOs produced by a single
/// call share the call's id. Pure.
/// </summary>
public static class DiagnosticsCorrelation
{
    /// <summary>
    /// Normalizes a caller-supplied correlation id, generating a fresh
    /// 32-char id when the caller omits one.
    /// </summary>
    public static string Normalize(string? correlationId)
    {
        if (!string.IsNullOrWhiteSpace(correlationId))
        {
            return correlationId.Trim();
        }

        return Guid.NewGuid().ToString("N");
    }
}
