using System.ComponentModel.DataAnnotations;
using System.Text.Json;

namespace DubbingPlatform.Api.Models;

/// <summary>
/// Login request. Example:
/// <c>{ "tenantId": "3fa85f64-5717-4562-b3fc-2c963f66afa6", "externalSubject": "auth0|abc123" }</c>.
/// Idempotency-Key is not used here: every login mints a fresh token pair by
/// design (replay must not return the same refresh secret).
/// </summary>
public sealed class LoginRequest
{
    [Required]
    public Guid TenantId { get; set; }

    [Required]
    [MinLength(1)]
    [MaxLength(256)]
    public string ExternalSubject { get; set; } = string.Empty;
}

/// <summary>
/// Refresh request. Example: <c>{ "refreshToken": "abcdef...0123.ghijkl..." }</c>.
/// Idempotency-Key is not used here: refresh rotates by design; retrying with
/// the same token after success is reuse and revokes the family.
/// </summary>
public sealed class RefreshRequest
{
    [Required]
    [MinLength(1)]
    [MaxLength(2048)]
    public string RefreshToken { get; set; } = string.Empty;
}

/// <summary>
/// Logout request. The token is optional: unknown, expired, or absent tokens
/// still return 200 (idempotent).
/// </summary>
public sealed class LogoutRequest
{
    [MaxLength(2048)]
    public string? RefreshToken { get; set; }
}

/// <summary>
/// Token-pair response. <c>tokenType</c> is always <c>Bearer</c>;
/// <c>expiresIn</c> is access-token seconds.
/// </summary>
public sealed record TokenResponse(
    string AccessToken,
    string RefreshToken,
    string TokenType,
    long ExpiresIn,
    DateTimeOffset IssuedAt,
    DateTimeOffset AccessExpiresAt,
    DateTimeOffset RefreshExpiresAt);

/// <summary>
/// Authenticated identity response for <c>GET /api/v1/me</c>. Example roles:
/// <c>["ProjectOwner"]</c>; permissions are the 12 UX-hint strings.
/// </summary>
public sealed record MeResponse(
    MeUser User,
    MeTenant Tenant,
    IReadOnlyList<string> Roles,
    IReadOnlyList<string> Permissions,
    string Locale,
    MeFeatureFlags FeatureFlags,
    MeSession Session);

/// <summary>User identity slice.</summary>
public sealed record MeUser(
    Guid Id,
    string Email,
    string DisplayName,
    string ExternalSubject,
    string Status);

/// <summary>Tenant slice (resolved from the caller's claim; no override accepted).</summary>
public sealed record MeTenant(Guid Id, string Name, string Slug);

/// <summary>Feature flags slice.</summary>
public sealed record MeFeatureFlags(
    bool VideoIntelligenceEnabled,
    bool LipSyncEnabled,
    bool LocalInferenceEnabled);

/// <summary>Session slice from the presenting access token.</summary>
public sealed record MeSession(DateTimeOffset IssuedAt, DateTimeOffset ExpiresAt);

/// <summary>
/// Preferences response. Values are raw JSON (round-trip exactly).
/// Example: <c>{ "preferences": { "locale": "en-US", "theme": "dark" } }</c>.
/// </summary>
public sealed class PreferencesResponse
{
    public Dictionary<string, JsonElement> Preferences { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>
/// Preferences update. Unknown keys → 400 <c>PREFERENCE_KEY_UNKNOWN</c>;
/// values serializing beyond 4096 UTF-8 bytes → 413
/// <c>PREFERENCE_VALUE_TOO_LARGE</c>. Concurrent writes are last-writer-wins
/// per key (no 409). Idempotency-Key is not used: PUT is naturally idempotent
/// on identical bodies and last-writer-wins otherwise.
/// </summary>
public sealed class UpdatePreferencesRequest
{
    public Dictionary<string, JsonElement> Preferences { get; set; } = new(StringComparer.Ordinal);
}
