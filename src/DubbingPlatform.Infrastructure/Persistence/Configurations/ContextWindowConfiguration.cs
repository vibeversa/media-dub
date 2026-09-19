using DubbingPlatform.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DubbingPlatform.Infrastructure.Persistence.Configurations;

public sealed class ContextWindowConfiguration : IEntityTypeConfiguration<ContextWindow>
{
    public void Configure(EntityTypeBuilder<ContextWindow> builder)
    {
        builder.ToTable("context_windows");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
        builder.Property(e => e.ContextHash).IsRequired().HasMaxLength(64);
        builder.HasIndex(e => new { e.TenantId, e.RunId, e.Sequence }).IsUnique();
        builder.HasIndex(e => new { e.TenantId, e.ProjectId });
    }
}
