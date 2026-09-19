using DubbingPlatform.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DubbingPlatform.Infrastructure.Persistence.Configurations;

public sealed class OutputAssetConfiguration : IEntityTypeConfiguration<OutputAsset>
{
    public void Configure(EntityTypeBuilder<OutputAsset> builder)
    {
        builder.ToTable("output_assets");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
        builder.Property(e => e.MediaKind).IsRequired().HasMaxLength(16);
        builder.Property(e => e.Container).IsRequired().HasMaxLength(64);
        builder.HasIndex(e => new { e.TenantId, e.ProcessingRunId, e.ArtifactId }).IsUnique();
        builder.HasIndex(e => new { e.TenantId, e.ProjectId });
    }
}
