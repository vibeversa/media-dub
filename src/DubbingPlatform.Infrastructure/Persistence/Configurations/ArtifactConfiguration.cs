using DubbingPlatform.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DubbingPlatform.Infrastructure.Persistence.Configurations;

public sealed class ArtifactConfiguration : IEntityTypeConfiguration<Artifact>
{
    public void Configure(EntityTypeBuilder<Artifact> builder)
    {
        builder.ToTable("artifacts");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
        builder.Property(e => e.Type).HasConversion<string>().HasMaxLength(32);
        builder.Property(e => e.SchemaVersion).HasMaxLength(64).HasDefaultValue("1");
        builder.Property(e => e.Provider).HasMaxLength(128);
        builder.Property(e => e.Model).HasMaxLength(128);
        builder.Property(e => e.ConfigurationHash).HasMaxLength(64);
        builder.Property(e => e.ExecutionSnapshotHash).HasMaxLength(64);
        builder.Property(e => e.Status).HasConversion<string>().HasMaxLength(32);
        builder.Property(e => e.MetadataJson).HasColumnType("jsonb");
        builder.HasIndex(e => e.ContentObjectId);
        builder.HasIndex(e => new { e.TenantId, e.ProjectId, e.ProcessingRunId });
    }
}
