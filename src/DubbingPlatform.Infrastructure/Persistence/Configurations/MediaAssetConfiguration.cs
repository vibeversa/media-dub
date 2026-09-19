using DubbingPlatform.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DubbingPlatform.Infrastructure.Persistence.Configurations;

public sealed class MediaAssetConfiguration : IEntityTypeConfiguration<MediaAsset>
{
    public void Configure(EntityTypeBuilder<MediaAsset> builder)
    {
        builder.ToTable("media_assets");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
        builder.Property(e => e.FileName).IsRequired().HasMaxLength(512);
        builder.Property(e => e.Container).IsRequired().HasMaxLength(64);
        builder.Property(e => e.AudioCodec).IsRequired().HasMaxLength(64);
        builder.Property(e => e.VideoCodec).HasMaxLength(64);
        builder.Property(e => e.ChannelLayout).IsRequired().HasMaxLength(64);
        builder.Property(e => e.Status).HasConversion<string>().HasMaxLength(32);
        builder.Property(e => e.FailureReason).HasMaxLength(1024);
        builder.Property(e => e.ContentHash).IsRequired().HasMaxLength(64);
        builder.HasIndex(e => new { e.TenantId, e.ProjectId });
        builder.HasIndex(e => new { e.TenantId, e.ContentHash });
    }
}
