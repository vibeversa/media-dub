using DubbingPlatform.Domain.Enums;

namespace DubbingPlatform.Application.Projects;

/// <summary>
/// Project list filters. <c>Archived</c> accepts <c>true|false|all</c>
/// (case-insensitive, default <c>false</c> = exclude archived;
/// <c>true</c> = only archived; <c>all</c> = both). <c>Sort</c> accepts
/// <c>createdAt|updatedAt|name</c> (default <c>createdAt</c>); <c>SortDir</c>
/// accepts <c>asc|desc</c> (default <c>desc</c> = newest first).
/// GAP-007: <c>TargetLanguage</c> exact match (2-3 letters, case-insensitive);
/// <c>CreatedFrom</c>/<c>CreatedTo</c> YYYY-MM-DD inclusive (UTC); <c>ReviewRequired</c>
/// when true narrows to <c>ManualReviewRequired</c> projects (null/false = no filter).
/// </summary>
public sealed record ProjectListQuery(
    ProjectStatus? Status,
    Guid? OwnerId,
    string? Search,
    string? Archived,
    string? Sort,
    string? SortDir,
    int Page,
    int PageSize,
    string? TargetLanguage = null,
    string? CreatedFrom = null,
    string? CreatedTo = null,
    bool? ReviewRequired = null)
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

        if (!string.IsNullOrWhiteSpace(TargetLanguage))
        {
            var lang = TargetLanguage.Trim();
            if (lang.Length is < 2 or > 3 || !IsLetters(lang))
            {
                throw new Domain.Exceptions.DomainException($"TargetLanguage must be a 2-3 letter code (got '{TargetLanguage}').");
            }
        }

        DateTimeOffset? from = null;
        DateTimeOffset? toExclusive = null;
        if (!string.IsNullOrWhiteSpace(CreatedFrom))
        {
            from = ParseDate(CreatedFrom, nameof(CreatedFrom));
        }

        if (!string.IsNullOrWhiteSpace(CreatedTo))
        {
            var toDate = ParseDate(CreatedTo, nameof(CreatedTo));
            toExclusive = toDate.AddDays(1);
        }

        if (from.HasValue && toExclusive.HasValue && from.Value >= toExclusive.Value)
        {
            throw new Domain.Exceptions.DomainException("CreatedFrom must not be after CreatedTo.");
        }
    }

    /// <summary>
    /// Inclusive UTC start for <c>CreatedFrom</c> (00:00 of that date), or null.
    /// </summary>
    public DateTimeOffset? CreatedFromStart()
    {
        if (string.IsNullOrWhiteSpace(CreatedFrom))
        {
            return null;
        }

        return ParseDate(CreatedFrom, nameof(CreatedFrom));
    }

    /// <summary>
    /// Exclusive UTC end for <c>CreatedTo</c> (00:00 of the next day), or null.
    /// </summary>
    public DateTimeOffset? CreatedToExclusive()
    {
        if (string.IsNullOrWhiteSpace(CreatedTo))
        {
            return null;
        }

        return ParseDate(CreatedTo, nameof(CreatedTo)).AddDays(1);
    }

    private static DateTimeOffset ParseDate(string raw, string name)
    {
        if (!System.DateTime.TryParseExact(
            raw.Trim(),
            "yyyy-MM-dd",
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
            out var date))
        {
            throw new Domain.Exceptions.DomainException($"{name} must be YYYY-MM-DD (got '{raw}').");
        }

        return new DateTimeOffset(System.DateTime.SpecifyKind(date.Date, System.DateTimeKind.Utc));
    }

    private static bool IsLetters(string value)
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
