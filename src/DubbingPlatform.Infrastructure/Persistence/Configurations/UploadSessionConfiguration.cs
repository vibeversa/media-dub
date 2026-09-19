using DubbingPlatform.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DubbingPlatform.Infrastructure.Persistence.Configurations;

public sealed class UploadSessionConfiguration : IEntityTypeConfiguration<UploadSession>
{
    public void Configure(EntityTypeBuilder<UploadSession> builder)
    {
        builder.ToTable("upload_sessions");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
        builder.Property(e => e.FileName).IsRequired().HasMaxLength(512);
        builder.Property(e => e.DeclaredContentType).IsRequired().HasMaxLength(256);
        builder.Property(e => e.StorageKey).IsRequired().HasMaxLength(1024);
        builder.Property(e => e.MultipartUploadId).HasMaxLength(512);
        builder.Property(e => e.Status).HasConversion<string>().HasMaxLength(32);
        builder.Property(e => e.ClientSha256Hex).HasMaxLength(64);
        builder.Property(e => e.ContentHash).HasMaxLength(64);
        builder.HasIndex(e => new { e.TenantId, e.ProjectId });
        builder.HasIndex(e => new { e.TenantId, e.Status, e.ExpiresAt });
    }
}
