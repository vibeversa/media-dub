using DubbingPlatform.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DubbingPlatform.Infrastructure.Persistence.Configurations;

public sealed class DubbingProjectConfiguration : IEntityTypeConfiguration<DubbingProject>
{
    public void Configure(EntityTypeBuilder<DubbingProject> builder)
    {
        builder.ToTable("dubbing_projects");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
        builder.Property(e => e.SourceLanguage).IsRequired().HasMaxLength(8);
        builder.Property(e => e.TargetLanguage).IsRequired().HasMaxLength(8);
        builder.Property(e => e.Status).HasConversion<string>().HasMaxLength(32);
        builder.Property(e => e.SettingsJson).IsRequired().HasColumnType("jsonb");
        builder.Property(e => e.ConfigurationHash).IsRequired().HasMaxLength(64);
        builder.Property<bool>("IsDeleted").HasDefaultValue(false);
        builder.HasIndex(e => new { e.TenantId, e.Status });
        builder.HasIndex(e => new { e.TenantId, e.CreatedAt });
    }
}
