using DubbingPlatform.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DubbingPlatform.Infrastructure.Persistence.Configurations;

public sealed class ActivityEventConfiguration : IEntityTypeConfiguration<ActivityEvent>
{
    public void Configure(EntityTypeBuilder<ActivityEvent> builder)
    {
        builder.ToTable("activity_events");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
        builder.Property(e => e.Type).HasConversion<string>().HasMaxLength(32);
        builder.Property(e => e.ActorType).HasConversion<string>().HasMaxLength(32);
        builder.Property(e => e.Summary).IsRequired().HasMaxLength(1000);
        builder.Property(e => e.Severity).HasConversion<string>().HasMaxLength(32);
        builder.Property(e => e.CorrelationId).IsRequired().HasMaxLength(128);
        builder.Property(e => e.MetadataJson).HasColumnType("jsonb");
        builder.HasIndex(e => new { e.TenantId, e.ProjectId, e.OccurredAt });
    }
}
