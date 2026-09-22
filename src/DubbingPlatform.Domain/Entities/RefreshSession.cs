using DubbingPlatform.Domain.Exceptions;

namespace DubbingPlatform.Domain.Entities;

/// <summary>
/// Server-side refresh-token session. Only a SHA-256 hash (salted) is stored;
/// the raw token is never persisted or logged. Tokens rotate per refresh:
/// the presenting session is revoked and a same-family successor issued. When
/// a revoked token is presented again (reuse), the whole family is revoked
/// (theft detection). The opaque wire token is
/// <c>{sessionId:N}.{secret}</c> so verification is a single indexed lookup.
/// </summary>
public sealed class RefreshSession
{
    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid UserId { get; private set; }

    public Guid FamilyId { get; private set; }

    public string TokenHash { get; private set; }

    public string Salt { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? RevokedAt { get; private set; }

    public Guid? ReplacedById { get; private set; }

    private RefreshSession()
    {
        TokenHash = string.Empty;
        Salt = string.Empty;
    }

    public RefreshSession(
        Guid id,
        Guid tenantId,
        Guid userId,
        Guid familyId,
        string tokenHash,
        string salt,
        DateTimeOffset expiresAt,
        DateTimeOffset createdAt,
        DateTimeOffset? revokedAt = null,
        Guid? replacedById = null)
    {
        Id = id;
        TenantId = tenantId;
        UserId = userId;
        FamilyId = familyId;
        TokenHash = tokenHash;
        Salt = salt;
        ExpiresAt = expiresAt;
        CreatedAt = createdAt;
        RevokedAt = revokedAt;
        ReplacedById = replacedById;

        Validate();
    }

    public void Validate()
    {
        if (Id == Guid.Empty)
        {
            throw new DomainException("RefreshSession Id must not be empty.");
        }

        if (TenantId == Guid.Empty)
        {
            throw new DomainException("RefreshSession TenantId must not be empty.");
        }

        if (UserId == Guid.Empty)
        {
            throw new DomainException("RefreshSession UserId must not be empty.");
        }

        if (FamilyId == Guid.Empty)
        {
            throw new DomainException("RefreshSession FamilyId must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(TokenHash))
        {
            throw new DomainException("RefreshSession TokenHash must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(Salt))
        {
            throw new DomainException("RefreshSession Salt must not be empty.");
        }

        if (ExpiresAt <= CreatedAt)
        {
            throw new DomainException("RefreshSession ExpiresAt must be after CreatedAt.");
        }

        if (RevokedAt.HasValue && ReplacedById.HasValue && ReplacedById.Value == Guid.Empty)
        {
            throw new DomainException("RefreshSession ReplacedById must not be empty when set.");
        }
    }

    public bool IsExpired(DateTimeOffset now)
    {
        return ExpiresAt <= now;
    }

    public bool IsRevoked => RevokedAt.HasValue;

    public void Revoke(DateTimeOffset revokedAt, Guid? replacedById = null)
    {
        RevokedAt ??= revokedAt;
        ReplacedById ??= replacedById;
        Validate();
    }
}
