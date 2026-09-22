using DubbingPlatform.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DubbingPlatform.Infrastructure.Persistence.Configurations;

public sealed class VoicePreviewJobConfiguration : IEntityTypeConfiguration<VoicePreviewJob>
{
    public void Configure(EntityTypeBuilder<VoicePreviewJob> builder)
    {
        builder.ToTable("voice_preview_jobs");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
        builder.Property(e => e.VoiceId).IsRequired().HasMaxLength(256);
        builder.Property(e => e.Text).IsRequired().HasMaxLength(500);
        builder.Property(e => e.Status).HasConversion<string>().HasMaxLength(32);
        builder.Property(e => e.IdempotencyKey).HasMaxLength(128);
        builder.Property(e => e.QuotaCheck).HasConversion<string>().HasMaxLength(16);
        builder.Property(e => e.QuotaCheckReason).HasMaxLength(1024);
        builder.Property(e => e.ConsentState).HasConversion<string>().HasMaxLength(16);
        builder.Property(e => e.ErrorCode).HasMaxLength(64);
        builder.Property(e => e.ErrorMessage).HasMaxLength(1024);
        builder.HasIndex(e => new { e.TenantId, e.ProjectId, e.Status });
        builder.HasIndex(e => new { e.TenantId, e.SpeakerId, e.CreatedAt });
        builder.HasIndex(e => new { e.TenantId, e.IdempotencyKey })
            .IsUnique()
            .HasFilter("idempotency_key IS NOT NULL");
    }
}
