using DubbingPlatform.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DubbingPlatform.Infrastructure.Persistence.Configurations;

public sealed class PromptTemplateVersionConfiguration : IEntityTypeConfiguration<PromptTemplateVersion>
{
    public void Configure(EntityTypeBuilder<PromptTemplateVersion> builder)
    {
        builder.ToTable("prompt_template_versions");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
        builder.Property(e => e.SafetySettingsJson).IsRequired().HasColumnType("jsonb");
        builder.Property(e => e.PromptHash).IsRequired().HasMaxLength(64);
        builder.HasIndex(e => new { e.PromptTemplateId, e.Version }).IsUnique();
        builder.HasIndex(e => new { e.TenantId, e.PromptTemplateId });
    }
}
