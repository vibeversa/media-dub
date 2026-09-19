using DubbingPlatform.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DubbingPlatform.Infrastructure.Persistence.Configurations;

public sealed class SyncResultConfiguration : IEntityTypeConfiguration<SyncResult>
{
    public void Configure(EntityTypeBuilder<SyncResult> builder)
    {
        builder.ToTable("sync_results");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
        builder.Property(e => e.Status).HasConversion<string>().HasMaxLength(32);
        builder.HasIndex(e => new { e.TenantId, e.RunId, e.SegmentId }).IsUnique();
        builder.HasIndex(e => new { e.TenantId, e.ProjectId });
    }
}
