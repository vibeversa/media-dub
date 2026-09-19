using DubbingPlatform.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DubbingPlatform.Infrastructure.Persistence.Configurations;

public sealed class RunStageSummaryConfiguration : IEntityTypeConfiguration<RunStageSummary>
{
    public void Configure(EntityTypeBuilder<RunStageSummary> builder)
    {
        builder.ToTable("run_stage_summaries");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
        builder.Property(e => e.StageType).HasConversion<string>().HasMaxLength(32);
        builder.HasIndex(e => new { e.TenantId, e.ProcessingRunId, e.StageType }).IsUnique();
    }
}
