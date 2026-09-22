using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Domain.Identity;

namespace DubbingPlatform.Api.Models;

/// <summary>
/// Create-project request body. Example:
/// <c>{ "name": "Trailer", "sourceLanguage": "en", "targetLanguage": "es", "settings": {}, "processingSettings": { "schemaVersion": 1 } }</c>.
/// <c>Name</c> is optional for backward compatibility (absent → Untitled);
/// when present it must be 1..200 chars (empty/overlong → 400). Duplicate
/// names are allowed. <c>ProcessingSettings</c> when present must satisfy the
/// v1 whitelist and is folded into the config hash.
/// </summary>
public sealed class CreateProjectRequest
{
    [Required]
    [MinLength(2)]
    [MaxLength(3)]
    public string SourceLanguage { get; set; } = string.Empty;

    [Required]
    [MinLength(2)]
    [MaxLength(3)]
    public string TargetLanguage { get; set; } = string.Empty;

    public JsonElement? Settings { get; set; }

    [MaxLength(200)]
    public string? Name { get; set; }

    [MaxLength(2000)]
    public string? Description { get; set; }

    public JsonElement? ProcessingSettings { get; set; }
}

/// <summary>
/// Project response body. <c>Id</c> is the prefixed public id (<c>prj_</c>).
/// <c>OwnerId</c> is the raw user GUID when known. <c>ETag</c> semantics:
/// <c>SettingsVersion</c> is the optimistic-concurrency token; clients send it
/// back via <c>If-Match</c> (or <c>settingsVersion</c> field) on PATCH.
/// </summary>
public sealed record ProjectResponse(
    string Id,
    Guid TenantId,
    string SourceLanguage,
    string TargetLanguage,
    string Status,
    string? Name,
    string? Description,
    Guid? OwnerId,
    bool IsArchived,
    DateTimeOffset? ArchivedAt,
    int SettingsVersion,
    string ConfigurationHash,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public static ProjectResponse From(DubbingProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        return new ProjectResponse(
            PublicIdMapper.ToPublic(project.Id, PublicIdMapper.DubbingProjectPrefix),
            project.TenantId,
            project.SourceLanguage,
            project.TargetLanguage,
            project.Status.ToString(),
            project.Name,
            project.Description,
            project.OwnerUserId,
            project.IsArchived,
            project.ArchivedAt,
            project.SettingsVersion,
            project.ConfigurationHash,
            project.CreatedAt,
            project.UpdatedAt);
    }
}

/// <summary>
/// Project list envelope: <c>{ items, page, pageSize, total, sort, sortDir,
/// hasMore, clamped }</c> serialized camelCase. <c>Sort</c> is one of
/// <c>createdAt|updatedAt|name</c>; <c>sortDir</c> is <c>asc|desc</c>.
/// <c>Clamped</c> is true only when the requested <c>pageSize &gt; 100</c> was
/// clamped to 100 (not an error). <c>HasMore</c> is
/// <c>page * pageSize &lt; total</c> (kept for backward compatibility with
/// <c>PaginatedResult</c> consumers).
/// Example: <c>{ items: [{ id: "prj_...", status: "Created", ... }], page: 1,
/// pageSize: 20, total: 2, sort: "createdAt", sortDir: "desc", hasMore: false,
/// clamped: false }</c>.
/// </summary>
public sealed record ProjectListResponse(
    IReadOnlyList<ProjectResponse> Items,
    int Page,
    int PageSize,
    long Total,
    string Sort,
    string SortDir,
    bool HasMore,
    bool Clamped)
{
    public static ProjectListResponse Create(
        IReadOnlyList<ProjectResponse> items,
        int page,
        int pageSize,
        long total,
        string sort,
        string sortDir,
        bool clamped)
    {
        ArgumentNullException.ThrowIfNull(items);
        var hasMore = (long)page * pageSize < total;
        return new ProjectListResponse(items, page, pageSize, total, sort, sortDir, hasMore, clamped);
    }
}

/// <summary>
/// Delete-project response body (202).
/// </summary>
public sealed record DeleteProjectResponse(string Id, bool Deleted);
