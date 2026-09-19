using DubbingPlatform.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DubbingPlatform.Infrastructure.Persistence.Configurations;

public sealed class VoiceProfileConfiguration : IEntityTypeConfiguration<VoiceProfile>
{
    public void Configure(EntityTypeBuilder<VoiceProfile> builder)
    {
        builder.ToTable("voice_profiles");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
        builder.Property(e => e.Provider).IsRequired().HasMaxLength(128);
        builder.Property(e => e.VoiceId).IsRequired().HasMaxLength(256);
        builder.Property(e => e.VoiceVersion).IsRequired().HasMaxLength(64);
        builder.Property(e => e.Language).IsRequired().HasMaxLength(8);
        builder.Property(e => e.Type).HasConversion<string>().HasMaxLength(32);
        builder.Property(e => e.ModelRefJson).HasColumnType("jsonb");
        builder.HasIndex(e => new { e.TenantId, e.Provider });
    }
}
