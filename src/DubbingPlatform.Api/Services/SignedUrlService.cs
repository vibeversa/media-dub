using System.Security.Cryptography;
using System.Text;
using DubbingPlatform.Application.Errors;
using DubbingPlatform.Application.Exceptions;
using DubbingPlatform.Application.Storage;
using DubbingPlatform.Domain.Exceptions;

namespace DubbingPlatform.Api.Services;

/// <summary>
/// Tenant-bound signed download/preview URL service (Task 037, R2–R3).
/// Issues short-lived bearer tokens carrying tenant+project+artifact claims
/// with a 15-minute default expiry (clamped to 1 minute .. 1 hour via
/// <see cref="SignedUrlPolicy"/>). Validation fails closed: unknown tenants,
/// tampered signatures, expired tokens, tenant/project mismatches, and use
/// after project archival are all rejected with <c>UNAUTHORIZED</c>,
/// <c>FORBIDDEN</c>, <c>URL_EXPIRED</c>, or <c>PROJECT_ARCHIVED</c> — never
/// data. Tokens are never logged (only issuance counts) and issuance
/// responses carry <c>Cache-Control: private, no-store</c> (see
/// <see cref="Middleware.SecurityHeadersMiddleware"/>).
/// The HMAC envelope proves the API-level tenancy boundary in hermetic tests;
/// object-store presigned URLs (S3, 15 minutes via
/// <see cref="StoragePresignedUrls"/>) remain the transport for bytes.
/// </summary>
public sealed class SignedUrlService
{
    /// <summary>Default lifetime: 15 minutes.</summary>
    public static readonly TimeSpan DefaultExpiry = TimeSpan.FromMinutes(15);

    /// <summary>Cache header applied to every issuance response.</summary>
    public const string IssuanceCacheControl = "private, no-store";

    private readonly byte[] _signingKey;

    public SignedUrlService(byte[] signingKey)
    {
        ArgumentNullException.ThrowIfNull(signingKey);
        if (signingKey.Length < 32)
        {
            throw new ArgumentException("Signing key must be at least 32 bytes.", nameof(signingKey));
        }

        _signingKey = (byte[])signingKey.Clone();
    }

    /// <summary>
    /// Issues a tenant-bound token for <paramref name="artifactId"/>.
    /// Throws <see cref="DomainException"/> on empty ids (fail closed).
    /// </summary>
    public SignedUrl Issue(
        Guid tenantId,
        Guid projectId,
        Guid artifactId,
        DateTimeOffset now,
        TimeSpan? requestedExpiry = null)
    {
        if (tenantId == Guid.Empty)
        {
            throw new DomainException("TenantId must not be empty.");
        }

        if (projectId == Guid.Empty)
        {
            throw new DomainException("ProjectId must not be empty.");
        }

        if (artifactId == Guid.Empty)
        {
            throw new DomainException("ArtifactId must not be empty.");
        }

        var expiry = requestedExpiry.HasValue
            ? SignedUrlPolicy.Resolve(requestedExpiry)
            : DefaultExpiry;

        var expiresAt = now.Add(expiry);
        var payload = BuildPayload(tenantId, projectId, artifactId, expiresAt);
        var signature = Sign(payload);
        return new SignedUrl(
            string.Concat(payload, ".", signature),
            tenantId,
            projectId,
            artifactId,
            expiresAt);
    }

    /// <summary>
    /// Validates a token for the calling tenant/project.
    /// Unknown tenant → <see cref="UnauthorizedAccessException"/> (401);
    /// tamper/mismatch → <see cref="ForbiddenException"/> (403);
    /// expiry → <see cref="ErrorCodeException"/> (<c>URL_EXPIRED</c>, 410);
    /// archived project → <see cref="ErrorCodeException"/>
    /// (<c>PROJECT_ARCHIVED</c>, 409 with reason, never silent 404).
    /// </summary>
    public SignedUrl Validate(
        string? token,
        Guid callerTenantId,
        Guid callerProjectId,
        DateTimeOffset now,
        bool projectArchived = false)
    {
        if (callerTenantId == Guid.Empty)
        {
            throw new UnauthorizedAccessException("Unknown tenant.");
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            throw new ForbiddenException("Signed URL is invalid.");
        }

        var parts = token.Trim().Split('.');
        if (parts.Length != 2)
        {
            throw new ForbiddenException("Signed URL is invalid.");
        }

        var payload = parts[0];
        var signature = parts[1];
        var expected = Sign(payload);
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(signature),
                Encoding.ASCII.GetBytes(expected)))
        {
            throw new ForbiddenException("Signed URL signature mismatch.");
        }

        SignedUrl parsed;
        try
        {
            parsed = Parse(payload, signature);
        }
        catch (FormatException ex)
        {
            throw new ForbiddenException($"Signed URL is malformed: {ex.Message}");
        }

        if (now >= parsed.ExpiresAt)
        {
            throw new ErrorCodeException(ErrorCodes.UrlExpired, "Signed URL has expired.");
        }

        if (parsed.TenantId != callerTenantId || parsed.ProjectId != callerProjectId)
        {
            throw new ForbiddenException("Signed URL does not belong to the current tenant/project.");
        }

        if (projectArchived)
        {
            throw new ErrorCodeException(ErrorCodes.ProjectArchived, "Project is archived; signed URL use is rejected.");
        }

        return parsed;
    }

    private string BuildPayload(Guid tenantId, Guid projectId, Guid artifactId, DateTimeOffset expiresAt)
    {
        var raw = string.Join(
            "|",
            tenantId.ToString("N"),
            projectId.ToString("N"),
            artifactId.ToString("N"),
            expiresAt.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture));
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(raw));
    }

    private static SignedUrl Parse(string payload, string signature)
    {
        var raw = Encoding.UTF8.GetString(Convert.FromBase64String(payload));
        var segments = raw.Split('|');
        if (segments.Length != 4)
        {
            throw new FormatException("Expected 4 payload segments.");
        }

        return new SignedUrl(
            string.Concat(payload, ".", signature),
            Guid.ParseExact(segments[0], "N"),
            Guid.ParseExact(segments[1], "N"),
            Guid.ParseExact(segments[2], "N"),
            DateTimeOffset.FromUnixTimeSeconds(long.Parse(segments[3], System.Globalization.CultureInfo.InvariantCulture)));
    }

    private string Sign(string payload)
    {
        using var hmac = new HMACSHA256(_signingKey);
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
        return Convert.ToBase64String(hash);
    }
}

/// <summary>
/// Issued tenant-bound signed URL (token + decoded claims).
/// The token string itself is secret material: never log it.
/// </summary>
public sealed record SignedUrl(
    string Token,
    Guid TenantId,
    Guid ProjectId,
    Guid ArtifactId,
    DateTimeOffset ExpiresAt);
