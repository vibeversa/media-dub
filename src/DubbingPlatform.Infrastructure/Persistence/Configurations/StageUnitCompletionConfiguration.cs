using DubbingPlatform.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DubbingPlatform.Infrastructure.Persistence.Configurations;

public sealed class StageUnitCompletionConfiguration : IEntityTypeConfiguration<StageUnitCompletion>
{
    public void Configure(EntityTypeBuilder<StageUnitCompletion> builder)
    {
        builder.ToTable("stage_unit_completions");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
        builder.Property(e => e.StageType).HasConversion<string>().HasMaxLength(32);
        builder.Property(e => e.ScopeType).HasConversion<string>().HasMaxLength(32);
        builder.Property(e => e.ScopeId).IsRequired().HasMaxLength(256);
        builder.Property(e => e.UnitState).IsRequired().HasMaxLength(32);
        builder.HasIndex(e => new { e.ProcessingRunId, e.StageType, e.ScopeType, e.ScopeId, e.StageExecutionId }).IsUnique();
        builder.HasIndex(e => new { e.ProcessingRunId, e.StageType, e.UnitState });
    }
}
