using DubbingPlatform.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DubbingPlatform.Infrastructure.Persistence.Configurations;

public sealed class ProcessingPolicyConfiguration : IEntityTypeConfiguration<ProcessingPolicy>
{
    public void Configure(EntityTypeBuilder<ProcessingPolicy> builder)
    {
        builder.ToTable("processing_policies");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
        builder.Property(e => e.ResidencyConstraint).HasMaxLength(256);
        builder.Property(e => e.SensitivePolicy).IsRequired().HasMaxLength(256);
        builder.Property(e => e.VoicePolicy).IsRequired().HasMaxLength(256);
        builder.Property(e => e.RetentionOverride).HasMaxLength(1024);
        builder.HasIndex(e => e.TenantId).IsUnique();
    }
}
