using DubbingPlatform.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DubbingPlatform.Infrastructure.Persistence.Configurations;

public sealed class ProcessingRunConfiguration : IEntityTypeConfiguration<ProcessingRun>
{
    public void Configure(EntityTypeBuilder<ProcessingRun> builder)
    {
        builder.ToTable("processing_runs");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
        builder.Property(e => e.Status).HasConversion<string>().HasMaxLength(32);
        builder.Property(e => e.PipelineVersion).IsRequired().HasMaxLength(128);
        builder.Property(e => e.ConfigurationHash).IsRequired().HasMaxLength(64);
        builder.Property(e => e.ProviderRouteHash).IsRequired().HasMaxLength(64);
        builder.Property(e => e.ExecutionSnapshotHash).IsRequired().HasMaxLength(64);
        builder.HasIndex(e => e.ProjectId)
            .IsUnique()
            .HasFilter("status IN ('Pending','Running','Cancelling','ManualReviewRequired')");
        builder.HasIndex(e => new { e.TenantId, e.ProjectId });
        builder.HasIndex(e => new { e.TenantId, e.Status, e.CreatedAt });
    }
}
