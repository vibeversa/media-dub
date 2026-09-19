using DubbingPlatform.Application.Exceptions;
using DubbingPlatform.Application.MultiTenancy;
using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Domain.Exceptions;
using Microsoft.EntityFrameworkCore;

namespace DubbingPlatform.Application.Services;

/// <summary>
/// Race-safe idempotency ledger over <see cref="IdempotencyRecord"/>.
/// Claim inserts atomically (<c>INSERT ... ON CONFLICT (tenant,endpoint,key)
/// DO NOTHING</c>); the unique index serializes concurrent duplicates so exactly
/// one claimant proceeds while the rest replay or fail. Semantics:
/// same key + same request hash + <c>Succeeded</c> → replay stored response;
/// same key + different hash → 409 CONFLICT; <c>Started</c> younger than
/// <see cref="InProgressWindow"/> → 409 CONFLICT with Retry-After
/// (<see cref="IdempotencyInProgressException"/>); stale <c>Started</c>,
/// <c>Failed</c>, or expired rows → treated as a new claim (row is reset).
/// Response bodies are stored verbatim (status + JSON); callers must not place
/// secrets in them. Retention is set per endpoint class via
/// <see cref="IdempotencyRetention"/>.
/// </summary>
public sealed class IdempotencyService
{
    /// <summary>
    /// In-flight window: a <c>Started</c> row younger than this yields 409+Retry-After.
    /// </summary>
    public static readonly TimeSpan InProgressWindow = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Header name for idempotency keys.
    /// </summary>
    public const string HeaderName = "Idempotency-Key";

    private readonly IStageExecutionContextFactory _contextFactory;

