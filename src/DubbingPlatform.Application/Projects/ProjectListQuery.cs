using DubbingPlatform.Domain.Enums;

namespace DubbingPlatform.Application.Projects;

/// <summary>
/// Project list filters. <c>Archived</c> accepts <c>true|false|all</c>
/// (case-insensitive, default <c>false</c> = exclude archived;
/// <c>true</c> = only archived; <c>all</c> = both). <c>Sort</c> accepts
/// <c>createdAt|updatedAt|name</c> (default <c>createdAt</c>); <c>SortDir</c>
/// accepts <c>asc|desc</c> (default <c>desc</c> = newest first).
/// </summary>
public sealed record ProjectListQuery(
    ProjectStatus? Status,
    Guid? OwnerId,
    string? Search,
    string? Archived,
    string? Sort,
    string? SortDir,
    int Page,
    int PageSize)
{
    /// <summary>
    /// Normalized sort key.
    /// </summary>
    public string NormalizedSort => string.IsNullOrWhiteSpace(Sort) ? "createdAt" : Sort.Trim();

    /// <summary>
    /// Normalized sort direction.
    /// </summary>
    public string NormalizedSortDir => string.IsNullOrWhiteSpace(SortDir) ? "desc" : SortDir.Trim().ToLowerInvariant();

    /// <summary>
    /// Validates sort/sortDir/archived values. Throws validation on misuse.
    /// </summary>
    public void Validate()
    {
        var sort = NormalizedSort.ToLowerInvariant();
        if (sort is not ("createdat" or "updatedat" or "name"))
        {
            throw new Domain.Exceptions.DomainException($"Sort must be one of createdAt, updatedAt, name (got '{Sort}').");
        }

        var dir = NormalizedSortDir;
        if (dir is not ("asc" or "desc"))
        {
            throw new Domain.Exceptions.DomainException($"SortDir must be one of asc, desc (got '{SortDir}').");
        }

        var archived = (Archived ?? "false").Trim().ToLowerInvariant();
        if (archived is not ("true" or "false" or "all"))
        {
            throw new Domain.Exceptions.DomainException($"Archived must be one of true, false, all (got '{Archived}').");
        }

        if (OwnerId.HasValue && OwnerId.Value == Guid.Empty)
        {
            throw new Domain.Exceptions.DomainException("OwnerId must not be empty when set.");
        }
    }

    /// <summary>
    /// Whether archived rows are included and whether only archived rows match.
    /// </summary>
    public (bool IncludeArchived, bool OnlyArchived) ArchivedMode()
    {
        var mode = (Archived ?? "false").Trim().ToLowerInvariant();
        return mode switch
        {
            "true" => (true, true),
            "all" => (true, false),
            _ => (false, false),
        };
    }
}
