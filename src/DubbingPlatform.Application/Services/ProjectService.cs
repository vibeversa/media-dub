using System.Text.Json;
using System.Text.Json.Nodes;
using DubbingPlatform.Application.Common;
using DubbingPlatform.Application.Configuration;
using DubbingPlatform.Application.Exceptions;
using DubbingPlatform.Application.MultiTenancy;
using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Domain.Enums;
using DubbingPlatform.Domain.Exceptions;
using Microsoft.EntityFrameworkCore;

namespace DubbingPlatform.Application.Services;

/// <summary>
/// Project lifecycle service. Creation validates ISO 639 language codes
/// (2-3 ASCII letters, different), normalizes settings JSON (empty becomes
/// <c>{}</c>, invalid JSON fails), computes the deterministic configuration
/// hash, inserts the <c>DubbingProject</c> (<c>Created</c>) plus a default
/// <c>ProcessingPolicy</c> when the tenant has none, and appends an audit
/// event. Deletion is logical via the <c>IsDeleted</c> EF shadow property
/// (default <c>false</c>, set by <see cref="DeleteAsync"/>; physical removal
/// and artifact dereference belong to Task 037 retention). Reads exclude
/// soft-deleted rows; <see cref="GetAsync"/> distinguishes 404 (missing or
/// deleted) from 403 (tenant mismatch) via a maintenance-scope load.
/// </summary>
public sealed class ProjectService
{
    private readonly IStageExecutionContextFactory _contextFactory;
    private readonly AuditService _audit;

