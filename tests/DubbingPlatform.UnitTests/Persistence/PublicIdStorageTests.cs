// GAP-010: public IDs are API-only by decision (ADR-010). The database stores
// native UUIDs; PublicIdConverter must never be applied to PK/FK columns
// (would break gen_random_uuid(), uuid indexes, RLS, FK joins, and require a
// non-expand-only uuid->text rewrite). This test pins the decision.
using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Domain.Identity;
using DubbingPlatform.Infrastructure.Persistence;
using DubbingPlatform.Infrastructure.Persistence.Converters;
using EFCore.NamingConventions;
using Microsoft.EntityFrameworkCore;

namespace DubbingPlatform.UnitTests.Persistence;

public sealed class PublicIdStorageTests : IDisposable
{
    private readonly AppDbContext _context;

    public PublicIdStorageTests()
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
    public void Id_Properties_Remain_Uuid_Without_PublicId_Converter()
    {
        var violations = new List<string>();
        foreach (var entityType in _context.Model.GetEntityTypes())
        {
            foreach (var property in entityType.GetProperties())
            {
                if (property.ClrType != typeof(Guid))
                {
                    continue;
                }

                if (!string.Equals(property.Name, "Id", StringComparison.Ordinal)
                    && !property.Name.EndsWith("Id", StringComparison.Ordinal))
                {
                    continue;
                }

                var converter = property.GetValueConverter();
                if (converter is PublicIdConverter)
                {
                    violations.Add($"{entityType.ClrType.Name}.{property.Name} uses PublicIdConverter");
                }
            }
        }

        Assert.Empty(violations);
    }

    [Fact]
    public void PublicIdConverter_Round_Trips_For_Compat_And_Test_Use()
    {
        var id = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var converter = PublicIdConverter.For<DubbingProject>();
        var mappingHints = converter.MappingHints;
        _ = mappingHints;

        var publicId = PublicIdMapper.ToPublic(id, PublicIdMapper.DubbingProjectPrefix);
        Assert.Equal("prj_11111111111111111111111111111111", publicId);
        Assert.Equal(id, PublicIdMapper.FromPublic(publicId).Id);
    }
}
