using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;

namespace DubbingPlatform.Api.Auth;

/// <summary>
/// Renders policy/ownership denials as structured 403 FORBIDDEN envelopes
/// (instead of the default empty 403) while preserving the correlation id.
/// Unauthenticated challenges are handled by <c>JwtBearerEvents.OnChallenge</c>
/// (401 UNAUTHORIZED envelope); this handler covers authenticated-but-denied.
/// </summary>
public sealed class EnvelopeAuthorizationMiddlewareResultHandler : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _inner = new();

    public async Task HandleAsync(
        RequestDelegate next,
        HttpContext context,
        AuthorizationPolicy policy,
        PolicyAuthorizationResult authorizeResult)
    {
        if (authorizeResult.Challenged)
        {
            await AuthEnvelopeWriter.WriteUnauthorizedAsync(context, "Authentication is required.").ConfigureAwait(false);
            return;
        }

        if (authorizeResult.Forbidden)
        {
            await AuthEnvelopeWriter.WriteForbiddenAsync(context, "The caller is not allowed to access this resource.").ConfigureAwait(false);
            return;
        }

        await _inner.HandleAsync(next, context, policy, authorizeResult).ConfigureAwait(false);
    }
}
