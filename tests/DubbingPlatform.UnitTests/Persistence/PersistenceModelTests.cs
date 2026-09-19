using System.Text.RegularExpressions;
using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Infrastructure.Persistence;
using EFCore.NamingConventions;
using Microsoft.EntityFrameworkCore;

namespace DubbingPlatform.UnitTests.Persistence;

/// <summary>
/// Offline model verification for AppDbContext: snake_case naming, required
/// unique/partial/composite indexes, MassTransit outbox tables, and tenant filters.
/// Runs without a database server.
/// </summary>
public sealed class PersistenceModelTests : IDisposable
{
    private static readonly Regex SnakeCase = new("^[a-z][a-z0-9_]*$", RegexOptions.Compiled);

    private readonly AppDbContext _context;

    public PersistenceModelTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=dummy;Username=dummy;Password=dummy")
            .UseSnakeCaseNamingConvention()
            .Options;
        _context = new AppDbContext(options);
    }

    public void Dispose()
    {
        _context.Dispose();
    }

    [Fact]
    public void All_Table_And_Column_Names_Are_Snake_Case()
    {
        var violations = new List<string>();
        foreach (var entityType in _context.Model.GetEntityTypes())
        {
            var table = entityType.GetTableName();
            if (table is not null && !SnakeCase.IsMatch(table))
            {
                violations.Add($"table {table}");
            }

            foreach (var property in entityType.GetProperties())
            {
                var column = property.GetColumnName();
                if (column is not null && !SnakeCase.IsMatch(column))
                {
                    violations.Add($"column {table}.{column}");
                }
            }
        }

        Assert.Empty(violations);
    }

    [Fact]
    public void Processing_Runs_Have_Partial_Unique_Active_Run_Index()
    {
        var entity = _context.Model.FindEntityType(typeof(ProcessingRun));
        Assert.NotNull(entity);

        var index = entity.GetIndexes().FirstOrDefault(i => i.IsUnique && i.Properties.Select(p => p.Name).SequenceEqual(["ProjectId"]));
        Assert.NotNull(index);
        Assert.NotNull(index.GetFilter());
        Assert.Contains("Pending", index.GetFilter(), StringComparison.Ordinal);
        Assert.Contains("Running", index.GetFilter(), StringComparison.Ordinal);
        Assert.Contains("Cancelling", index.GetFilter(), StringComparison.Ordinal);
        Assert.Contains("ManualReviewRequired", index.GetFilter(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(typeof(IdempotencyRecord), new[] { "TenantId", "Endpoint", "IdempotencyKey" })]
    [InlineData(typeof(ContentObject), new[] { "TenantId", "ContentHash" })]
    [InlineData(typeof(StageExecution), new[] { "ProcessingRunId", "StageType", "ScopeType", "ScopeId", "Attempt" })]
    [InlineData(typeof(StageUnitCompletion), new[] { "ProcessingRunId", "StageType", "ScopeType", "ScopeId", "StageExecutionId" })]
    [InlineData(typeof(UploadPart), new[] { "UploadSessionId", "PartNumber" })]
    public void Required_Unique_Indexes_Exist(Type entityType, string[] properties)
    {
        var entity = _context.Model.FindEntityType(entityType);
        Assert.NotNull(entity);

        var exists = entity.GetIndexes().Any(i =>
            i.IsUnique && i.Properties.Select(p => p.Name).SequenceEqual(properties));
        Assert.True(exists, $"Unique index on ({string.Join(",", properties)}) missing for {entityType.Name}.");
    }

    [Fact]
    public void Stage_Executions_Have_Lease_And_Workload_Indexes()
    {
        var entity = _context.Model.FindEntityType(typeof(StageExecution));
        Assert.NotNull(entity);

        Assert.Contains(entity.GetIndexes(), i =>
            i.Properties.Select(p => p.Name).SequenceEqual(["ProcessingRunId", "StageType", "Status"]));
        Assert.Contains(entity.GetIndexes(), i =>
            i.Properties.Select(p => p.Name).SequenceEqual(["ProcessingRunId", "Status", "LeaseExpiresAt"]));
        Assert.Contains(entity.GetIndexes(), i =>
            i.GetFilter() is not null && i.GetFilter()!.Contains("Running", StringComparison.Ordinal));
    }

    [Fact]
    public void MassTransit_Outbox_Tables_Exist_With_Snake_Case_Names()
    {
        var tables = _context.Model.GetEntityTypes()
            .Select(e => e.GetTableName())
            .Where(t => t is not null)
            .ToHashSet();

        Assert.Contains("outbox_state", tables);
        Assert.Contains("outbox_message", tables);
        Assert.Contains("inbox_state", tables);
    }

    [Fact]
    public void All_Tenant_Scoped_Entities_Have_Query_Filters()
    {
        var missing = new List<string>();
        foreach (var entityType in _context.Model.GetEntityTypes())
        {
            var clrType = entityType.ClrType;
            if (clrType is null || clrType.Assembly != typeof(Tenant).Assembly)
            {
                continue;
            }

            if (entityType.FindProperty("TenantId")?.ClrType != typeof(Guid))
            {
                continue;
            }

            var filters = entityType.GetDeclaredQueryFilters();
            if (filters is null || !filters.Any())
            {
                missing.Add(clrType.Name);
            }
        }

        Assert.Empty(missing);
    }

    [Fact]
    public void Tenant_Entity_Has_No_Query_Filter()
    {
        var entity = _context.Model.FindEntityType(typeof(Tenant));
        Assert.NotNull(entity);
        var filters = entity.GetDeclaredQueryFilters();
        Assert.True(filters is null || !filters.Any(), "Tenant entity must not have a query filter.");
    }
}
