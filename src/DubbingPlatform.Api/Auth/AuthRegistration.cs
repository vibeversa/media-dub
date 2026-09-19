using DubbingPlatform.Application.Authorization;
using Microsoft.AspNetCore.Authorization;

namespace DubbingPlatform.Api.Auth;

/// <summary>
/// Authorization setup: six hierarchical policies plus the project-ownership
/// resource requirement. Role checks accept <c>roles</c>, <c>role</c>, and
/// <c>ClaimTypes.Role</c> claim types so OIDC and HS256 test tokens interop.
/// </summary>
public static class AuthRegistration
{
    /// <summary>
    /// Adds the six policies. Each policy requires an authenticated user holding
    /// one of its allowed roles, plus project ownership where applicable.
    /// </summary>
    public static void AddPolicies(AuthorizationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        foreach (var policy in AuthPolicies.All)
        {
            var allowed = AuthPolicies.AllowedRoles(policy);
            options.AddPolicy(policy, builder =>
            {
                builder.RequireAuthenticatedUser();
                builder.RequireAssertion(context => context.User.HasAnyRole(allowed));
                if (!string.Equals(policy, AuthPolicies.RequireService, StringComparison.Ordinal))
                {
                    builder.Requirements.Add(new ProjectOwnershipRequirement());
                }
            });
        }
    }
}
