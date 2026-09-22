using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DubbingPlatform.Application.MultiTenancy;
using DubbingPlatform.Application.Services;
using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Domain.Enums;
using DubbingPlatform.Domain.Exceptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DubbingPlatform.Application.Previews;

/// <summary>
/// Attaches <c>QcEvidenceArtifact</c> references (clip range plus peak slice)
/// to QC issues. Evidence payloads are stored as tenant-scoped
/// <c>ContentObject</c>-backed artifacts; the owning <c>QualityResult</c> row
/// keeps an <c>evidence</c> map inside <c>DetailsJson</c> keyed by artifact
/// kind plus the latest evidence artifact id. Re-runs are deduplicated on
/// <c>(TenantId, QcIssueId, ArtifactKind)</c>: an existing kind returns the
/// stored artifact without new rows. Cross-tenant issue ids read as 404
/// without leaking existence. Only ids, ranges, and kinds are logged or
/// persisted — never media bytes or secrets.
/// </summary>
public sealed class QcEvidenceLinker
{
    /// <summary>Maximum artifact-kind length.</summary>
    public const int MaxKindLength = 64;

    /// <summary>Maximum peak-slice descriptor length.</summary>
    public const int MaxPeakSliceLength = 256;

    /// <summary>Audit action appended per new evidence link.</summary>
    public const string AuditAction = "qc.evidence_linked";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IStageExecutionContextFactory _contextFactory;
    private readonly ArtifactService _artifacts;
    private readonly AuditService _audit;
    private readonly ILogger<QcEvidenceLinker> _logger;

