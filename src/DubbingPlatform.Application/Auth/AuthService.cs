using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using DubbingPlatform.Application.MultiTenancy;
using DubbingPlatform.Application.Options;
using DubbingPlatform.Application.Services;
using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace DubbingPlatform.Application.Auth;

/// <summary>
/// Issued token pair.
/// </summary>
public sealed record TokenPair(
    string AccessToken,
    string RefreshToken,
    DateTimeOffset IssuedAt,
    DateTimeOffset AccessExpiresAt,
    DateTimeOffset RefreshExpiresAt);

/// <summary>
/// Session issuance over external-subject credentials. Login validates the
/// tenant/user pair (unknown → 401 INVALID_CREDENTIALS with no
/// user-enumeration detail; disabled → 403 USER_DISABLED) and issues a
/// short-lived HS256 access JWT plus an opaque refresh token persisted as a
/// salted SHA-256 hash. Refresh rotates within the same family; presenting a
/// revoked token revokes the whole family (theft detection, 401 TOKEN_REUSED).
/// Logout revokes a single session and is idempotent. Login/logout append an
/// audit event carrying the correlationId; tokens, salts, and hashes are never
/// logged or persisted in the clear.
/// </summary>
public sealed class AuthService
{
    public const string AuditLoginAction = "auth.login";

    public const string AuditLogoutAction = "auth.logout";

    public const string AuditResourceType = "auth";

    /// <summary>Access-token lifetime (short-lived).</summary>
    public static readonly TimeSpan AccessLifetime = TimeSpan.FromMinutes(15);

    /// <summary>Refresh-token lifetime.</summary>
    public static readonly TimeSpan RefreshLifetime = TimeSpan.FromDays(7);

    private readonly IStageExecutionContextFactory _contextFactory;
    private readonly AuthOptions _auth;
    private readonly AuditService _audit;
    private readonly ILogger<AuthService> _logger;
    private readonly TimeProvider _time;

