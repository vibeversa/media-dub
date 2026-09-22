using System.IdentityModel.Tokens.Jwt;
using System.Text.Json;
using DubbingPlatform.Api.Middleware;
using DubbingPlatform.Api.Models;
using DubbingPlatform.Application.Auth;
using DubbingPlatform.Application.Authorization;
using DubbingPlatform.Application.MultiTenancy;
using DubbingPlatform.Application.Options;
using DubbingPlatform.Application.Services;
using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DubbingPlatform.Api.Controllers;

/// <summary>
/// Caller identity and preferences: <c>GET /api/v1/me</c> returns user,
/// tenant, JWT+membership roles, the full 12-permission hint list, locale,
/// feature flags, and session expiry in one call; <c>GET|PUT
/// /api/v1/me/preferences</c> reads/writes the Task 001 whitelist
/// (<c>locale, timezone, theme, defaultProjectFilters, timelineZoom,
/// notificationPreferences</c>). Unknown key → 400
/// <c>PREFERENCE_KEY_UNKNOWN</c>; oversize value → 413
/// <c>PREFERENCE_VALUE_TOO_LARGE</c>. Disabled users get 403
/// <c>USER_DISABLED</c> on <c>/me</c> (and resolve zero permissions).
/// Missing tenant claim → 401 <c>TENANT_REQUIRED</c>. Tenant comes only from
/// the caller's claim; no <c>?tenantId</c> override is accepted. Permission
/// strings are hints: every other controller still enforces its
/// <c>[Authorize(Policy=...)]</c> policy. Idempotency-Key is not used on
/// these routes: <c>/me</c> is a read, and preference PUT is naturally
/// idempotent with last-writer-wins per key. Error examples: 401
/// <c>{ error: { code: "TENANT_REQUIRED", ... } }</c>; 403
/// <c>{ error: { code: "USER_DISABLED", ... } }</c>; 400
/// <c>{ error: { code: "PREFERENCE_KEY_UNKNOWN", ... } }</c>.
/// </summary>
[ApiController]
[Route("api/v1/me")]
[Authorize]
[ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
[ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status403Forbidden)]
public sealed class MeController : ControllerBase
{
    private const string DefaultLocale = "en-US";

    private readonly IStageExecutionContextFactory _contextFactory;
    private readonly IPermissionResolver _permissions;
    private readonly FeatureOptions _features;

