using DubbingPlatform.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DubbingPlatform.Infrastructure.Persistence.Configurations;

public sealed class TranslationVersionConfiguration : IEntityTypeConfiguration<TranslationVersion>
{
    public void Configure(EntityTypeBuilder<TranslationVersion> builder)
    {
        builder.ToTable("translation_versions");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
        builder.Property(e => e.Provider).IsRequired().HasMaxLength(128);
        builder.Property(e => e.Model).IsRequired().HasMaxLength(128);
        builder.Property(e => e.PromptHash).HasMaxLength(64);
        builder.HasIndex(e => new { e.TenantId, e.ProjectId });
        builder.HasIndex(e => new { e.ProjectId, e.SegmentId });
    }
}