    public AuthService(
        IStageExecutionContextFactory contextFactory,
        IOptions<AuthOptions> authOptions,
        AuditService audit,
        ILogger<AuthService> logger,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        ArgumentNullException.ThrowIfNull(authOptions);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(logger);
        _contextFactory = contextFactory;
        _auth = authOptions.Value;
        _audit = audit;
        _logger = logger;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Validates the external credential and issues a token pair.
    /// </summary>
    public async Task<TokenPair> LoginAsync(
        Guid tenantId,
        string externalSubject,
        string correlationId,
        CancellationToken cancellationToken = default)
    {
        if (tenantId == Guid.Empty)
        {
            throw new TenantRequiredException("A tenant is required to sign in.");
        }

        if (string.IsNullOrWhiteSpace(externalSubject))
        {
            throw new InvalidCredentialsException("The credentials are invalid.");
        }

        var subject = externalSubject.Trim();
        var now = _time.GetUtcNow();

        TenantUser user;
        List<string> roles;
        using (TenantContext.BeginMaintenanceScope())
        {
            using var db = _contextFactory.CreateDbContext();
            var found = await db.Set<TenantUser>()
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    u => u.TenantId == tenantId && u.ExternalSubject == subject,
                    cancellationToken)
                .ConfigureAwait(false);
            if (found is null)
            {
                throw new InvalidCredentialsException("The credentials are invalid.");
            }

            user = found;
            roles = await db.Set<ProjectMembership>()
                .AsNoTracking()
                .Where(m => m.TenantId == tenantId && m.UserId == found.Id)
                .Select(m => m.Role.ToString())
                .Distinct()
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        if (user.Status == TenantUserStatus.Disabled)
        {
            throw new UserDisabledException("The user account is disabled.");
        }

        var pair = await IssuePairAsync(tenantId, user.Id, roles, Guid.NewGuid(), now, cancellationToken).ConfigureAwait(false);

        await _audit.LogAsync(
            tenantId, null, user.Id.ToString("D"), AuditLoginAction,
            AuditResourceType, user.Id.ToString("D"),
            $"{{\"correlationId\":\"{JsonEscape(correlationId)}\"}}",
            cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("User signed in. {TenantId} {UserId}", tenantId, user.Id);
        return pair;
    }

    /// <summary>
    /// Rotates a refresh token. Reuse of a revoked token revokes the whole
    /// family and throws <see cref="TokenReusedException"/>.
    /// </summary>
    public async Task<TokenPair> RefreshAsync(
        string refreshToken,
        CancellationToken cancellationToken = default)
    {
        var now = _time.GetUtcNow();
        if (!TryParseToken(refreshToken, out var sessionId, out var secret))
        {
            throw new TokenExpiredException("The refresh token is expired or invalid.");
        }

        RefreshSession session;
        List<string> roles;
        TenantUserStatus status;
        using (TenantContext.BeginMaintenanceScope())
        {
            using var db = _contextFactory.CreateDbContext();
            var found = await db.Set<RefreshSession>()
                .FirstOrDefaultAsync(s => s.Id == sessionId, cancellationToken)
                .ConfigureAwait(false);
            if (found is null)
            {
                throw new TokenExpiredException("The refresh token is expired or invalid.");
            }

            session = found;
            if (!VerifySecret(secret, session.Salt, session.TokenHash))
            {
                throw new TokenExpiredException("The refresh token is expired or invalid.");
            }

            if (session.IsRevoked)
            {
                await RevokeFamilyAsync(db, session.FamilyId, now, cancellationToken).ConfigureAwait(false);
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                throw new TokenReusedException("The refresh token was already used.");
            }

            if (session.IsExpired(now))
            {
                throw new TokenExpiredException("The refresh token is expired or invalid.");
            }

            var user = await db.Set<TenantUser>()
                .AsNoTracking()
                .FirstOrDefaultAsync(u => u.Id == session.UserId && u.TenantId == session.TenantId, cancellationToken)
                .ConfigureAwait(false);
            if (user is null)
            {
                throw new TokenExpiredException("The refresh token is expired or invalid.");
            }

            if (user.Status == TenantUserStatus.Disabled)
            {
                throw new UserDisabledException("The user account is disabled.");
            }

            status = user.Status;
            roles = await db.Set<ProjectMembership>()
                .AsNoTracking()
                .Where(m => m.TenantId == session.TenantId && m.UserId == session.UserId)
                .Select(m => m.Role.ToString())
                .Distinct()
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            var successorId = Guid.NewGuid();
            var successorSecret = NewSecret();
            var successorSalt = NewSalt();
            var successor = new RefreshSession(
                successorId, session.TenantId, session.UserId, session.FamilyId,
                HashSecret(successorSecret, successorSalt), successorSalt,
                now.Add(RefreshLifetime), now);
            session.Revoke(now, successorId);
            db.Set<RefreshSession>().Add(successor);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            _ = status;
            var access = CreateAccessToken(session.TenantId, session.UserId, roles, now);
            _logger.LogInformation("Refresh token rotated. {TenantId} {UserId}", session.TenantId, session.UserId);
            return new TokenPair(
                access,
                FormatToken(successorId, successorSecret),
                now,
                now.Add(AccessLifetime),
                successor.ExpiresAt);
        }
    }

    /// <summary>
    /// Revokes a refresh token. Unknown, expired, or already-revoked tokens
    /// still return success (idempotent).
    /// </summary>
    public async Task LogoutAsync(
        string? refreshToken,
        string correlationId,
        CancellationToken cancellationToken = default)
    {
        var now = _time.GetUtcNow();
        if (TryParseToken(refreshToken, out var sessionId, out var secret))
        {
            using (TenantContext.BeginMaintenanceScope())
            {
                using var db = _contextFactory.CreateDbContext();
                var session = await db.Set<RefreshSession>()
                    .FirstOrDefaultAsync(s => s.Id == sessionId, cancellationToken)
                    .ConfigureAwait(false);
                if (session is not null && VerifySecret(secret, session.Salt, session.TokenHash))
                {
                    if (!session.IsRevoked)
                    {
                        session.Revoke(now);
                        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    }

                    await _audit.LogAsync(
                        session.TenantId, null, session.UserId.ToString("D"), AuditLogoutAction,
                        AuditResourceType, session.UserId.ToString("D"),
                        $"{{\"correlationId\":\"{JsonEscape(correlationId)}\"}}",
                        cancellationToken).ConfigureAwait(false);
                    _logger.LogInformation("User signed out. {TenantId} {UserId}", session.TenantId, session.UserId);
                    return;
                }
            }
        }

        _logger.LogInformation("Logout with unknown token treated as success.");
    }

    /// <summary>
    /// Creates a signed HS256 access JWT for the given identity. Pure except
    /// for the configured signing key.
    /// </summary>
    public string CreateAccessToken(Guid tenantId, Guid userId, IEnumerable<string> roles, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(roles);

        if (string.IsNullOrWhiteSpace(_auth.SigningKey) || _auth.SigningKey.Length < 32)
        {
            throw new InvalidOperationException("Auth signing key is not configured.");
        }

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_auth.SigningKey));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var claims = new List<Claim>
        {
            new(Authorization.ClaimTypes.TenantId, tenantId.ToString("D")),
            new(Authorization.ClaimTypes.Subject, userId.ToString("D")),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
            new(JwtRegisteredClaimNames.Iat, now.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture), ClaimValueTypes.Integer64),
        };
        foreach (var role in roles.Where(r => !string.IsNullOrWhiteSpace(r)).Distinct(StringComparer.Ordinal))
        {
            claims.Add(new Claim(Authorization.ClaimTypes.Roles, role.Trim()));
        }

