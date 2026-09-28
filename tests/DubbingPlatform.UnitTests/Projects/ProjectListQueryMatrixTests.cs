// Task 039C: exports unit gap closure (query construction / pagination area).
using DubbingPlatform.Application.Common;
using DubbingPlatform.Application.Projects;
using DubbingPlatform.Domain.Enums;
using DubbingPlatform.Domain.Exceptions;

namespace DubbingPlatform.UnitTests.Projects;

/// <summary>
/// Filter/sort/paging construction for <c>GET /api/v1/projects</c> plus the
/// tenant-scoping predicate that is layered on top of it. The query record
/// itself carries no tenant: <c>ProjectService</c> composes
/// <c>TenantQueryFilter.ApplyTenant</c> first, so these tests pin both the
/// normalized filter values and the fact that a foreign tenant's rows can only
/// be excluded, never included.
/// </summary>
public sealed class ProjectListQueryMatrixTests
{
    private static readonly Guid TenantA = Guid.Parse("aaaaaaaa-1111-1111-1111-111111111111");
    private static readonly Guid TenantB = Guid.Parse("bbbbbbbb-2222-2222-2222-222222222222");
    private static readonly Guid OwnerA = Guid.Parse("cccccccc-3333-3333-3333-333333333333");
    private static readonly Guid OwnerB = Guid.Parse("dddddddd-4444-4444-4444-444444444444");

    private static ProjectListQuery Query(
        ProjectStatus? status = null,
        Guid? ownerId = null,
        string? search = null,
        string? archived = null,
        string? sort = null,
        string? sortDir = null,
        int page = 1,
        int pageSize = 20)
        => new(status, ownerId, search, archived, sort, sortDir, page, pageSize);

    [Fact]
    public void Defaults_Are_Newest_First_And_Exclude_Archived()
    {
        var query = Query();

        Assert.Equal("createdAt", query.NormalizedSort);
        Assert.Equal("desc", query.NormalizedSortDir);
        Assert.Equal((false, false), query.ArchivedMode());
        Assert.Null(query.Status);
        Assert.Null(query.OwnerId);
        Assert.Null(query.Search);
        Assert.Equal(1, query.Page);
        Assert.Equal(20, query.PageSize);
    }

    [Theory]
    [InlineData(null, "createdAt")]
    [InlineData("", "createdAt")]
    [InlineData("   ", "createdAt")]
    [InlineData("\t", "createdAt")]
    [InlineData("name", "name")]
    [InlineData("  name  ", "name")]
    [InlineData("updatedAt", "updatedAt")]
    [InlineData("CREATEDAT", "CREATEDAT")]
    public void NormalizedSort_Defaults_And_Trims(string? sort, string expected)
    {
        Assert.Equal(expected, Query(sort: sort).NormalizedSort);
    }

    [Theory]
    [InlineData(null, "desc")]
    [InlineData("", "desc")]
    [InlineData("  ", "desc")]
    [InlineData("asc", "asc")]
    [InlineData("  ASC  ", "asc")]
    [InlineData("Desc", "desc")]
    [InlineData("DESC", "desc")]
    public void NormalizedSortDir_Defaults_And_Lowercases(string? sortDir, string expected)
    {
        Assert.Equal(expected, Query(sortDir: sortDir).NormalizedSortDir);
    }

    [Theory]
    [InlineData("createdAt")]
    [InlineData("CREATEDAT")]
    [InlineData("createdat")]
    [InlineData("  updatedAt ")]
    [InlineData("UPDATEDAT")]
    [InlineData("name")]
    [InlineData("NAME")]
    public void Validate_Accepts_All_Sort_Keys_Case_Insensitively(string sort)
    {
        Query(sort: sort).Validate();
    }

    [Theory]
    [InlineData("asc")]
    [InlineData("ASC")]
    [InlineData(" Asc ")]
    [InlineData("desc")]
    [InlineData("DESC")]
    public void Validate_Accepts_Both_Sort_Directions(string sortDir)
    {
        Query(sortDir: sortDir).Validate();
    }

    [Theory]
    [InlineData("true")]
    [InlineData("TRUE")]
    [InlineData(" True ")]
    [InlineData("false")]
    [InlineData("FALSE")]
    [InlineData("all")]
    [InlineData("ALL")]
    [InlineData("  all  ")]
    [InlineData(null)]
    public void Validate_Accepts_Archived_Modes(string? archived)
    {
        Query(archived: archived).Validate();
    }