    public ProjectService(IStageExecutionContextFactory contextFactory, AuditService audit)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        ArgumentNullException.ThrowIfNull(audit);
        _contextFactory = contextFactory;
        _audit = audit;
    }

    /// <summary>
    /// Creates a project and returns it.
    /// </summary>
    public async Task<DubbingProject> CreateAsync(
        Guid tenantId,
        string sourceLanguage,
        string targetLanguage,
        string? settingsJson,
        string actor,
        CancellationToken cancellationToken = default)
    {
        RequireTenant(tenantId);
        var source = ValidateLanguage(sourceLanguage, "SourceLanguage");
        var target = ValidateLanguage(targetLanguage, "TargetLanguage");
        if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
        {
            throw new DomainException("SourceLanguage and TargetLanguage must differ.");
        }

        var normalizedSettings = NormalizeSettings(settingsJson);
        var configurationHash = ComputeConfigurationHash(source, target, normalizedSettings);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);

        var now = DateTimeOffset.UtcNow;
        var projectId = Guid.NewGuid();
        var project = new DubbingProject(
            projectId, tenantId, source, target,
            ProjectStatus.Created, normalizedSettings, configurationHash,
            null, null, now, now);

        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            db.Set<DubbingProject>().Add(project);
            await EnsureDefaultPolicyAsync(db, tenantId, now, cancellationToken).ConfigureAwait(false);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        await _audit.LogAsync(
            tenantId, projectId, actor.Trim(), "project.create",
            "project", projectId.ToString("N"), null, cancellationToken).ConfigureAwait(false);

        return project;
    }

    /// <summary>
    /// Lists non-deleted projects for the tenant, ordered by creation time.
    /// </summary>
    public async Task<(IReadOnlyList<DubbingProject> Items, long Total)> ListAsync(
        Guid tenantId,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        RequireTenant(tenantId);
        var normalized = new PaginationParams { Page = page, PageSize = pageSize };
        normalized.Normalize();
        var (safePage, safeSize) = normalized.Normalized();

        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            var scoped = TenantQueryFilter.ApplyTenant(db.Set<DubbingProject>().AsNoTracking(), tenantId)
                .Where(p => EF.Property<bool>(p, "IsDeleted") == false);
            var total = await scoped.LongCountAsync(cancellationToken).ConfigureAwait(false);
            var items = await scoped
                .OrderBy(p => p.CreatedAt)
                .Skip((safePage - 1) * safeSize)
                .Take(safeSize)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            return (items, total);
        }
    }

    /// <summary>
    /// Gets a project. Throws <see cref="NotFoundException"/> (404 NOT_FOUND)
    /// when missing or soft-deleted, <see cref="ForbiddenException"/> (403)
    /// when the row belongs to another tenant.
    /// </summary>
    public async Task<DubbingProject> GetAsync(
        Guid tenantId,
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        RequireTenant(tenantId);
        RequireId(projectId, nameof(projectId));

        DubbingProject? project;
        bool isDeleted;
        using (TenantContext.BeginMaintenanceScope())
        {
            using var db = _contextFactory.CreateDbContext();
            project = await db.Set<DubbingProject>()
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken).ConfigureAwait(false);
            if (project is null)
            {
                throw new NotFoundException($"Project '{projectId}' was not found.");
            }

            isDeleted = await db.Set<DubbingProject>()
                .Where(p => p.Id == projectId)
                .Select(p => EF.Property<bool>(p, "IsDeleted"))
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        }

        if (project.TenantId != tenantId)
        {
            throw new ForbiddenException($"Project '{projectId}' does not belong to the current tenant.");
        }

        if (isDeleted)
        {
            throw new NotFoundException($"Project '{projectId}' was not found.");
        }

        return project;
    }

    /// <summary>
    /// Logically deletes a project (sets <c>IsDeleted</c>) and audits.
    /// Returns 202 semantics to callers; subsequent reads treat the row as missing.
    /// </summary>
    public async Task DeleteAsync(
        Guid tenantId,
        Guid projectId,
        string actor,
        CancellationToken cancellationToken = default)
    {
        RequireTenant(tenantId);
        RequireId(projectId, nameof(projectId));
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);

        // Ownership first (maintenance load distinguishes 404 vs 403).
        var project = await GetAsync(tenantId, projectId, cancellationToken).ConfigureAwait(false);

        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            var attached = await db.Set<DubbingProject>()
                .FirstOrDefaultAsync(p => p.Id == project.Id, cancellationToken).ConfigureAwait(false);
            if (attached is null)
            {
                throw new NotFoundException($"Project '{projectId}' was not found.");
            }

            db.Entry(attached).Property("IsDeleted").CurrentValue = true;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        await _audit.LogAsync(
            tenantId, projectId, actor.Trim(), "project.delete",
            "project", projectId.ToString("N"), null, cancellationToken).ConfigureAwait(false);
    }

    internal static string ValidateLanguage(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new DomainException($"{name} must be a 2-3 letter code.");
        }

        var trimmed = value.Trim();
        if (trimmed.Length is < 2 or > 3 || !IsLetters(trimmed))
        {
            throw new DomainException($"{name} must be a 2-3 letter code.");
        }

        return trimmed;
    }

    internal static bool IsLetters(string value)
    {
        foreach (var c in value)
        {
            var isLetter = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');
            if (!isLetter)
            {
                return false;
            }
        }

        return true;
    }

    internal static string NormalizeSettings(string? settingsJson)
    {
        if (string.IsNullOrWhiteSpace(settingsJson))
        {
            return "{}";
        }

        var trimmed = settingsJson.Trim();
        try
        {
            using var document = JsonDocument.Parse(trimmed);
            return document.RootElement.GetRawText();
        }
        catch (JsonException ex)
        {
            throw new DomainException("Settings must be valid JSON.", ex);
        }
    }

    internal static string ComputeConfigurationHash(string source, string target, string normalizedSettings)
    {
        JsonNode? settingsNode;
        try
        {
            settingsNode = JsonNode.Parse(normalizedSettings) ?? new JsonObject();
        }
        catch (JsonException)
        {
            settingsNode = new JsonObject();
        }

        var canonical = new JsonObject
        {
            ["settings"] = settingsNode,
            ["sourceLanguage"] = source,
            ["targetLanguage"] = target,
        };
        return ConfigurationHashCalculator.Compute(canonical);
    }

    private static async Task EnsureDefaultPolicyAsync(
        DbContext db,
        Guid tenantId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var exists = await db.Set<ProcessingPolicy>().AnyAsync(cancellationToken).ConfigureAwait(false);
        if (exists)
        {
            return;
        }

        db.Set<ProcessingPolicy>().Add(new ProcessingPolicy(
            Guid.NewGuid(), tenantId, false, [], null,
            "default", "default", false, null, now, now));
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
            throw new DomainException($"{name} must not be empty.");
        }
    }
}
