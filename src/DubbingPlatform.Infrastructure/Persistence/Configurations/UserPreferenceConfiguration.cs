using DubbingPlatform.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DubbingPlatform.Infrastructure.Persistence.Configurations;

public sealed class UserPreferenceConfiguration : IEntityTypeConfiguration<UserPreference>
{
    public void Configure(EntityTypeBuilder<UserPreference> builder)
    {
        builder.ToTable("user_preferences");
        builder.HasKey(e => new { e.TenantId, e.UserId, e.Key });
        builder.Property(e => e.Key).IsRequired().HasMaxLength(128);
        builder.Property(e => e.ValueJson).IsRequired().HasColumnType("jsonb");
        builder.HasIndex(e => new { e.TenantId, e.UserId });
    }
}
