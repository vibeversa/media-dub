using DubbingPlatform.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DubbingPlatform.Infrastructure.Persistence.Configurations;

public sealed class SpeakerConfiguration : IEntityTypeConfiguration<Speaker>
{
    public void Configure(EntityTypeBuilder<Speaker> builder)
    {
        builder.ToTable("speakers");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
        builder.Property(e => e.SpeakerKey).IsRequired().HasMaxLength(128);
        builder.Property(e => e.DisplayName).IsRequired().HasMaxLength(256);
        builder.Property(e => e.MappingMethod).IsRequired().HasMaxLength(128);
        builder.Property(e => e.MappingVersion).IsRequired().HasMaxLength(64);
        builder.Property(e => e.ProviderLabel).HasMaxLength(256);
        builder.HasIndex(e => new { e.TenantId, e.ProjectId, e.SpeakerKey }).IsUnique();
    }
}
