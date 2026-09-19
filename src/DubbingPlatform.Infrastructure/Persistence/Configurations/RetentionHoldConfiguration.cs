using DubbingPlatform.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DubbingPlatform.Infrastructure.Persistence.Configurations;

public sealed class RetentionHoldConfiguration : IEntityTypeConfiguration<RetentionHold>
{
    public void Configure(EntityTypeBuilder<RetentionHold> builder)
    {
        builder.ToTable("retention_holds");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
        builder.Property(e => e.PlacedBy).IsRequired().HasMaxLength(256);
        builder.HasIndex(e => new { e.TenantId, e.ProjectId });
        builder.HasIndex(e => new { e.TenantId, e.ArtifactId });
        builder.HasIndex(e => new { e.TenantId, e.IsActive });
    }
}
