using DubbingPlatform.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DubbingPlatform.Infrastructure.Persistence.Configurations;

public sealed class StageOutputArtifactConfiguration : IEntityTypeConfiguration<StageOutputArtifact>
{
    public void Configure(EntityTypeBuilder<StageOutputArtifact> builder)
    {
        builder.ToTable("stage_output_artifacts");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
        builder.HasIndex(e => new { e.StageExecutionId, e.ArtifactId }).IsUnique();
        builder.HasIndex(e => e.ArtifactId);
    }
}
