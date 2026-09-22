using DubbingPlatform.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DubbingPlatform.Infrastructure.Persistence.Configurations;

public sealed class NotificationConfiguration : IEntityTypeConfiguration<Notification>
{
    public void Configure(EntityTypeBuilder<Notification> builder)
    {
        builder.ToTable("notifications");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
        builder.Property(e => e.Type).HasConversion<string>().HasMaxLength(32);
        builder.Property(e => e.Severity).HasConversion<string>().HasMaxLength(32);
        builder.Property(e => e.Title).IsRequired().HasMaxLength(200);
        builder.Property(e => e.Body).IsRequired().HasMaxLength(1000);
        builder.Property(e => e.ResourceType).IsRequired().HasMaxLength(128);
        builder.Property(e => e.ResourceId).IsRequired().HasMaxLength(256);
        builder.HasIndex(e => new { e.TenantId, e.RecipientUserId, e.SourceEventId })
            .IsUnique()
            .HasFilter("source_event_id IS NOT NULL");
        builder.HasIndex(e => new { e.TenantId, e.RecipientUserId, e.ReadAt, e.CreatedAt });
    }
}
