using DubbingPlatform.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DubbingPlatform.Infrastructure.Persistence.Configurations;

public sealed class QualityResultConfiguration : IEntityTypeConfiguration<QualityResult>
{
    public void Configure(EntityTypeBuilder<QualityResult> builder)
    {
        builder.ToTable("quality_results");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
        builder.Property(e => e.ScopeType).HasConversion<string>().HasMaxLength(32);
        builder.Property(e => e.ScopeId).IsRequired().HasMaxLength(256);
        builder.Property(e => e.Status).HasConversion<string>().HasMaxLength(32);
        builder.Property(e => e.Code).IsRequired().HasMaxLength(128);
        builder.Property(e => e.Severity).IsRequired().HasMaxLength(32);
        builder.Property(e => e.DetailsJson).HasColumnType("jsonb");
        builder.HasIndex(e => new { e.TenantId, e.ProjectId });
        builder.HasIndex(e => new { e.TenantId, e.ProcessingRunId });
    }
}
