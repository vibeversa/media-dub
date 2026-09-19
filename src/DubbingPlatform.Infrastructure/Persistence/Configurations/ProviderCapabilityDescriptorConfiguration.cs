using DubbingPlatform.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DubbingPlatform.Infrastructure.Persistence.Configurations;

public sealed class ProviderCapabilityDescriptorConfiguration : IEntityTypeConfiguration<ProviderCapabilityDescriptor>
{
    public void Configure(EntityTypeBuilder<ProviderCapabilityDescriptor> builder)
    {
        builder.ToTable("provider_capability_descriptors");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
        builder.Property(e => e.Provider).HasConversion<string>().HasMaxLength(32);
        builder.Property(e => e.Capability).HasConversion<string>().HasMaxLength(32);
        builder.Property(e => e.ConfidenceSemantics).IsRequired().HasMaxLength(1024);
        builder.Property(e => e.RateLimitDimsJson).IsRequired().HasColumnType("jsonb");
        builder.Property(e => e.CostDimsJson).IsRequired().HasColumnType("jsonb");
        builder.Property(e => e.PrivacyClass).IsRequired().HasMaxLength(64);
        builder.Property(e => e.Region).IsRequired().HasMaxLength(128);
        builder.HasIndex(e => new { e.TenantId, e.Provider, e.Capability, e.Version }).IsUnique();
    }
}
