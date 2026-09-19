using DubbingPlatform.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DubbingPlatform.Infrastructure.Persistence.Configurations;

public sealed class AuditEventConfiguration : IEntityTypeConfiguration<AuditEvent>
{
    public void Configure(EntityTypeBuilder<AuditEvent> builder)
    {
        builder.ToTable("audit_events");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
        builder.Property(e => e.Actor).IsRequired().HasMaxLength(256);
        builder.Property(e => e.Action).IsRequired().HasMaxLength(256);
        builder.Property(e => e.ResourceType).IsRequired().HasMaxLength(128);
        builder.Property(e => e.ResourceId).IsRequired().HasMaxLength(256);
        builder.Property(e => e.DetailsJson).HasColumnType("jsonb");
        builder.HasIndex(e => new { e.TenantId, e.CreatedAt });
        builder.HasIndex(e => new { e.TenantId, e.ResourceType, e.ResourceId });
    }
}
