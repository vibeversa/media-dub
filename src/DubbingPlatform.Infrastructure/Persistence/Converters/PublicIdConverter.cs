using DubbingPlatform.Domain.Identity;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace DubbingPlatform.Infrastructure.Persistence.Converters;

/// <summary>
/// EF value converter between internal UUIDs and prefixed public IDs (e.g. <c>prj_&lt;32 hex&gt;</c>).
/// The database stores UUID columns; this converter exists for API-facing string columns and for
/// query-translation tests. It must not be applied to primary UUID columns.
/// </summary>
public sealed class PublicIdConverter : ValueConverter<Guid, string>
{
    public PublicIdConverter(string prefix)
        : base(
            id => PublicIdMapper.ToPublic(id, prefix),
            value => PublicIdMapper.FromPublic(value).Id)
    {
    }

    /// <summary>
    /// Creates a converter using the registered prefix for the given entity type.
    /// </summary>
    public static PublicIdConverter For<TEntity>()
        where TEntity : class
    {
        return new PublicIdConverter(PublicIdMapper.PrefixFor<TEntity>());
    }
}
