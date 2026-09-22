using DubbingPlatform.Api.Middleware;
using DubbingPlatform.Api.Models;
using DubbingPlatform.Application.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace DubbingPlatform.Api.Controllers;

/// <summary>
/// Session issuance: <c>POST /api/v1/auth/login</c> (401
/// <c>INVALID_CREDENTIALS</c> on bad credential, 403 <c>USER_DISABLED</c> when
/// disabled), <c>POST /api/v1/auth/refresh</c> (rotates; reuse → 401
/// <c>TOKEN_REUSED</c> with whole-family revocation; expired/unknown → 401
/// <c>TOKEN_EXPIRED</c>), <c>POST /api/v1/auth/logout</c> (revokes one
/// session; unknown/expired token still 200). Anonymous. Rate limited:
/// login 5/min/IP, refresh 30/min/user (relax via <c>AuthRateLimit</c>).
/// Idempotency-Key is intentionally not supported: login/refresh mint fresh
/// secrets per call by design. Error examples: 401
/// <c>{ error: { code: "INVALID_CREDENTIALS", message: "The credentials are invalid.", ... } }</c>;
/// 403 <c>{ error: { code: "USER_DISABLED", ... } }</c>.
/// </summary>
[ApiController]
[Route("api/v1/auth")]
[ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
public sealed class AuthController : ControllerBase
{
    public const string LoginPolicy = "auth-login";

    public const string RefreshPolicy = "auth-refresh";

    private readonly AuthService _auth;

    public AuthController(AuthService auth)
    {
        ArgumentNullException.ThrowIfNull(auth);
        _auth = auth;
    }

    /// <summary>
    /// Validates the external credential and issues an access+refresh pair.
    /// Writes an <c>auth.login</c> audit event.
    /// </summary>
    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting(LoginPolicy)]
    [ProducesResponseType(typeof(TokenResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Login([FromBody] LoginRequest request, CancellationToken cancellationToken)
    {
        var correlationId = CorrelationIdMiddleware.GetCorrelationId(HttpContext);
        var pair = await _auth.LoginAsync(request.TenantId, request.ExternalSubject, correlationId, cancellationToken).ConfigureAwait(false);
        return Ok(ToResponse(pair));
    }

    /// <summary>
    /// Rotates a refresh token within its family.
    /// </summary>
    [HttpPost("refresh")]
    [AllowAnonymous]
    [EnableRateLimiting(RefreshPolicy)]
    [ProducesResponseType(typeof(TokenResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Refresh([FromBody] RefreshRequest request, CancellationToken cancellationToken)
    {
        var pair = await _auth.RefreshAsync(request.RefreshToken, cancellationToken).ConfigureAwait(false);
        return Ok(ToResponse(pair));
    }

    /// <summary>
    /// Revokes a refresh token. Idempotent: unknown/expired/absent tokens
    /// still return 200. Writes an <c>auth.logout</c> audit event when the
    /// token resolves.
    /// </summary>
    [HttpPost("logout")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> Logout([FromBody] LogoutRequest? request, CancellationToken cancellationToken)
    {
        var correlationId = CorrelationIdMiddleware.GetCorrelationId(HttpContext);
        await _auth.LogoutAsync(request?.RefreshToken, correlationId, cancellationToken).ConfigureAwait(false);
        return Ok(new { loggedOut = true });
    }

    private static TokenResponse ToResponse(TokenPair pair)
    {
        return new TokenResponse(
            pair.AccessToken,
            pair.RefreshToken,
            "Bearer",
            (long)(pair.AccessExpiresAt - pair.IssuedAt).TotalSeconds,
            pair.IssuedAt,
            pair.AccessExpiresAt,
            pair.RefreshExpiresAt);
    }
}
