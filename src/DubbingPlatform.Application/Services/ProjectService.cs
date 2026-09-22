using System.Text.Json;
using System.Text.Json.Nodes;
using DubbingPlatform.Application.Common;
using DubbingPlatform.Application.Configuration;
using DubbingPlatform.Application.Exceptions;
using DubbingPlatform.Application.MultiTenancy;
using DubbingPlatform.Application.Projects;
using DubbingPlatform.Application.Validation;
using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Domain.Enums;
using DubbingPlatform.Domain.Exceptions;
using Microsoft.EntityFrameworkCore;

namespace DubbingPlatform.Application.Services;

/// <summary>
/// Project lifecycle service. Creation validates ISO 639 language codes
/// (2-3 ASCII letters, different), normalizes settings JSON (empty becomes
/// <c>{}</c>, invalid JSON fails), computes the deterministic configuration
/// hash via <see cref="ProjectConfigHash"/>, inserts the <c>DubbingProject</c>
/// (<c>Created</c>) plus a default <c>ProcessingPolicy</c> when the tenant has none, and appends an audit
/// event. Deletion is logical via the <c>IsDeleted</c> EF shadow property
/// (default <c>false</c>, set by <see cref="DeleteAsync"/>; physical removal
/// and artifact dereference belong to Task 037 retention) plus
/// <c>IsArchived/ArchivedAt</c> so deleted rows also read as archived. Reads exclude
/// soft-deleted rows; <see cref="GetAsync"/> distinguishes 404 (missing or
/// deleted, <c>PROJECT_NOT_FOUND</c>) from 403 (tenant mismatch) via a maintenance-scope load.
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
    /// Creates a project and returns it. <paramref name="name"/> is optional
    /// for backward compatibility (absent → <c>Untitled project</c>); when
    /// present it must be 1..200 chars (empty/overlong → 400). Duplicate names
    /// are allowed. <paramref name="processingSettingsJson"/> when provided is
    /// validated against the v1 whitelist and folded into the config hash.
    /// </summary>
    public async Task<DubbingProject> CreateAsync(
        Guid tenantId,
        string sourceLanguage,
        string targetLanguage,
        string? settingsJson,
        string actor,
        CancellationToken cancellationToken = default,
        string? name = null,
        string? description = null,
        string? processingSettingsJson = null,
        string? correlationId = null)
    {
        RequireTenant(tenantId);
        var source = ValidateLanguage(sourceLanguage, "SourceLanguage");
        var target = ValidateLanguage(targetLanguage, "TargetLanguage");
        if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
        {
            throw new DomainException("SourceLanguage and TargetLanguage must differ.");
        }

        var normalizedSettings = NormalizeSettings(settingsJson);
        string? normalizedProcessing = null;
        if (!string.IsNullOrWhiteSpace(processingSettingsJson))
        {
            normalizedProcessing = NormalizeProcessingSettings(processingSettingsJson);
        }

        var configurationHash = ProjectConfigHash.Compute(source, target, normalizedSettings, normalizedProcessing);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);

        string? validatedName = null;
        if (name is not null)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > DubbingProject.MaxNameLength)
            {
                throw new DomainException($"Name must be 1..{DubbingProject.MaxNameLength} chars.");
            }

            validatedName = name.Trim();
        }

        string? validatedDescription = null;
        if (description is not null)
        {
            if (!string.IsNullOrWhiteSpace(description) && description.Length > DubbingProject.MaxDescriptionLength)
            {
                throw new DomainException($"Description must be at most {DubbingProject.MaxDescriptionLength} chars.");
            }

            if (string.IsNullOrWhiteSpace(description))
            {
                validatedDescription = null;
            }
            else
            {
                validatedDescription = description;
            }
        }

        Guid? ownerId = null;
        if (Guid.TryParse(actor.Trim(), out var actorGuid) && actorGuid != Guid.Empty)
        {
            ownerId = actorGuid;
        }

        var now = DateTimeOffset.UtcNow;
        var projectId = Guid.NewGuid();
        var project = new DubbingProject(
            projectId, tenantId, source, target,
            ProjectStatus.Created, normalizedSettings, configurationHash,
            null, null, now, now,
            validatedName, validatedDescription, ownerId, ownerId, ownerId, false, null, 1, normalizedProcessing);

        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            db.Set<DubbingProject>().Add(project);
            await EnsureDefaultPolicyAsync(db, tenantId, now, cancellationToken).ConfigureAwait(false);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        await _audit.LogAsync(
            tenantId, projectId, actor.Trim(), "project.create",
            "project", projectId.ToString("N"), AuditDetails(correlationId), cancellationToken).ConfigureAwait(false);

        return project;
    }

    /// <summary>
    /// Lists non-deleted projects for the tenant, ordered by creation time.
    /// Kept for backward compatibility (ascending creation order).
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
    /// Filtered list for <c>GET /api/v1/projects</c>: status/owner/search plus
    /// archived mode (<c>false|true|all</c>, default <c>false</c>) and sort
    /// (<c>createdAt|updatedAt|name</c>, <c>asc|desc</c>, default
    /// <c>createdAt desc</c>). Soft-deleted rows are always excluded.
    /// </summary>
    public async Task<(IReadOnlyList<DubbingProject> Items, long Total, string Sort, string SortDir)> ListFilteredAsync(
        Guid tenantId,
        ProjectListQuery query,
        CancellationToken cancellationToken = default)
    {
        RequireTenant(tenantId);
        ArgumentNullException.ThrowIfNull(query);
        query.Validate();

        var normalized = new PaginationParams { Page = query.Page, PageSize = query.PageSize };
        normalized.Normalize();
        var (safePage, safeSize) = normalized.Normalized();
        var (includeArchived, onlyArchived) = query.ArchivedMode();
        var sort = query.NormalizedSort.ToLowerInvariant();
        var descending = !string.Equals(query.NormalizedSortDir, "asc", StringComparison.Ordinal);

        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            var scoped = TenantQueryFilter.ApplyTenant(db.Set<DubbingProject>().AsNoTracking(), tenantId)
                .Where(p => EF.Property<bool>(p, "IsDeleted") == false);

            if (!includeArchived)
            {
                scoped = scoped.Where(p => !p.IsArchived);
            }
            else if (onlyArchived)
            {
                scoped = scoped.Where(p => p.IsArchived);
            }

            if (query.Status.HasValue)
            {
                var status = query.Status.Value;
                scoped = scoped.Where(p => p.Status == status);
            }

            if (query.OwnerId.HasValue)
            {
                var owner = query.OwnerId.Value;
                scoped = scoped.Where(p => p.OwnerUserId == owner);
            }

            if (!string.IsNullOrWhiteSpace(query.Search))
            {
                var term = query.Search.Trim().ToLowerInvariant();
                scoped = scoped.Where(p =>
                    (p.Name != null && p.Name.ToLower().Contains(term)) ||
                    (p.Description != null && p.Description.ToLower().Contains(term)));
            }

            var total = await scoped.LongCountAsync(cancellationToken).ConfigureAwait(false);

            IOrderedQueryable<DubbingProject> ordered = sort switch
            {
                "updatedat" => descending
                    ? scoped.OrderByDescending(p => p.UpdatedAt).ThenByDescending(p => p.CreatedAt)
                    : scoped.OrderBy(p => p.UpdatedAt).ThenBy(p => p.CreatedAt),
                "name" => descending
                    ? scoped.OrderByDescending(p => p.Name).ThenByDescending(p => p.CreatedAt)
                    : scoped.OrderBy(p => p.Name).ThenBy(p => p.CreatedAt),
                _ => descending
                    ? scoped.OrderByDescending(p => p.CreatedAt)
                    : scoped.OrderBy(p => p.CreatedAt),
            };

            var items = await ordered
                .Skip((safePage - 1) * safeSize)
                .Take(safeSize)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            var canonicalSort = sort switch
            {
                "updatedat" => "updatedAt",
                "name" => "name",
                _ => "createdAt",
            };
            return (items, total, canonicalSort, descending ? "desc" : "asc");
        }
    }

    /// <summary>
    /// Gets a project. Throws <see cref="ProjectNotFoundException"/> (404
    /// PROJECT_NOT_FOUND) when missing or soft-deleted,
    /// <see cref="ForbiddenException"/> (403) when the row belongs to another
    /// tenant. Cross-tenant stays 403 (not 404) to match the established
    /// <c>ProjectOwnershipHandler</c> + <c>ProjectsUploadsApiTests</c>
    /// contract; <c>prj_</c> ids therefore surface 403 while the new
    /// <c>PROJECT_NOT_FOUND</c> code covers missing/deleted rows.
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
                throw new ProjectNotFoundException($"Project '{projectId}' was not found.");
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
            throw new ProjectNotFoundException($"Project '{projectId}' was not found.");
        }

        return project;
    }

    /// <summary>
    /// Patches name/description/settings only. Language keys are rejected by
    /// the controller with <c>LANGUAGE_IMMUTABLE</c> before reaching here.
    /// Processing-settings changes require no active run (else 409
    /// <c>SETTINGS_LOCKED_ACTIVE_RUN</c>); <paramref name="expectedSettingsVersion"/>
    /// enforces optimistic concurrency (mismatch → 409
    /// <c>SETTINGS_VERSION_CONFLICT</c>). On a processing-settings change the
    /// version bumps and the config hash is recomputed; general-settings
    /// changes recompute the hash without bumping. No partial apply: validation
    /// runs before any mutation. Audits with old/new hash.
    /// </summary>
    public async Task<DubbingProject> PatchAsync(
        Guid tenantId,
        Guid projectId,
        string actor,
        string? name,
        string? description,
        string? settingsJson,
        string? processingSettingsJson,
        int? expectedSettingsVersion,
        string? correlationId,
        CancellationToken cancellationToken = default)
    {
        RequireTenant(tenantId);
        RequireId(projectId, nameof(projectId));
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);

        var current = await GetAsync(tenantId, projectId, cancellationToken).ConfigureAwait(false);
        if (expectedSettingsVersion.HasValue && expectedSettingsVersion.Value != current.SettingsVersion)
        {
            throw new SettingsVersionConflictException($"Project '{projectId}' settings version {current.SettingsVersion} does not match expected {expectedSettingsVersion.Value}.");
        }

        string? normalizedSettings = null;
        if (settingsJson is not null)
        {
            normalizedSettings = NormalizeSettings(settingsJson);
        }

        string? normalizedProcessing = null;
        var processingChanged = processingSettingsJson is not null;
        if (processingChanged)
        {
            var guard = new ProjectSettingsGuard(_contextFactory);
            await guard.ThrowIfSettingsLockedAsync(tenantId, projectId, cancellationToken).ConfigureAwait(false);
            normalizedProcessing = NormalizeProcessingSettings(processingSettingsJson);
        }

        string? validatedName = null;
        var nameChanged = name is not null;
        if (nameChanged)
        {
            if (string.IsNullOrWhiteSpace(name) || name!.Trim().Length > DubbingProject.MaxNameLength)
            {
                throw new DomainException($"Name must be 1..{DubbingProject.MaxNameLength} chars.");
            }

            validatedName = name!.Trim();
        }

        string? validatedDescription = null;
        var descriptionChanged = description is not null;
        if (descriptionChanged)
        {
            if (!string.IsNullOrWhiteSpace(description) && description!.Length > DubbingProject.MaxDescriptionLength)
            {
                throw new DomainException($"Description must be at most {DubbingProject.MaxDescriptionLength} chars.");
            }

            validatedDescription = string.IsNullOrWhiteSpace(description) ? null : description;
        }

        var oldHash = current.ConfigurationHash;
        var effectiveSettings = normalizedSettings ?? current.SettingsJson;
        var effectiveProcessing = processingChanged ? normalizedProcessing : current.ProcessingSettingsJson;
        var newHash = oldHash;
        if (normalizedSettings is not null || processingChanged)
        {
            newHash = ProjectConfigHash.Compute(current.SourceLanguage, current.TargetLanguage, effectiveSettings, effectiveProcessing);
        }

        var newVersion = current.SettingsVersion + (processingChanged ? 1 : 0);

        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            var attached = await db.Set<DubbingProject>()
                .FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken).ConfigureAwait(false);
            if (attached is null)
            {
                throw new ProjectNotFoundException($"Project '{projectId}' was not found.");
            }

            if (nameChanged || descriptionChanged)
            {
                var effectiveName = validatedName ?? attached.Name ?? "Untitled project";
                var effectiveDescription = descriptionChanged ? validatedDescription : attached.Description;
                attached.Rename(effectiveName, effectiveDescription);
            }

            if (normalizedSettings is not null && !processingChanged)
            {
                attached.UpdateGeneralSettings(normalizedSettings, newHash);
            }
            else if (processingChanged)
            {
                if (normalizedSettings is not null)
                {
                    attached.UpdateGeneralSettings(normalizedSettings, newHash);
                }

                attached.UpdateProcessingSettings(normalizedProcessing!, newVersion, newHash);
            }
            else if (normalizedSettings is null && !processingChanged && (nameChanged || descriptionChanged))
            {
                // Name/description-only patch leaves hash/version untouched.
            }

            if (Guid.TryParse(actor.Trim(), out var actorGuid) && actorGuid != Guid.Empty)
            {
                attached.SetOwnership(attached.OwnerUserId, actorGuid);
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            current = attached;
            newHash = attached.ConfigurationHash;
        }

        await _audit.LogAsync(
            tenantId, projectId, actor.Trim(), "project.patch",
            "project", projectId.ToString("N"),
            AuditDetails(correlationId, oldHash, newHash),
            cancellationToken).ConfigureAwait(false);

        return current;
    }

    /// <summary>
    /// Archives a project. Idempotent: already-archived returns success.
    /// </summary>
    public async Task<DubbingProject> ArchiveAsync(
        Guid tenantId,
        Guid projectId,
        string actor,
        string? correlationId,
        CancellationToken cancellationToken = default)
    {
        RequireTenant(tenantId);
        RequireId(projectId, nameof(projectId));
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);

        var project = await GetAsync(tenantId, projectId, cancellationToken).ConfigureAwait(false);
        if (!project.IsArchived)
        {
            using (TenantContext.BeginScope(tenantId))
            {
                using var db = _contextFactory.CreateDbContext();
                var attached = await db.Set<DubbingProject>()
                    .FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken).ConfigureAwait(false);
                if (attached is null)
                {
                    throw new ProjectNotFoundException($"Project '{projectId}' was not found.");
                }

                attached.Archive(DateTimeOffset.UtcNow);
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                project = attached;
            }

            await _audit.LogAsync(
                tenantId, projectId, actor.Trim(), "project.archive",
                "project", projectId.ToString("N"), AuditDetails(correlationId), cancellationToken).ConfigureAwait(false);
        }

        return project;
    }

    /// <summary>
    /// Unarchives a project. Idempotent: already-active returns success.
    /// </summary>
    public async Task<DubbingProject> UnarchiveAsync(
        Guid tenantId,
        Guid projectId,
        string actor,
        string? correlationId,
        CancellationToken cancellationToken = default)
    {
        RequireTenant(tenantId);
        RequireId(projectId, nameof(projectId));
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);

        var project = await GetAsync(tenantId, projectId, cancellationToken).ConfigureAwait(false);
        if (project.IsArchived)
        {
            using (TenantContext.BeginScope(tenantId))
            {
                using var db = _contextFactory.CreateDbContext();
                var attached = await db.Set<DubbingProject>()
                    .FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken).ConfigureAwait(false);
                if (attached is null)
                {
                    throw new ProjectNotFoundException($"Project '{projectId}' was not found.");
                }

                attached.Unarchive();
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                project = attached;
            }

            await _audit.LogAsync(
                tenantId, projectId, actor.Trim(), "project.unarchive",
                "project", projectId.ToString("N"), AuditDetails(correlationId), cancellationToken).ConfigureAwait(false);
        }

        return project;
    }

    /// <summary>
    /// Logically deletes a project (sets <c>IsDeleted</c> plus archived state)
    /// and audits. Blocked with 409 <c>PROJECT_HAS_ACTIVE_RUN</c> while an
    /// active run exists. Returns 202 semantics to callers; subsequent reads
    /// treat the row as missing.
    /// </summary>
    public async Task DeleteAsync(
        Guid tenantId,
        Guid projectId,
        string actor,
        CancellationToken cancellationToken = default,
        string? correlationId = null)
    {
        RequireTenant(tenantId);
        RequireId(projectId, nameof(projectId));
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);

        // Ownership first (maintenance load distinguishes 404 vs 403).
        var project = await GetAsync(tenantId, projectId, cancellationToken).ConfigureAwait(false);

        var guard = new ProjectSettingsGuard(_contextFactory);
        await guard.ThrowIfDeleteBlockedAsync(tenantId, projectId, cancellationToken).ConfigureAwait(false);

        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            var attached = await db.Set<DubbingProject>()
                .FirstOrDefaultAsync(p => p.Id == project.Id, cancellationToken).ConfigureAwait(false);
            if (attached is null)
            {
                throw new ProjectNotFoundException($"Project '{projectId}' was not found.");
            }

            if (!attached.IsArchived)
            {
                attached.Archive(DateTimeOffset.UtcNow);
            }

            db.Entry(attached).Property("IsDeleted").CurrentValue = true;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        await _audit.LogAsync(
            tenantId, projectId, actor.Trim(), "project.delete",
            "project", projectId.ToString("N"), AuditDetails(correlationId), cancellationToken).ConfigureAwait(false);
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

    internal static string NormalizeProcessingSettings(string? processingSettingsJson)
    {
        if (string.IsNullOrWhiteSpace(processingSettingsJson))
        {
            throw new DomainException("Processing settings must not be empty.");
        }

        var trimmed = processingSettingsJson.Trim();
        var validator = new ProjectProcessingSettingsValidator();
        var result = validator.Validate(trimmed);
        if (!result.IsValid)
        {
            var first = result.Errors.Count > 0 ? result.Errors[0].ErrorMessage : "Processing settings are invalid.";
            throw new DomainException(first);
        }

        try
        {
            using var document = JsonDocument.Parse(trimmed);
            return document.RootElement.GetRawText();
        }
        catch (JsonException ex)
        {
            throw new DomainException("Processing settings must be valid JSON.", ex);
        }
    }

    internal static string ComputeConfigurationHash(string source, string target, string normalizedSettings)
    {
        return ProjectConfigHash.Compute(source, target, normalizedSettings, null);
    }

    internal static string AuditDetails(string? correlationId, string? oldHash = null, string? newHash = null)
    {
        if (string.IsNullOrWhiteSpace(correlationId) && oldHash is null)
        {
            return "{}";
        }

        using var stream = new System.IO.MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            if (!string.IsNullOrWhiteSpace(correlationId))
            {
                writer.WriteString("correlationId", correlationId!.Trim());
            }

            if (oldHash is not null)
            {
                writer.WriteString("oldHash", oldHash);
            }

            if (newHash is not null)
            {
                writer.WriteString("newHash", newHash);
            }

            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
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
