using DubbingPlatform.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DubbingPlatform.Infrastructure.Persistence.Configurations;

public sealed class ReviewItemConfiguration : IEntityTypeConfiguration<ReviewItem>
{
    public void Configure(EntityTypeBuilder<ReviewItem> builder)
    {
        builder.ToTable("review_items");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
        builder.Property(e => e.ScopeType).HasConversion<string>().HasMaxLength(32);
        builder.Property(e => e.ScopeId).IsRequired().HasMaxLength(256);
        builder.Property(e => e.Status).HasConversion<string>().HasMaxLength(32);
        builder.Property(e => e.PayloadJson).HasColumnType("jsonb");
        builder.HasIndex(e => new { e.TenantId, e.Status, e.CreatedAt });
        builder.HasIndex(e => new { e.TenantId, e.ProjectId, e.ProcessingRunId });
    }
}
