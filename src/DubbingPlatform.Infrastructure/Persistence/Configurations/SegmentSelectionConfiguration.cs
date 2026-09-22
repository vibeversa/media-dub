using DubbingPlatform.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DubbingPlatform.Infrastructure.Persistence.Configurations;

public sealed class SegmentSelectionConfiguration : IEntityTypeConfiguration<SegmentSelection>
{
    public void Configure(EntityTypeBuilder<SegmentSelection> builder)
    {
        builder.ToTable("segment_selections");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
        builder.HasIndex(e => new { e.TenantId, e.SegmentId }).IsUnique();
        builder.HasIndex(e => new { e.TenantId, e.ProjectId });
    }
}
