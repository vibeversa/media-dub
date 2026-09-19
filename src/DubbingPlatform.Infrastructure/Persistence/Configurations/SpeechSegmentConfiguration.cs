using DubbingPlatform.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DubbingPlatform.Infrastructure.Persistence.Configurations;

public sealed class SpeechSegmentConfiguration : IEntityTypeConfiguration<SpeechSegment>
{
    public void Configure(EntityTypeBuilder<SpeechSegment> builder)
    {
        builder.ToTable("speech_segments");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
        builder.Property(e => e.Status).IsRequired().HasMaxLength(32);
        builder.HasIndex(e => new { e.ProjectId, e.Sequence });
        builder.HasIndex(e => new { e.TenantId, e.ProjectId });
    }
}
