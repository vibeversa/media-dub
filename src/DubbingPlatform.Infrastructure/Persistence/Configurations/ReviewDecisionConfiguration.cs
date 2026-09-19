using DubbingPlatform.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DubbingPlatform.Infrastructure.Persistence.Configurations;

public sealed class ReviewDecisionConfiguration : IEntityTypeConfiguration<ReviewDecision>
{
    public void Configure(EntityTypeBuilder<ReviewDecision> builder)
    {
        builder.ToTable("review_decisions");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
        builder.Property(e => e.Type).HasConversion<string>().HasMaxLength(32);
        builder.Property(e => e.Reviewer).IsRequired().HasMaxLength(256);
        builder.Property(e => e.MetadataJson).HasColumnType("jsonb");
        builder.HasIndex(e => e.ReviewItemId);
    }
}