        var token = new JwtSecurityToken(
            issuer: string.IsNullOrWhiteSpace(_auth.Authority) ? null : _auth.Authority.Trim(),
            audience: _auth.Audience,
            claims: claims,
            notBefore: now.UtcDateTime,
            expires: now.Add(AccessLifetime).UtcDateTime,
            signingCredentials: credentials);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    /// <summary>
    /// Computes the salted SHA-256 hash of a refresh secret. Pure.
    /// </summary>
    public static string HashSecret(string secret, string salt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);
        ArgumentException.ThrowIfNullOrWhiteSpace(salt);

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(string.Concat(salt, ".", secret)));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>
    /// Verifies a refresh secret against its stored hash using a
    /// constant-time comparison. Pure, never throws.
    /// </summary>
    public static bool VerifySecret(string secret, string salt, string expectedHash)
    {
        if (string.IsNullOrEmpty(secret) || string.IsNullOrEmpty(salt) || string.IsNullOrEmpty(expectedHash))
        {
            return false;
        }

        string computed;
        try
        {
            computed = HashSecret(secret, salt);
        }
        catch (ArgumentException)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(computed),
            Encoding.UTF8.GetBytes(expectedHash));
    }

    internal static bool TryParseToken(string? token, out Guid sessionId, out string secret)
    {
        sessionId = Guid.Empty;
        secret = string.Empty;
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        var trimmed = token.Trim();
        var separator = trimmed.IndexOf('.');
        if (separator <= 0 || separator >= trimmed.Length - 1)
        {
            return false;
        }

        if (!Guid.TryParseExact(trimmed[..separator], "N", out sessionId) || sessionId == Guid.Empty)
        {
            sessionId = Guid.Empty;
            return false;
        }

        secret = trimmed[(separator + 1)..];
        return !string.IsNullOrWhiteSpace(secret);
    }

    internal static string FormatToken(Guid sessionId, string secret)
    {
        return string.Concat(sessionId.ToString("N"), ".", secret);
    }

    private async Task<TokenPair> IssuePairAsync(
        Guid tenantId,
        Guid userId,
        List<string> roles,
        Guid familyId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var sessionId = Guid.NewGuid();
        var secret = NewSecret();
        var salt = NewSalt();
        var session = new RefreshSession(
            sessionId, tenantId, userId, familyId,
            HashSecret(secret, salt), salt,
            now.Add(RefreshLifetime), now);

        using (TenantContext.BeginMaintenanceScope())
        {
            using var db = _contextFactory.CreateDbContext();
            db.Set<RefreshSession>().Add(session);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        return new TokenPair(
            CreateAccessToken(tenantId, userId, roles, now),
            FormatToken(sessionId, secret),
            now,
            now.Add(AccessLifetime),
            session.ExpiresAt);
    }

    private static async Task RevokeFamilyAsync(DbContext db, Guid familyId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var members = await db.Set<RefreshSession>()
            .Where(s => s.FamilyId == familyId && !s.RevokedAt.HasValue)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        foreach (var member in members)
        {
            member.Revoke(now);
        }
    }

    private static string NewSecret()
    {
        return Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace("+", "-", StringComparison.Ordinal)
            .Replace("/", "_", StringComparison.Ordinal)
            .TrimEnd('=');
    }

    private static string NewSalt()
    {
        return Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
    }

    private static string JsonEscape(string value)
    {
        return value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
    }
}
