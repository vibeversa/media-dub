using DubbingPlatform.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DubbingPlatform.Infrastructure.Persistence.Configurations;

public sealed class DeletionJobConfiguration : IEntityTypeConfiguration<DeletionJob>
{
    public void Configure(EntityTypeBuilder<DeletionJob> builder)
    {
        builder.ToTable("deletion_jobs");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
        builder.Property(e => e.Scope).IsRequired().HasMaxLength(256);
        builder.Property(e => e.Status).IsRequired().HasMaxLength(32);
        builder.HasIndex(e => new { e.TenantId, e.Status, e.CreatedAt });
    }
}
