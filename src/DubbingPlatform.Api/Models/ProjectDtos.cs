using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Domain.Identity;

namespace DubbingPlatform.Api.Models;

/// <summary>
/// Create-project request body. Example:
/// <c>{ "sourceLanguage": "en", "targetLanguage": "es", "settings": {} }</c>.
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
}

/// <summary>
/// Project response body. <c>Id</c> is the prefixed public id (<c>prj_</c>).
/// </summary>
public sealed record ProjectResponse(
    string Id,
    Guid TenantId,
    string SourceLanguage,
    string TargetLanguage,
    string Status,
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
            project.CreatedAt,
            project.UpdatedAt);
    }
}

/// <summary>
/// Delete-project response body (202).
/// </summary>
public sealed record DeleteProjectResponse(string Id, bool Deleted);