    public QcEvidenceLinker(
        IStageExecutionContextFactory contextFactory,
        ArtifactService artifacts,
        AuditService audit,
        ILogger<QcEvidenceLinker> logger)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        ArgumentNullException.ThrowIfNull(artifacts);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(logger);
        _contextFactory = contextFactory;
        _artifacts = artifacts;
        _audit = audit;
        _logger = logger;
    }

    /// <summary>
    /// Validates a clip range (both bounds set or both unset; start &lt;= end,
    /// non-negative). Pure.
    /// </summary>
    public static void RequireClipRange(int? clipStartMs, int? clipEndMs)
    {
        if (!clipStartMs.HasValue && !clipEndMs.HasValue)
        {
            return;
        }

        if (!clipStartMs.HasValue || !clipEndMs.HasValue)
        {
            throw new DomainException("Clip range requires both ClipStartMs and ClipEndMs.");
        }

        if (clipStartMs.Value < 0 || clipEndMs.Value < 0)
        {
            throw new DomainException("Clip range bounds must be >= 0.");
        }

        if (clipStartMs.Value > clipEndMs.Value)
        {
            throw new DomainException("ClipStartMs must not exceed ClipEndMs.");
        }
    }

    /// <summary>
    /// Links evidence to a QC issue, returning the new or existing artifact.
    /// </summary>
    public async Task<Artifact> LinkAsync(
        Guid tenantId,
        Guid qcIssueId,
        string artifactKind,
        int? clipStartMs = null,
        int? clipEndMs = null,
        string? peakSlice = null,
        CancellationToken cancellationToken = default)
    {
        RequireTenant(tenantId);
        RequireId(qcIssueId, nameof(qcIssueId));
        var kind = RequireKind(artifactKind);
        RequireClipRange(clipStartMs, clipEndMs);
        var slice = NormalizeSlice(peakSlice);

        var issue = await LoadIssueAsync(tenantId, qcIssueId, cancellationToken).ConfigureAwait(false);

        var existingId = ExtractEvidenceArtifactId(issue.DetailsJson, kind);
        if (existingId.HasValue)
        {
            var reused = await LoadArtifactAsync(tenantId, existingId.Value, cancellationToken).ConfigureAwait(false);
            if (reused is not null)
            {
                return reused;
            }
        }

        var now = DateTimeOffset.UtcNow;
        var evidenceJson = JsonSerializer.Serialize(new
        {
            qcIssueId = qcIssueId.ToString("N"),
            artifactKind = kind,
            clipStartMs,
            clipEndMs,
            peakSlice = slice,
            linkedAt = now,
        }, JsonOptions);

        PublishResult published;
        using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(evidenceJson), writable: false))
        {
            published = await _artifacts.PublishAsync(
                tenantId, issue.ProjectId, issue.ProcessingRunId,
                StageType.QualityControl, ArtifactType.QcEvidenceArtifact,
                stream, ".json", "application/json",
                "preview", "preview-v1", null, null,
                issue.ArtifactId.HasValue ? [issue.ArtifactId.Value] : [],
                null,
                cancellationToken).ConfigureAwait(false);
        }

        var metadataJson = JsonSerializer.Serialize(new
        {
            qcIssueId = qcIssueId.ToString("N"),
            artifactKind = kind,
            clipStartMs,
            clipEndMs,
            peakSlice = slice,
        }, JsonOptions);

        var mergedDetails = MergeEvidence(issue.DetailsJson, kind, published.ArtifactId);
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            await db.Database.ExecuteSqlRawAsync(
                "UPDATE quality_results SET details_json = {0}, artifact_id = {1} WHERE id = {2} AND tenant_id = {3}",
                mergedDetails, published.ArtifactId, qcIssueId, tenantId).ConfigureAwait(false);
            await db.Database.ExecuteSqlRawAsync(
                "UPDATE artifacts SET metadata_json = {0} WHERE id = {1} AND tenant_id = {2}",
                metadataJson, published.ArtifactId, tenantId).ConfigureAwait(false);
        }

        await _audit.LogAsync(
            tenantId, issue.ProjectId, "qc-evidence-linker", AuditAction,
            "QualityResult", qcIssueId.ToString("N"),
            Application.Security.SecretRedactor.Redact(JsonSerializer.Serialize(new
            {
                qcIssueId = qcIssueId.ToString("N"),
                artifactKind = kind,
                artifactId = published.ArtifactId.ToString("N"),
                clipStartMs,
                clipEndMs,
            }, JsonOptions)),
            cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "QC evidence {Kind} linked to issue {IssueId} for tenant {TenantId}.",
            kind, qcIssueId, tenantId);

        var created = await LoadArtifactAsync(tenantId, published.ArtifactId, cancellationToken).ConfigureAwait(false);
        return created!;
    }

    private static string RequireKind(string? kind)
    {
        var trimmed = kind?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            throw new DomainException("ArtifactKind must not be empty.");
        }

        if (trimmed.Length > MaxKindLength)
        {
            throw new DomainException("ArtifactKind must be at most 64 chars.");
        }

        return trimmed;
    }

    private static string? NormalizeSlice(string? slice)
    {
        if (string.IsNullOrWhiteSpace(slice))
        {
            return null;
        }

        var trimmed = slice.Trim();
        if (trimmed.Length > MaxPeakSliceLength)
        {
            throw new DomainException("PeakSlice must be at most 256 chars.");
        }

        return trimmed;
    }

    private async Task<QualityResult> LoadIssueAsync(
        Guid tenantId,
        Guid qcIssueId,
        CancellationToken cancellationToken)
    {
        QualityResult? issue;
        using (TenantContext.BeginMaintenanceScope())
        {
            using var db = _contextFactory.CreateDbContext();
            issue = await db.Set<QualityResult>()
                .AsNoTracking()
                .FirstOrDefaultAsync(q => q.Id == qcIssueId, cancellationToken)
                .ConfigureAwait(false);
        }

        if (issue is null || issue.TenantId != tenantId)
        {
            throw new Application.Exceptions.NotFoundException($"QC issue '{qcIssueId:D}' was not found.");
        }

        return issue;
    }

    private async Task<Artifact?> LoadArtifactAsync(
        Guid tenantId,
        Guid artifactId,
        CancellationToken cancellationToken)
    {
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            return await db.Set<Artifact>()
                .AsNoTracking()
                .FirstOrDefaultAsync(a => a.Id == artifactId, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static Guid? ExtractEvidenceArtifactId(string? detailsJson, string kind)
    {
        if (string.IsNullOrWhiteSpace(detailsJson))
        {
            return null;
        }

        try
        {
            var root = JsonNode.Parse(detailsJson) as JsonObject;
            var evidence = root?["evidence"] as JsonObject;
            var value = evidence?[kind]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(value)
                && Guid.TryParseExact(value, "N", out var id)
                && id != Guid.Empty)
            {
                return id;
            }
        }
#pragma warning disable CA1031 // Evidence map is best-effort: unparsable details are treated as having no evidence.
        catch (JsonException)
#pragma warning restore CA1031
        {
            return null;
        }

        return null;
    }

    private static string MergeEvidence(string? detailsJson, string kind, Guid artifactId)
    {
        JsonObject root;
        if (string.IsNullOrWhiteSpace(detailsJson))
        {
            root = new JsonObject();
        }
        else
        {
            try
            {
                root = JsonNode.Parse(detailsJson) as JsonObject ?? new JsonObject();
            }
#pragma warning disable CA1031 // Malformed details are replaced: the evidence map is authoritative for linking.
            catch (JsonException)
#pragma warning restore CA1031
            {
                root = new JsonObject();
            }
        }

        var evidence = root["evidence"] as JsonObject ?? new JsonObject();
        evidence[kind] = artifactId.ToString("N");
        root["evidence"] = evidence;
        return root.ToJsonString();
    }

    private static void RequireTenant(Guid tenantId)
    {
        if (tenantId == Guid.Empty)
        {
            throw new DomainException("TenantId must not be empty.");
        }
    }

    private static void RequireId(Guid id, string name)
    {
        if (id == Guid.Empty)
        {
            throw new DomainException(string.Concat(name, " must not be empty."));
        }
    }
}
