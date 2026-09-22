using DubbingPlatform.Application.Diagnostics.Dto;
using DubbingPlatform.Application.MultiTenancy;
using DubbingPlatform.Application.Services;
using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DubbingPlatform.Application.Diagnostics;

/// <summary>
/// Read-only lease and orphan diagnostics over Plan A runtime state: stale
/// stage-execution leases (running, older than the configured TTL, heartbeat
/// missed) and orphaned content objects (no owning artifact, media asset, or
/// generated-audio reference). Orphan scans are paginated (default 50, max
/// 200) with an opaque skip cursor plus <c>hasMore</c>. Lease tokens are never
/// surfaced; storage keys (internal paths) are never surfaced. All queries
/// are <c>AsNoTracking</c>; no writes.
/// </summary>
public sealed class LeaseOrphanService
{
    private readonly IStageExecutionContextFactory _contextFactory;
    private readonly DiagnosticsOptions _options;
    private readonly IDiagnosticsAccessChecker _access;
    private readonly ILogger<LeaseOrphanService> _logger;
    private readonly TimeProvider _time;

    public LeaseOrphanService(
        IStageExecutionContextFactory contextFactory,
        IOptions<DiagnosticsOptions> diagnosticsOptions,
        IDiagnosticsAccessChecker access,
        ILogger<LeaseOrphanService> logger,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        ArgumentNullException.ThrowIfNull(diagnosticsOptions);
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(logger);
        _contextFactory = contextFactory;
        _options = diagnosticsOptions.Value;
        _access = access;
        _logger = logger;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Determines whether a running lease is stale: the lease age reached the
    /// configured TTL and the heartbeat was missed (lease expired). A missing
    /// start time counts as stale once the lease expired. Pure.
    /// </summary>
    public static bool IsStaleLease(DateTimeOffset? startedAt, DateTimeOffset leaseExpiresAt, DateTimeOffset now, TimeSpan ttl)
    {
        if (leaseExpiresAt > now)
        {
            return false;
        }

        if (!startedAt.HasValue)
        {
            return true;
        }

        return now - startedAt.Value >= ttl;
    }

    /// <summary>
    /// Builds the owner hint for a stale lease: run/project ids only.
    /// Pure. Never includes lease tokens.
    /// </summary>
    public static string BuildOwnerHint(Guid processingRunId, Guid projectId)
    {
        return string.Concat("run:", processingRunId.ToString("N"), ";project:", projectId.ToString("N"));
    }

    /// <summary>
    /// Returns stale running leases for the tenant, oldest expiry first.
    /// <c>UpdatedAt</c> is the last-heartbeat proxy: lease renewals persist
    /// through the conditional-update path, which bumps the row.
    /// </summary>
    public async Task<IReadOnlyList<StaleLeaseDto>> GetStaleLeasesAsync(
        Guid tenantId,
        Guid userId,
        string? correlationId = null,
        CancellationToken cancellationToken = default)
    {
        await _access.RequireDiagnosticsViewerAsync(tenantId, userId, cancellationToken).ConfigureAwait(false);
        var correlation = DiagnosticsCorrelation.Normalize(correlationId);
        var now = _time.GetUtcNow();
        var ttl = _options.StaleLeaseTtl;

        List<StaleLeaseDto> stale;
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            var running = await db.Set<StageExecution>()
                .AsNoTracking()
                .Where(e => e.Status == StageStatus.Running)
                .OrderBy(e => e.LeaseExpiresAt)
                .Select(e => new
                {
                    e.Id,
                    e.StageType,
                    e.Status,
                    e.ProjectId,
                    e.ProcessingRunId,
                    e.StartedAt,
                    e.LeaseExpiresAt,
                    e.UpdatedAt,
                })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            stale = running
                .Where(e => IsStaleLease(e.StartedAt, e.LeaseExpiresAt, now, ttl))
                .Select(e => new StaleLeaseDto(
                    correlation,
                    e.Id,
                    e.StageType.ToString(),
                    e.Status.ToString(),
                    e.ProjectId,
                    e.ProcessingRunId,
                    BuildOwnerHint(e.ProcessingRunId, e.ProjectId),
                    e.StartedAt ?? e.UpdatedAt,
                    e.LeaseExpiresAt,
                    e.UpdatedAt))
                .ToList();
        }

        _logger.LogInformation(
            "Diagnostics stale leases queried. {CorrelationId} {TenantId} {StaleCount}",
            correlation,
            tenantId,
            stale.Count);
        return stale;
    }

