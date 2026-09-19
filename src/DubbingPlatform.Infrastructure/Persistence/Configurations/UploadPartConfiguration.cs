using DubbingPlatform.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DubbingPlatform.Infrastructure.Persistence.Configurations;

public sealed class UploadPartConfiguration : IEntityTypeConfiguration<UploadPart>
{
    public void Configure(EntityTypeBuilder<UploadPart> builder)
    {
        builder.ToTable("upload_parts");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
        builder.Property(e => e.ETag).IsRequired().HasMaxLength(256);
        builder.HasIndex(e => new { e.UploadSessionId, e.PartNumber }).IsUnique();
    }
}
