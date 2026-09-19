using System.Security.Claims;
using DubbingPlatform.Application.Exceptions;

namespace DubbingPlatform.Application.Authorization;

/// <summary>
/// Claim-type constants and helpers. Tenant comes from <c>tid</c>, user from
/// <c>sub</c>, roles from repeated <c>roles</c> claims (<c>role</c> and
/// <c>ClaimTypes.Role</c> accepted for IdP interop).
/// </summary>
public static class ClaimTypes
{
    public const string TenantId = "tid";

    public const string Subject = "sub";

    public const string Roles = "roles";

    public const string Role = "role";
}

/// <summary>
/// ClaimsPrincipal helpers for tenant/user/role extraction.
/// </summary>
public static class ClaimsPrincipalExtensions
{
    /// <summary>
    /// Gets the tenant id from the <c>tid</c> claim. Throws
    /// <see cref="ForbiddenException"/> (403 FORBIDDEN) when missing or invalid.
    /// </summary>
    public static Guid GetTenantId(this ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(user);
        var raw = user.FindFirst(Authorization.ClaimTypes.TenantId)?.Value;
        if (string.IsNullOrWhiteSpace(raw) || !Guid.TryParse(raw.Trim(), out var tenantId) || tenantId == Guid.Empty)
        {
            throw new ForbiddenException("The 'tid' tenant claim is missing or invalid.");
        }

        return tenantId;
    }

    /// <summary>
    /// Tries to get the tenant id without throwing.
    /// </summary>
    public static bool TryGetTenantId(this ClaimsPrincipal user, out Guid tenantId)
    {
        tenantId = Guid.Empty;
        if (user is null)
        {
            return false;
        }

        var raw = user.FindFirst(Authorization.ClaimTypes.TenantId)?.Value;
        return !string.IsNullOrWhiteSpace(raw) && Guid.TryParse(raw.Trim(), out tenantId) && tenantId != Guid.Empty;
    }

    /// <summary>
    /// Gets the subject (<c>sub</c>) claim, or "unknown" when absent.
    /// </summary>
    public static string GetSubject(this ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(user);
        var raw = user.FindFirst(Authorization.ClaimTypes.Subject)?.Value;
        return string.IsNullOrWhiteSpace(raw) ? "unknown" : raw.Trim();
    }

    /// <summary>
    /// Gets all role values from <c>roles</c>, <c>role</c>, and
    /// <c>ClaimTypes.Role</c> claims.
    /// </summary>
    public static IReadOnlyList<string> GetRoles(this ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(user);
        var roles = new List<string>();
        foreach (var claim in user.Claims)
        {
            if (string.Equals(claim.Type, Authorization.ClaimTypes.Roles, StringComparison.Ordinal)
                || string.Equals(claim.Type, Authorization.ClaimTypes.Role, StringComparison.Ordinal)
                || string.Equals(claim.Type, System.Security.Claims.ClaimTypes.Role, StringComparison.Ordinal))
            {
                var value = claim.Value?.Trim();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    roles.Add(value);
                }
            }
        }

        return roles;
    }

    /// <summary>
    /// Determines whether the user holds any of the given roles.
    /// </summary>
    public static bool HasAnyRole(this ClaimsPrincipal user, IEnumerable<string> allowed)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(allowed);
        var held = new HashSet<string>(user.GetRoles(), StringComparer.Ordinal);
        return allowed.Any(held.Contains);
    }
}
