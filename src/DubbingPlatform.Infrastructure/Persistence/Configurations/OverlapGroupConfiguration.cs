using DubbingPlatform.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DubbingPlatform.Infrastructure.Persistence.Configurations;

public sealed class OverlapGroupConfiguration : IEntityTypeConfiguration<OverlapGroup>
{
    public void Configure(EntityTypeBuilder<OverlapGroup> builder)
    {
        builder.ToTable("overlap_groups");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
        builder.HasIndex(e => new { e.TenantId, e.ProjectId });
    }
}