    [Fact]
    public void Validate_Accepts_Status_Owner_And_Search_Combinations()
    {
        Query(ProjectStatus.Completed, OwnerA, "documentary", "all", "name", "asc", 3, 50).Validate();
        Query(ProjectStatus.Processing, OwnerB, string.Empty, "true", "updatedAt", "desc", 1, 100).Validate();
    }

    [Theory]
    [InlineData("bogus")]
    [InlineData("created")]
    [InlineData("name;drop")]
    [InlineData("1")]
    public void Validate_Rejects_Unknown_Sort_Keys(string sort)
    {
        var ex = Assert.Throws<DomainException>(() => Query(sort: sort).Validate());
        Assert.Contains("Sort must be one of", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("sideways")]
    [InlineData("descending")]
    [InlineData("ascending")]
    [InlineData("1")]
    public void Validate_Rejects_Unknown_Sort_Directions(string sortDir)
    {
        var ex = Assert.Throws<DomainException>(() => Query(sortDir: sortDir).Validate());
        Assert.Contains("SortDir must be one of", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("sometimes")]
    [InlineData("yes")]
    [InlineData("1")]
    [InlineData("none")]
    public void Validate_Rejects_Unknown_Archived_Modes(string archived)
    {
        var ex = Assert.Throws<DomainException>(() => Query(archived: archived).Validate());
        Assert.Contains("Archived must be one of", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_Rejects_An_Empty_Owner_Guid()
    {
        var ex = Assert.Throws<DomainException>(() => Query(ownerId: Guid.Empty).Validate());
        Assert.Equal("OwnerId must not be empty when set.", ex.Message);
    }

    [Fact]
    public void Validate_Rejects_Sort_Before_SortDir_Before_Archived()
    {
        // All three are invalid at once: the first violation is the one reported.
        var ex = Assert.Throws<DomainException>(
            () => Query(archived: "sometimes", sort: "bogus", sortDir: "sideways").Validate());
        Assert.Contains("Sort must be one of", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, false, false)]
    [InlineData("", false, false)]
    [InlineData("  ", false, false)]
    [InlineData("false", false, false)]
    [InlineData("FALSE", false, false)]
    [InlineData(" False ", false, false)]
    [InlineData("true", true, true)]
    [InlineData("TRUE", true, true)]
    [InlineData(" True ", true, true)]
    [InlineData("all", true, false)]
    [InlineData("ALL", true, false)]
    [InlineData("  all  ", true, false)]
    public void ArchivedMode_Matrix(string? archived, bool includeArchived, bool onlyArchived)
    {
        Assert.Equal((includeArchived, onlyArchived), Query(archived: archived).ArchivedMode());
    }

    [Fact]
    public void ArchivedMode_Fails_Closed_For_Unknown_Values()
    {
        // An unrecognised value must narrow (exclude archived), never widen to
        // "include everything".
        var (includeArchived, onlyArchived) = Query(archived: "sometimes").ArchivedMode();

        Assert.False(includeArchived);
        Assert.False(onlyArchived);
    }

    [Fact]
    public void ArchivedMode_All_Includes_But_Does_Not_Filter_To_Archived_Only()
    {
        var (includeArchived, onlyArchived) = Query(archived: "all").ArchivedMode();

        Assert.True(includeArchived);
        Assert.False(onlyArchived);
    }

    [Fact]
    public void Record_Supports_With_And_Value_Equality()
    {
        var a = Query(ProjectStatus.Completed, OwnerA, "doc", "all", "name", "asc", 2, 50);
        var b = Query(ProjectStatus.Completed, OwnerA, "doc", "all", "name", "asc", 2, 50);
        var c = a with { Page = 3 };

        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
        Assert.True(a.GetHashCode() == b.GetHashCode());
        Assert.Equal(2, a.Page);
        Assert.Equal(3, c.Page);
    }

    [Fact]
    public void Paging_Values_Survive_Normalization_Of_Erroneous_Input()
    {
        // Page/PageSize are not validated here; PaginationParams clamps them.
        // A page 0 / size 0 query therefore still normalizes to a usable page.
        var query = Query(page: 0, pageSize: 0);
        var pagination = new PaginationParams { Page = query.Page, PageSize = query.PageSize };
        pagination.Normalize();

        Assert.Equal((1, 1), pagination.Normalized());
        query.Validate();
    }

    [Fact]
    public void Tenant_Scoped_Filter_For_One_Tenant_Excludes_The_Other()
    {
        var rows = new List<TenantRow>
        {
            new(TenantA, ProjectStatus.Completed, "Tenant A project", false),
            new(TenantB, ProjectStatus.Completed, "Tenant B project", false),
            new(TenantA, ProjectStatus.Processing, "Tenant A second", false),
        };

        var forA = Apply(rows, TenantA);
        var forB = Apply(rows, TenantB);

        Assert.Equal(2, forA.Count);
        Assert.Single(forB);
        Assert.All(forA, r => Assert.Equal(TenantA, r.TenantId));
        Assert.All(forB, r => Assert.Equal(TenantB, r.TenantId));
        foreach (var row in forB)
        {
            Assert.DoesNotContain(forA, r => r.Equals(row));
        }
    }

    [Fact]
    public void Tenant_Scoped_Filter_Never_Widens_The_Row_Set()
    {
        var rows = new List<TenantRow>
        {
            new(TenantA, ProjectStatus.Completed, "a", false),
            new(TenantB, ProjectStatus.Completed, "b", false),
        };

        var all = rows.AsQueryable();
        var scoped = Apply(rows, TenantA);

        Assert.True(scoped.Count < all.Count());
        foreach (var row in scoped)
        {
            Assert.Contains(row, all.ToList());
        }
    }

    [Fact]
    public void Tenant_Scoped_Filter_Composes_With_Archived_And_Status_Filters()
    {
        var rows = new List<TenantRow>
        {
            new(TenantA, ProjectStatus.Completed, "keep", false),
            new(TenantA, ProjectStatus.Completed, "archived", true),
            new(TenantA, ProjectStatus.Processing, "wrong-status", false),
            new(TenantB, ProjectStatus.Completed, "foreign", false),
        };

        var query = Query(ProjectStatus.Completed, archived: "false");
        var (includeArchived, onlyArchived) = query.ArchivedMode();

        var forA = Apply(rows, TenantA, query, includeArchived, onlyArchived);
        Assert.Single(forA);
        Assert.Equal("keep", forA[0].Name);

        // archived=true narrows to archived-only, still tenant-scoped.
        var archivedQuery = Query(archived: "true");
        var archivedMode = archivedQuery.ArchivedMode();
        var archivedRows = Apply(rows, TenantA, archivedQuery, archivedMode.IncludeArchived, archivedMode.OnlyArchived);
        Assert.Single(archivedRows);
        Assert.Equal("archived", archivedRows[0].Name);

        // archived=all returns every tenant-A row and none from tenant B.
        var allQuery = Query(archived: "all");
        var allMode = allQuery.ArchivedMode();
        var allRows = Apply(rows, TenantA, allQuery, allMode.IncludeArchived, allMode.OnlyArchived);
        Assert.Equal(3, allRows.Count);
        Assert.All(allRows, r => Assert.Equal(TenantA, r.TenantId));
        Assert.DoesNotContain(allRows, r => r.Name == "foreign");
    }

    [Fact]
    public void Tenant_Scoped_Filter_Applies_Owner_Filter_Without_Crossing_Tenants()
    {
        var rows = new List<TenantRow>
        {
            new(TenantA, ProjectStatus.Completed, "a-owned", false, OwnerA),
            new(TenantA, ProjectStatus.Completed, "a-other-owner", false, OwnerB),
            new(TenantB, ProjectStatus.Completed, "b-owned-by-a", false, OwnerA),
        };

        var query = Query(ownerId: OwnerA);
        query.Validate();
        var mode = query.ArchivedMode();
        var forA = Apply(rows, TenantA, query, mode.IncludeArchived, mode.OnlyArchived);

        Assert.Single(forA);
        Assert.Equal("a-owned", forA[0].Name);
        Assert.Equal(OwnerA, forA[0].OwnerId);
    }

    [Fact]
    public void Tenant_Scoped_Filter_Rejects_An_Empty_Tenant()
    {
        var rows = new List<TenantRow> { new(TenantA, ProjectStatus.Completed, "a", false) };

        Assert.Throws<ArgumentException>(() => TenantQueryFilter.ApplyTenantScoped(rows.AsQueryable(), Guid.Empty));
        Assert.Throws<ArgumentNullException>(
            () => TenantQueryFilter.ApplyTenantScoped<TenantRow>(null!, TenantA));
    }

    private static List<TenantRow> Apply(
        IReadOnlyList<TenantRow> rows,
        Guid tenantId,
        ProjectListQuery? query = null,
        bool includeArchived = false,
        bool onlyArchived = false)
    {
        var scoped = TenantQueryFilter.ApplyTenantScoped(rows.AsQueryable(), tenantId);
        if (!includeArchived)
        {
            scoped = scoped.Where(r => !r.IsArchived);
        }
        else if (onlyArchived)
        {
            scoped = scoped.Where(r => r.IsArchived);
        }

        if (query?.Status is { } status)
        {
            scoped = scoped.Where(r => r.Status == status);
        }

        if (query?.OwnerId is { } owner)
        {
            scoped = scoped.Where(r => r.OwnerId == owner);
        }

        if (!string.IsNullOrWhiteSpace(query?.Search))
        {
            var term = query.Search.Trim().ToLowerInvariant();
            scoped = scoped.Where(r => r.Name.ToLowerInvariant().Contains(term, StringComparison.Ordinal));
        }

        return scoped.ToList();
    }

    private sealed record TenantRow(
        Guid TenantId,
        ProjectStatus Status,
        string Name,
        bool IsArchived,
        Guid? OwnerId = null) : ITenantScoped;
}

/// <summary>
/// <see cref="PaginatedResult{T}"/> / <see cref="PaginationParams"/> page math:
/// validation bounds, <c>HasMore</c> derivation and skip arithmetic including
/// the checked overflow guard.
/// </summary>
public sealed class PaginatedResultMatrixTests
{
    [Fact]
    public void Create_Rejects_Null_Items()
    {
        Assert.Throws<ArgumentNullException>(
            () => PaginatedResult<string>.Create(null!, 1, 20, 0));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void Create_Rejects_Page_Below_One(int page)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => PaginatedResult<string>.Create([], page, 20, 0));
        Assert.Equal("page", ex.ParamName);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(101)]
    [InlineData(int.MaxValue)]
    public void Create_Rejects_Page_Size_Outside_1_To_100(int pageSize)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => PaginatedResult<string>.Create([], 1, pageSize, 0));
        Assert.Equal("pageSize", ex.ParamName);
    }

    [Fact]
    public void Create_Accepts_The_Page_Size_Bounds()
    {
        var min = PaginatedResult<string>.Create(["a"], 1, 1, 1);
        var max = PaginatedResult<string>.Create(["a"], 1, PaginationParams.MaxPageSize, 1);

        Assert.Equal(1, min.PageSize);
        Assert.Equal(100, max.PageSize);
    }

    [Fact]
    public void Create_Rejects_Negative_Total()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => PaginatedResult<string>.Create([], 1, 20, -1));
        Assert.Equal("total", ex.ParamName);
    }

    [Fact]
    public void Create_With_Zero_Total_Yields_An_Empty_First_Page()
    {
        var page = PaginatedResult<string>.Create([], 1, 20, 0);

        Assert.Empty(page.Items);
        Assert.Equal(1, page.Page);
        Assert.Equal(20, page.PageSize);
        Assert.Equal(0, page.Total);
        Assert.False(page.HasMore);
    }

    [Theory]
    [InlineData(1, 20, 0, false)]
    [InlineData(1, 20, 1, false)]
    [InlineData(1, 20, 19, false)]
    [InlineData(1, 20, 20, false)]
    [InlineData(1, 20, 21, true)]
    [InlineData(1, 20, 40, true)]
    [InlineData(2, 20, 21, false)]
    [InlineData(2, 20, 40, false)]
    [InlineData(2, 20, 41, true)]
    [InlineData(1, 1, 0, false)]
    [InlineData(1, 1, 1, false)]
    [InlineData(1, 1, 2, true)]
    [InlineData(2, 1, 1, false)]
    [InlineData(2, 1, 2, false)]
    [InlineData(2, 1, 3, true)]
    [InlineData(1, 100, 100, false)]
    [InlineData(1, 100, 101, true)]
    public void HasMore_Is_Page_Times_PageSize_Less_Than_Total(int page, int pageSize, long total, bool expected)
    {
        Assert.Equal(expected, PaginatedResult<int>.Create([], page, pageSize, total).HasMore);
    }

    [Fact]
    public void HasMore_Does_Not_Overflow_For_Huge_Page_Numbers()
    {
        // page * pageSize is computed in long, so 2_147_483_647 * 100 is safe.
        var page = PaginatedResult<int>.Create([], int.MaxValue, 100, long.MaxValue);

        Assert.True(page.HasMore);
        Assert.Equal(int.MaxValue, page.Page);
        Assert.Equal(long.MaxValue, page.Total);
    }

    [Fact]
    public void Record_Exposes_All_Envelope_Fields()
    {
        var page = PaginatedResult<int>.Create([1, 2, 3], 2, 3, 10);

        Assert.Equal(3, page.Items.Count);
        Assert.Equal(1, page.Items[0]);
        Assert.Equal(2, page.Items[1]);
        Assert.Equal(3, page.Items[2]);
        Assert.Equal(2, page.Page);
        Assert.Equal(3, page.PageSize);
        Assert.Equal(10, page.Total);
        Assert.True(page.HasMore);
    }

    [Fact]
    public void Direct_Construction_Bypasses_Validation_But_Keeps_Fields()
    {
        var page = new PaginatedResult<int>([1], 0, 0, -5, true);

        Assert.Equal(0, page.Page);
        Assert.Equal(-5, page.Total);
        Assert.True(page.HasMore);
    }

    [Fact]
    public void Pagination_Params_Defaults()
    {
        var parameters = new PaginationParams();

        Assert.Equal(1, parameters.Page);
        Assert.Equal(20, parameters.PageSize);
        Assert.Equal(20, PaginationParams.DefaultPageSize);
        Assert.Equal(100, PaginationParams.MaxPageSize);
    }

    [Theory]
    [InlineData(0, 20, 1, 20)]
    [InlineData(-5, 20, 1, 20)]
    [InlineData(1, 0, 1, 1)]
    [InlineData(1, -1, 1, 1)]
    [InlineData(1, 1000, 1, 100)]
    [InlineData(1, 100, 1, 100)]
    [InlineData(1, 99, 1, 99)]
    [InlineData(7, 25, 7, 25)]
    [InlineData(int.MaxValue, 100, int.MaxValue, 100)]
    public void Normalized_Clamps_Without_Mutating(int page, int pageSize, int expectedPage, int expectedSize)
    {
        var parameters = new PaginationParams { Page = page, PageSize = pageSize };

        var (normalizedPage, normalizedSize) = parameters.Normalized();

        Assert.Equal(expectedPage, normalizedPage);
        Assert.Equal(expectedSize, normalizedSize);
        // Normalized() must not mutate.
        Assert.Equal(page, parameters.Page);
        Assert.Equal(pageSize, parameters.PageSize);
    }

    [Theory]
    [InlineData(0, 20, 1, 20)]
    [InlineData(-5, 20, 1, 20)]
    [InlineData(1, 0, 1, 1)]
    [InlineData(1, 1000, 1, 100)]
    [InlineData(1, 100, 1, 100)]
    [InlineData(3, 25, 3, 25)]
    public void Normalize_Mutates_In_Place(int page, int pageSize, int expectedPage, int expectedSize)
    {
        var parameters = new PaginationParams { Page = page, PageSize = pageSize };

        parameters.Normalize();

        Assert.Equal(expectedPage, parameters.Page);
        Assert.Equal(expectedSize, parameters.PageSize);
        Assert.Equal((expectedPage, expectedSize), parameters.Normalized());
    }

    [Theory]
    [InlineData(1, 20, 0)]
    [InlineData(0, 20, 0)]
    [InlineData(2, 20, 20)]
    [InlineData(3, 20, 40)]
    [InlineData(1, 1, 0)]
    [InlineData(10, 100, 900)]
    [InlineData(101, 100, 10000)]
    [InlineData(1, 1000, 0)]
    public void Skip_Is_Page_Minus_One_Times_PageSize(int page, int pageSize, int expected)
    {
        var parameters = new PaginationParams { Page = page, PageSize = pageSize };

        Assert.Equal(expected, parameters.Skip());
    }

    [Fact]
    public void Skip_Is_Checked_And_Overflows_Loudly()
    {
        var parameters = new PaginationParams { Page = int.MaxValue, PageSize = 100 };

        Assert.Throws<OverflowException>(() => parameters.Skip());
    }

    [Fact]
    public void Page_Math_Agrees_Between_Envelope_And_Params()
    {
        const int pageSize = 20;
        const long total = 47;

        for (var page = 1; page <= 5; page++)
        {
            var parameters = new PaginationParams { Page = page, PageSize = pageSize };
            var (safePage, safeSize) = parameters.Normalized();
            var result = PaginatedResult<int>.Create([], safePage, safeSize, total);

            Assert.Equal((safePage - 1) * safeSize, parameters.Skip());
            Assert.Equal(safePage * safeSize < total, result.HasMore);
        }
    }
}
