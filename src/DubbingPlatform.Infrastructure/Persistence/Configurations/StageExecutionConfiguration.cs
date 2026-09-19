using DubbingPlatform.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DubbingPlatform.Infrastructure.Persistence.Configurations;

public sealed class StageExecutionConfiguration : IEntityTypeConfiguration<StageExecution>
{
    public void Configure(EntityTypeBuilder<StageExecution> builder)
    {
        builder.ToTable("stage_executions");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
        builder.Property(e => e.StageType).HasConversion<string>().HasMaxLength(32);
        builder.Property(e => e.ScopeType).HasConversion<string>().HasMaxLength(32);
        builder.Property(e => e.ScopeId).IsRequired().HasMaxLength(256);
        builder.Property(e => e.Status).HasConversion<string>().HasMaxLength(32);
        builder.Property(e => e.LeaseOwner).IsRequired().HasMaxLength(256);
        builder.Property(e => e.LeaseToken).IsRequired().HasMaxLength(256);
        builder.Property(e => e.InputHash).HasMaxLength(64);
        builder.Property(e => e.ConfigurationHash).IsRequired().HasMaxLength(64);
        builder.Property(e => e.ExecutionSnapshotHash).IsRequired().HasMaxLength(64);
        builder.Property(e => e.OutputArtifactIdsJson).HasColumnType("jsonb");
        builder.Property(e => e.ErrorCode).HasMaxLength(128);
        builder.HasIndex(e => new { e.ProcessingRunId, e.StageType, e.ScopeType, e.ScopeId, e.Attempt }).IsUnique();
        builder.HasIndex(e => new { e.ProcessingRunId, e.StageType, e.Status });
        builder.HasIndex(e => new { e.ProcessingRunId, e.Status, e.LeaseExpiresAt });
        builder.HasIndex(e => e.Status).HasFilter("status = 'Running'");
        builder.HasIndex(e => new { e.TenantId, e.ProjectId });
    }
}
