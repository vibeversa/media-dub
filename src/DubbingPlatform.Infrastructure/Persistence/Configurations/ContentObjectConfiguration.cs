using DubbingPlatform.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DubbingPlatform.Infrastructure.Persistence.Configurations;

public sealed class ContentObjectConfiguration : IEntityTypeConfiguration<ContentObject>
{
    public void Configure(EntityTypeBuilder<ContentObject> builder)
    {
        builder.ToTable("content_objects");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
        builder.Property(e => e.ContentHash).IsRequired().HasMaxLength(64);
        builder.Property(e => e.Sha256Hex).IsRequired().HasMaxLength(64);
        builder.Property(e => e.MediaFormat).IsRequired().HasMaxLength(128);
        builder.Property(e => e.StorageKey).IsRequired().HasMaxLength(1024);
        builder.Property(e => e.Status).HasConversion<string>().HasMaxLength(32);
        builder.HasIndex(e => new { e.TenantId, e.ContentHash }).IsUnique();
        builder.HasIndex(e => new { e.TenantId, e.Status });
    }
}
