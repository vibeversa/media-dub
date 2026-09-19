using DubbingPlatform.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DubbingPlatform.Infrastructure.Persistence.Configurations;

public sealed class ArtifactParentConfiguration : IEntityTypeConfiguration<ArtifactParent>
{
    public void Configure(EntityTypeBuilder<ArtifactParent> builder)
    {
        builder.ToTable("artifact_parents");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
        builder.HasIndex(e => new { e.ChildArtifactId, e.ParentArtifactId }).IsUnique();
        builder.HasIndex(e => e.ParentArtifactId);
    }
}
