using DubbingPlatform.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DubbingPlatform.Infrastructure.Persistence.Configurations;

public sealed class SegmentOverlapConfiguration : IEntityTypeConfiguration<SegmentOverlap>
{
    public void Configure(EntityTypeBuilder<SegmentOverlap> builder)
    {
        builder.ToTable("segment_overlaps");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
        builder.Property(e => e.RelationType).IsRequired().HasMaxLength(64);
        builder.HasIndex(e => new { e.TenantId, e.OverlapGroupId });
    }
}
