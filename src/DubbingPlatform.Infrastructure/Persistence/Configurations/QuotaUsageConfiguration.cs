using DubbingPlatform.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DubbingPlatform.Infrastructure.Persistence.Configurations;

public sealed class QuotaUsageConfiguration : IEntityTypeConfiguration<QuotaUsage>
{
    public void Configure(EntityTypeBuilder<QuotaUsage> builder)
    {
        builder.ToTable("quota_usages");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
        builder.Property(e => e.Dimension).IsRequired().HasMaxLength(128);
        builder.HasIndex(e => new { e.TenantId, e.Dimension, e.WindowStart }).IsUnique();
    }
}