    /// <summary>
    /// Returns one page of orphaned content objects (no owning artifact, media
    /// asset, or generated-audio reference), oldest first. The cursor is an
    /// opaque skip count; null starts at the beginning.
    /// </summary>
    public async Task<OrphanArtifactPage> GetOrphanArtifactsAsync(
        Guid tenantId,
        Guid userId,
        int? pageSize = null,
        string? cursor = null,
        string? correlationId = null,
        CancellationToken cancellationToken = default)
    {
        await _access.RequireDiagnosticsViewerAsync(tenantId, userId, cancellationToken).ConfigureAwait(false);
        var correlation = DiagnosticsCorrelation.Normalize(correlationId);
        var take = NormalizePageSize(pageSize, _options);
        var skip = ParseCursor(cursor);

        List<OrphanArtifactDto> items;
        bool hasMore;
        string? nextCursor;
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            var orphans = db.Set<ContentObject>()
                .AsNoTracking()
                .Where(c => !db.Set<Artifact>().Any(a => a.ContentObjectId == c.Id)
                    && !db.Set<MediaAsset>().Any(m => m.ContentObjectId == c.Id)
                    && !db.Set<GeneratedAudioArtifact>().Any(g => g.ContentObjectId == c.Id))
                .OrderBy(c => c.CreatedAt)
                .ThenBy(c => c.Id)
                .Skip(skip)
                .Take(take + 1);

            var window = await orphans.ToListAsync(cancellationToken).ConfigureAwait(false);
            hasMore = window.Count > take;
            items = window
                .Take(take)
                .Select(c => new OrphanArtifactDto(
                    correlation,
                    c.Id,
                    c.SizeBytes,
                    c.MediaFormat,
                    string.IsNullOrWhiteSpace(c.ContentHash) ? null : c.ContentHash,
                    c.CreatedAt,
                    c.LastReferencedAt))
                .ToList();
            nextCursor = hasMore ? (skip + take).ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
        }

        _logger.LogInformation(
            "Diagnostics orphan artifacts queried. {CorrelationId} {TenantId} {ItemCount} {HasMore}",
            correlation,
            tenantId,
            items.Count,
            hasMore);
        return new OrphanArtifactPage(items, nextCursor, hasMore);
    }

    /// <summary>
    /// Clamps the requested page size to the configured default/max. Pure.
    /// </summary>
    public static int NormalizePageSize(int? requested, DiagnosticsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!requested.HasValue || requested.Value <= 0)
        {
            return options.OrphanPageDefaultSize;
        }

        return Math.Min(requested.Value, options.OrphanPageMaxSize);
    }

    /// <summary>
    /// Parses the opaque skip cursor. Pure. Missing or invalid cursors restart
    /// at the beginning rather than failing the read.
    /// </summary>
    public static int ParseCursor(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return 0;
        }

        if (int.TryParse(cursor.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var skip) && skip >= 0)
        {
            return skip;
        }

        return 0;
    }
}

/// <summary>
/// One orphan-artifact page: items plus the opaque cursor for the next page
/// (null when complete) and whether more rows remain.
/// </summary>
public sealed record OrphanArtifactPage(
    IReadOnlyList<Dto.OrphanArtifactDto> Items,
    string? NextCursor,
    bool HasMore);
