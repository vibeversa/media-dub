using DubbingPlatform.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DubbingPlatform.Infrastructure.Persistence.Configurations;

public sealed class CostReservationConfiguration : IEntityTypeConfiguration<CostReservation>
{
    public void Configure(EntityTypeBuilder<CostReservation> builder)
    {
        builder.ToTable("cost_reservations");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
        builder.Property(e => e.Capability).HasConversion<string>().HasMaxLength(32);
        builder.Property(e => e.Currency).IsRequired().HasMaxLength(8);
        builder.Property(e => e.PriceTableVersion).IsRequired().HasMaxLength(64);
        builder.Property(e => e.State).IsRequired().HasMaxLength(32);
        builder.HasIndex(e => new { e.TenantId, e.ProjectId });
        builder.HasIndex(e => new { e.TenantId, e.State });
    }
}
