using DubbingPlatform.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DubbingPlatform.Infrastructure.Persistence.Configurations;

public sealed class ExportArtifactConfiguration : IEntityTypeConfiguration<ExportArtifact>
{
    public void Configure(EntityTypeBuilder<ExportArtifact> builder)
    {
        builder.ToTable("export_artifacts");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
        builder.HasIndex(e => new { e.ExportJobId, e.ArtifactId }).IsUnique();
    }
}
