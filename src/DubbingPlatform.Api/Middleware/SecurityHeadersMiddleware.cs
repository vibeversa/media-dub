using DubbingPlatform.Api.Services;

namespace DubbingPlatform.Api.Middleware;

/// <summary>
/// Transport hardening headers (Task 037, R3–R4).
/// Applied to every response: strict <c>Content-Security-Policy</c> with no
/// <c>unsafe-inline</c> and no <c>unsafe-eval</c>, <c>Referrer-Policy:
/// no-referrer</c>, <c>X-Content-Type-Options: nosniff</c>,
/// <c>X-Frame-Options: DENY</c>. Signed-URL issuance endpoints
/// (<c>/output/download</c>, <c>/exports/.../download</c>,
/// <c>/voice-previews/...</c>, <c>/uploads/.../parts</c>) additionally carry
/// <c>Cache-Control: private, no-store</c> so tokens are never cached.
/// CORS preflight from an unlisted origin is rejected by the CORS middleware
/// without leaking the allowlist contents (no echo of the request origin).
/// </summary>
public sealed class SecurityHeadersMiddleware
{
    /// <summary>
    /// Strict CSP: self-only with no inline/eval. Connect allows self plus
    /// configured API origins at runtime; the static header stays self-only
    /// and the CORS layer governs cross-origin access.
    /// </summary>
    public const string ContentSecurityPolicy =
        "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; font-src 'self' data:; object-src 'none'; base-uri 'self'; frame-ancestors 'none'";

    public const string ReferrerPolicy = "no-referrer";

    private static readonly string[] NoStorePrefixes =
    [
        "/output/download",
        "/exports/",
        "/voice-previews/",
        "/uploads/",
    ];

    private readonly RequestDelegate _next;

    public SecurityHeadersMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        context.Response.OnStarting(() =>
        {
            context.Response.Headers["Content-Security-Policy"] = ContentSecurityPolicy;
            context.Response.Headers["Referrer-Policy"] = ReferrerPolicy;
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers["X-Frame-Options"] = "DENY";

            if (IsIssuanceEndpoint(context.Request.Path.Value))
            {
                context.Response.Headers["Cache-Control"] = SignedUrlService.IssuanceCacheControl;
            }

            return Task.CompletedTask;
        });

        await _next(context).ConfigureAwait(false);
    }

    /// <summary>
    /// Whether the path issues signed URLs and must never be cached. Pure.
    /// </summary>
    public static bool IsIssuanceEndpoint(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        foreach (var prefix in NoStorePrefixes)
        {
            if (path.Contains(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