    public MeController(
        IStageExecutionContextFactory contextFactory,
        IPermissionResolver permissions,
        IOptions<FeatureOptions> features)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        ArgumentNullException.ThrowIfNull(permissions);
        ArgumentNullException.ThrowIfNull(features);
        _contextFactory = contextFactory;
        _permissions = permissions;
        _features = features.Value;
    }

    /// <summary>
    /// Returns the caller's identity, tenant, roles, permissions, locale,
    /// flags, and session expiry in one call.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(MeResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        var tenantId = RequireTenantId();
        var userId = RequireUserId();
        var now = DateTimeOffset.UtcNow;

        TenantUser user;
        Tenant tenant;
        List<ProjectRole> membershipRoles;
        string? localeValue;
        using (TenantContext.BeginMaintenanceScope())
        {
            using var db = _contextFactory.CreateDbContext();
            var found = await db.Set<TenantUser>()
                .AsNoTracking()
                .FirstOrDefaultAsync(u => u.Id == userId && u.TenantId == tenantId, cancellationToken)
                .ConfigureAwait(false);
            if (found is null)
            {
                throw new InvalidCredentialsException("The credentials are invalid.");
            }

            user = found;
            tenant = await db.Set<Tenant>()
                .AsNoTracking()
                .FirstOrDefaultAsync(t => t.Id == tenantId, cancellationToken)
                .ConfigureAwait(false)
                ?? new Tenant(tenantId, tenantId.ToString("N"), string.Concat("tenant-", tenantId.ToString("N")), now);
            membershipRoles = await db.Set<ProjectMembership>()
                .AsNoTracking()
                .Where(m => m.TenantId == tenantId && m.UserId == userId)
                .Select(m => m.Role)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            localeValue = await db.Set<UserPreference>()
                .AsNoTracking()
                .Where(p => p.TenantId == tenantId && p.UserId == userId && p.Key == "locale")
                .Select(p => p.ValueJson)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        if (user.Status == TenantUserStatus.Disabled)
        {
            throw new UserDisabledException("The user account is disabled.");
        }

        var jwtRoles = User.GetRoles();
        var roleNames = membershipRoles
            .Select(r => r.ToString())
            .Concat(jwtRoles)
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(r => r, StringComparer.Ordinal)
            .ToList();
        var permissions = PermissionResolver.Resolve(user.Status, membershipRoles, jwtRoles)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        return Ok(new MeResponse(
            new MeUser(user.Id, user.Email, user.DisplayName, user.ExternalSubject, user.Status.ToString()),
            new MeTenant(tenant.Id, tenant.Name, tenant.Slug),
            roleNames,
            permissions,
            ParseLocale(localeValue),
            new MeFeatureFlags(_features.VideoIntelligenceEnabled, _features.LipSyncEnabled, _features.LocalInferenceEnabled),
            ReadSession(now)));
    }

    /// <summary>
    /// Reads the caller's whitelisted preferences.
    /// </summary>
    [HttpGet("preferences")]
    [ProducesResponseType(typeof(PreferencesResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetPreferences(CancellationToken cancellationToken)
    {
        var tenantId = RequireTenantId();
        var userId = RequireUserId();

        await RequireActiveUserAsync(tenantId, userId, cancellationToken).ConfigureAwait(false);

        Dictionary<string, string> stored;
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            stored = await db.Set<UserPreference>()
                .AsNoTracking()
                .Where(p => p.UserId == userId)
                .ToDictionaryAsync(p => p.Key, p => p.ValueJson, StringComparer.Ordinal, cancellationToken)
                .ConfigureAwait(false);
        }

        return Ok(ToPreferencesResponse(stored));
    }

    /// <summary>
    /// Upserts the caller's whitelisted preferences (last-writer-wins per key).
    /// </summary>
    [HttpPut("preferences")]
    [ProducesResponseType(typeof(PreferencesResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status413PayloadTooLarge)]
    public async Task<IActionResult> PutPreferences(
        [FromBody] UpdatePreferencesRequest request,
        CancellationToken cancellationToken)
    {
        var tenantId = RequireTenantId();
        var userId = RequireUserId();
        ArgumentNullException.ThrowIfNull(request);

        await RequireActiveUserAsync(tenantId, userId, cancellationToken).ConfigureAwait(false);

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in request.Preferences)
        {
            if (!UserPreference.AllowedKeys.Contains(pair.Key))
            {
                throw new PreferenceKeyUnknownException($"Preference key '{pair.Key}' is not allowed.");
            }

            var valueJson = JsonSerializer.Serialize(pair.Value);
            if (System.Text.Encoding.UTF8.GetByteCount(valueJson) > UserPreference.MaxValueBytes)
            {
                throw new PreferenceValueTooLargeException($"Preference '{pair.Key}' exceeds {UserPreference.MaxValueBytes} bytes.");
            }

            values[pair.Key] = valueJson;
        }

        var now = DateTimeOffset.UtcNow;
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            foreach (var pair in values)
            {
                var existing = await db.Set<UserPreference>()
                    .FirstOrDefaultAsync(
                        p => p.TenantId == tenantId && p.UserId == userId && p.Key == pair.Key,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (existing is null)
                {
                    db.Set<UserPreference>().Add(new UserPreference(tenantId, userId, pair.Key, pair.Value, now));
                }
                else
                {
                    existing.UpdateValue(pair.Value, now);
                }
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            var stored = await db.Set<UserPreference>()
                .AsNoTracking()
                .Where(p => p.UserId == userId)
                .ToDictionaryAsync(p => p.Key, p => p.ValueJson, StringComparer.Ordinal, cancellationToken)
                .ConfigureAwait(false);
            return Ok(ToPreferencesResponse(stored));
        }
    }

    private Guid RequireTenantId()
    {
        if (!User.TryGetTenantId(out var tenantId))
        {
            throw new TenantRequiredException("A tenant claim is required.");
        }

        return tenantId;
    }

    private Guid RequireUserId()
    {
        var raw = User.GetSubject();
        if (Guid.TryParse(raw.Trim(), out var userId) && userId != Guid.Empty)
        {
            return userId;
        }

        throw new InvalidCredentialsException("The credentials are invalid.");
    }

    private async Task RequireActiveUserAsync(Guid tenantId, Guid userId, CancellationToken cancellationToken)
    {
        TenantUser? user;
        using (TenantContext.BeginMaintenanceScope())
        {
            using var db = _contextFactory.CreateDbContext();
            user = await db.Set<TenantUser>()
                .AsNoTracking()
                .FirstOrDefaultAsync(u => u.Id == userId && u.TenantId == tenantId, cancellationToken)
                .ConfigureAwait(false);
        }

        if (user is null)
        {
            throw new InvalidCredentialsException("The credentials are invalid.");
        }

        if (user.Status == TenantUserStatus.Disabled)
        {
            throw new UserDisabledException("The user account is disabled.");
        }
    }

    private MeSession ReadSession(DateTimeOffset now)
    {
        var issued = now;
        var expires = now.Add(AuthService.AccessLifetime);
        foreach (var claim in User.Claims)
        {
            if (string.Equals(claim.Type, JwtRegisteredClaimNames.Iat, StringComparison.Ordinal)
                && long.TryParse(claim.Value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var iat))
            {
                issued = DateTimeOffset.FromUnixTimeSeconds(iat);
            }

            if (string.Equals(claim.Type, JwtRegisteredClaimNames.Exp, StringComparison.Ordinal)
                || string.Equals(claim.Type, "exp", StringComparison.Ordinal))
            {
                if (long.TryParse(claim.Value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var exp))
                {
                    expires = DateTimeOffset.FromUnixTimeSeconds(exp);
                }
            }
        }

        return new MeSession(issued, expires);
    }

    private static string ParseLocale(string? valueJson)
    {
        if (string.IsNullOrWhiteSpace(valueJson))
        {
            return DefaultLocale;
        }

        try
        {
            using var document = JsonDocument.Parse(valueJson);
            if (document.RootElement.ValueKind == JsonValueKind.String)
            {
                var locale = document.RootElement.GetString()?.Trim();
                return string.IsNullOrWhiteSpace(locale) ? DefaultLocale : locale;
            }
        }
        catch (JsonException)
        {
        }

        return DefaultLocale;
    }

    private static PreferencesResponse ToPreferencesResponse(Dictionary<string, string> stored)
    {
        var response = new PreferencesResponse();
        foreach (var pair in stored.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            try
            {
                response.Preferences[pair.Key] = JsonDocument.Parse(pair.Value).RootElement.Clone();
            }
            catch (JsonException)
            {
                response.Preferences[pair.Key] = JsonDocument.Parse(JsonSerializer.Serialize(pair.Value)).RootElement.Clone();
            }
        }

        return response;
    }
}
