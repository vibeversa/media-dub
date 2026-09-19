using DubbingPlatform.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DubbingPlatform.Infrastructure.Persistence.Configurations;

public sealed class ProviderExecutionConfiguration : IEntityTypeConfiguration<ProviderExecution>
{
    public void Configure(EntityTypeBuilder<ProviderExecution> builder)
    {
        builder.ToTable("provider_executions");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
        builder.Property(e => e.Provider).HasConversion<string>().HasMaxLength(32);
        builder.Property(e => e.Capability).HasConversion<string>().HasMaxLength(32);
        builder.Property(e => e.Model).IsRequired().HasMaxLength(256);
        builder.Property(e => e.ModelVersion).HasMaxLength(64);
        builder.Property(e => e.Deployment).HasMaxLength(256);
        builder.Property(e => e.Region).HasMaxLength(128);
        builder.Property(e => e.ApiVersion).HasMaxLength(64);
        builder.Property(e => e.RequestHash).IsRequired().HasMaxLength(64);
        builder.Property(e => e.ResponseHash).HasMaxLength(64);
        builder.Property(e => e.PriceTableVersion).HasMaxLength(64);
        builder.Property(e => e.Outcome).HasConversion<string>().HasMaxLength(32);
        builder.Property(e => e.FallbackReason).HasMaxLength(1024);
        builder.Property(e => e.PromptHash).HasMaxLength(64);
        builder.Property(e => e.VoiceProfileVersion).HasMaxLength(64);
        builder.Property(e => e.ExternalJobId).HasMaxLength(256);
        builder.Property(e => e.ProviderIdempotencyKey).HasMaxLength(256);
        builder.HasIndex(e => new { e.ProjectId, e.CreatedAt });
        builder.HasIndex(e => new { e.TenantId, e.ProcessingRunId, e.CreatedAt });
    }
}
