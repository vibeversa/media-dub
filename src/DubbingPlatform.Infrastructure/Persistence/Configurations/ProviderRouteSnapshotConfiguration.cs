using DubbingPlatform.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DubbingPlatform.Infrastructure.Persistence.Configurations;

public sealed class ProviderRouteSnapshotConfiguration : IEntityTypeConfiguration<ProviderRouteSnapshot>
{
    public void Configure(EntityTypeBuilder<ProviderRouteSnapshot> builder)
    {
        builder.ToTable("provider_route_snapshots");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
        builder.Property(e => e.RouteConfigHash).IsRequired().HasMaxLength(64);
        builder.Property(e => e.CapabilityHash).IsRequired().HasMaxLength(64);
        builder.Property(e => e.PrivacyHash).IsRequired().HasMaxLength(64);
        builder.HasIndex(e => new { e.TenantId, e.ProcessingRunId }).IsUnique();
        builder.HasIndex(e => new { e.TenantId, e.ProjectId });
    }
}