    public IdempotencyService(IStageExecutionContextFactory contextFactory)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        _contextFactory = contextFactory;
    }

    /// <summary>
    /// Attempts to claim <c>(tenant, endpoint, key)</c> for <c>requestHash</c>.
    /// Returns <c>(IsReplay: false)</c> when this caller owns the claim and must
    /// execute then call <see cref="CompleteAsync"/>; <c>(IsReplay: true, Response)</c>
    /// when a prior success must be replayed without re-executing.
    /// </summary>
    public async Task<(bool IsReplay, string? Response)> TryClaimAsync(
        Guid tenantId,
        string endpoint,
        string key,
        string requestHash,
        TimeSpan expiry,
        CancellationToken cancellationToken = default)
    {
        RequireTenant(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestHash);
        if (expiry <= TimeSpan.Zero)
        {
            throw new DomainException("Expiry must be positive.");
        }

        var normalizedEndpoint = endpoint.Trim();
        var normalizedKey = key.Trim();
        var now = DateTimeOffset.UtcNow;
        var expiresAt = now.Add(expiry);

        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();

            var inserted = await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO idempotency_records (id, tenant_id, endpoint, idempotency_key, request_hash, state, created_at, expires_at) " +
                "VALUES ({0}, {1}, {2}, {3}, {4}, 'Started', {5}, {6}) " +
                "ON CONFLICT (tenant_id, endpoint, idempotency_key) DO NOTHING",
                Guid.NewGuid(),
                tenantId,
                normalizedEndpoint,
                normalizedKey,
                requestHash,
                now,
                expiresAt).ConfigureAwait(false);

            if (inserted == 1)
            {
                return (false, null);
            }

            db.ChangeTracker.Clear();
            var existing = await db.Set<IdempotencyRecord>()
                .FirstOrDefaultAsync(
                    r => r.TenantId == tenantId && r.Endpoint == normalizedEndpoint && r.IdempotencyKey == normalizedKey,
                    cancellationToken).ConfigureAwait(false);

            if (existing is null)
            {
                throw new ConflictException($"Idempotency key '{normalizedKey}' is already claimed for '{normalizedEndpoint}'.");
            }

            if (existing.ExpiresAt < now)
            {
                await ResetToStartedAsync(db, existing, requestHash, now, expiresAt).ConfigureAwait(false);
                return (false, null);
            }

            if (!string.Equals(existing.RequestHash, requestHash, StringComparison.Ordinal))
            {
                throw new ConflictException($"Idempotency key '{normalizedKey}' was already used with a different request body.");
            }

            if (string.Equals(existing.State, "Succeeded", StringComparison.Ordinal))
            {
                return (true, existing.ResponseBody);
            }

            if (string.Equals(existing.State, "Failed", StringComparison.Ordinal))
            {
                await ResetToStartedAsync(db, existing, requestHash, now, expiresAt).ConfigureAwait(false);
                return (false, null);
            }

            if (string.Equals(existing.State, "Started", StringComparison.Ordinal))
            {
                var age = now - existing.CreatedAt;
                if (age < InProgressWindow)
                {
                    var retryAfter = Math.Max(1, (int)(InProgressWindow - age).TotalSeconds);
                    throw new IdempotencyInProgressException(
                        $"Idempotency key '{normalizedKey}' is already in progress for '{normalizedEndpoint}'. Retry after {retryAfter}s.",
                        retryAfter);
                }

                await ResetToStartedAsync(db, existing, requestHash, now, expiresAt).ConfigureAwait(false);
                return (false, null);
            }

            throw new ConflictException($"Idempotency key '{normalizedKey}' is in state '{existing.State}'.");
        }
    }

    /// <summary>
    /// Completes a claimed row as <c>Succeeded</c> with the canonical response.
    /// </summary>
    public async Task CompleteAsync(
        Guid tenantId,
        string endpoint,
        string key,
        int statusCode,
        string responseBody,
        CancellationToken cancellationToken = default)
    {
        RequireTenant(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(responseBody);
        if (statusCode < 200 || statusCode > 599)
        {
            throw new DomainException("StatusCode must be a valid HTTP status.");
        }

        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            await db.Database.ExecuteSqlRawAsync(
                "UPDATE idempotency_records SET state = 'Succeeded', response_status = {0}, response_body = {1} " +
                "WHERE tenant_id = {2} AND endpoint = {3} AND idempotency_key = {4}",
                statusCode.ToString(System.Globalization.CultureInfo.InvariantCulture),
                responseBody,
                tenantId,
                endpoint.Trim(),
                key.Trim()).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Marks a claimed row as <c>Failed</c> so the next attempt with the same
    /// key treats it as a new claim.
    /// </summary>
    public async Task FailAsync(
        Guid tenantId,
        string endpoint,
        string key,
        CancellationToken cancellationToken = default)
    {
        RequireTenant(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            await db.Database.ExecuteSqlRawAsync(
                "UPDATE idempotency_records SET state = 'Failed' " +
                "WHERE tenant_id = {0} AND endpoint = {1} AND idempotency_key = {2}",
                tenantId,
                endpoint.Trim(),
                key.Trim()).ConfigureAwait(false);
        }
    }

    private static async Task ResetToStartedAsync(
        DbContext db,
        IdempotencyRecord existing,
        string requestHash,
        DateTimeOffset now,
        DateTimeOffset expiresAt)
    {
        await db.Database.ExecuteSqlRawAsync(
            "UPDATE idempotency_records SET request_hash = {0}, state = 'Started', created_at = {1}, expires_at = {2}, response_status = NULL, response_body = NULL " +
            "WHERE tenant_id = {3} AND endpoint = {4} AND idempotency_key = {5}",
            requestHash,
            now,
            expiresAt,
            existing.TenantId,
            existing.Endpoint,
            existing.IdempotencyKey).ConfigureAwait(false);
        db.ChangeTracker.Clear();
    }

    private static void RequireTenant(Guid tenantId)
    {
        if (tenantId == Guid.Empty)
        {
            throw new DomainException("TenantId must not be empty.");
        }
    }
}
