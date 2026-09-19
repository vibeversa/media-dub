using DubbingPlatform.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DubbingPlatform.Infrastructure.Persistence.Configurations;

public sealed class SegmentContextAssignmentConfiguration : IEntityTypeConfiguration<SegmentContextAssignment>
{
    public void Configure(EntityTypeBuilder<SegmentContextAssignment> builder)
    {
        builder.ToTable("segment_context_assignments");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
        builder.HasIndex(e => new { e.SegmentId, e.ContextWindowId }).IsUnique();
    }
}
