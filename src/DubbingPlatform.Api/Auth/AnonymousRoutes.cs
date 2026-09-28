namespace DubbingPlatform.Api.Auth;

/// <summary>
/// Explicit allowlist of anonymous (unauthenticated) routes (Task 037, R1).
/// Product endpoints require an authenticated principal everywhere else;
/// only session bootstrap (login/refresh/logout), liveness/readiness probes,
/// and the OpenAPI document are reachable without a bearer token.
/// Logout is intentionally anonymous and idempotent: an unknown, expired, or
/// absent refresh token still returns 200 so clients can always clear local
/// session state without leaking token validity.
/// CORS preflight (OPTIONS) is handled by the CORS middleware before auth
/// and is not an application route.
/// </summary>
public static class AnonymousRoutes
{
    /// <summary>POST /api/v1/auth/login — session issuance.</summary>
    public const string Login = "POST /api/v1/auth/login";

    /// <summary>POST /api/v1/auth/refresh — refresh rotation.</summary>
    public const string Refresh = "POST /api/v1/auth/refresh";

    /// <summary>POST /api/v1/auth/logout — idempotent session revocation.</summary>
    public const string Logout = "POST /api/v1/auth/logout";

    /// <summary>GET /health/live — liveness probe.</summary>
    public const string HealthLive = "GET /health/live";

    /// <summary>GET /health/ready — readiness probe.</summary>
    public const string HealthReady = "GET /health/ready";

    /// <summary>GET /openapi/v1.json — public API document.</summary>
    public const string OpenApi = "GET /openapi/v1.json";

    /// <summary>
    /// All anonymous routes in <c>METHOD path</c> form.
    /// </summary>
    public static readonly string[] All =
    [
        Login,
        Refresh,
        Logout,
        HealthLive,
        HealthReady,
        OpenApi,
    ];

    /// <summary>
    /// Whether the given method+path is anonymous-allowed. Pure.
    /// Path comparison is case-insensitive and ignores trailing slashes and
    /// query strings; method comparison is case-insensitive.
    /// </summary>
    public static bool IsAnonymous(string? method, string? path)
    {
        if (string.IsNullOrWhiteSpace(method) || string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var normalizedPath = path.Trim();
        var queryIndex = normalizedPath.IndexOf('?', StringComparison.Ordinal);
        if (queryIndex >= 0)
        {
            normalizedPath = normalizedPath.Substring(0, queryIndex);
        }

        normalizedPath = normalizedPath.TrimEnd('/');
        if (normalizedPath.Length == 0)
        {
            normalizedPath = "/";
        }

        var candidate = string.Concat(method.Trim().ToUpperInvariant(), " ", normalizedPath);
        foreach (var allowed in All)
        {
            if (string.Equals(candidate, allowed, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
