using DubbingPlatform.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DubbingPlatform.Infrastructure.Persistence.Configurations;

public sealed class IdempotencyRecordConfiguration : IEntityTypeConfiguration<IdempotencyRecord>
{
    public void Configure(EntityTypeBuilder<IdempotencyRecord> builder)
    {
        builder.ToTable("idempotency_records");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
        builder.Property(e => e.Endpoint).IsRequired().HasMaxLength(512);
        builder.Property(e => e.IdempotencyKey).IsRequired().HasMaxLength(256);
        builder.Property(e => e.RequestHash).IsRequired().HasMaxLength(64);
        builder.Property(e => e.State).IsRequired().HasMaxLength(32);
        builder.Property(e => e.ResponseStatus).HasMaxLength(16);
        builder.HasIndex(e => new { e.TenantId, e.Endpoint, e.IdempotencyKey }).IsUnique();
        builder.HasIndex(e => new { e.TenantId, e.State, e.ExpiresAt });
    }
}
