using DubbingPlatform.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DubbingPlatform.Infrastructure.Persistence.Configurations;

public sealed class ConsentRecordConfiguration : IEntityTypeConfiguration<ConsentRecord>
{
    public void Configure(EntityTypeBuilder<ConsentRecord> builder)
    {
        builder.ToTable("consent_records");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
        builder.Property(e => e.SubjectIdentity).IsRequired().HasMaxLength(256);
        builder.Property(e => e.EvidenceReference).IsRequired().HasMaxLength(1024);
        builder.Property(e => e.Scope).IsRequired().HasMaxLength(256);
        builder.Property(e => e.Jurisdiction).IsRequired().HasMaxLength(16);
        builder.Property(e => e.Status).HasConversion<string>().HasMaxLength(32);
        builder.HasIndex(e => new { e.TenantId, e.Status });
        builder.HasIndex(e => new { e.TenantId, e.SubjectIdentity });
    }
}
