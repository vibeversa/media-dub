using DubbingPlatform.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DubbingPlatform.Infrastructure.Persistence.Configurations;

public sealed class SpeakerVoiceAssignmentConfiguration : IEntityTypeConfiguration<SpeakerVoiceAssignment>
{
    public void Configure(EntityTypeBuilder<SpeakerVoiceAssignment> builder)
    {
        builder.ToTable("speaker_voice_assignments");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
        builder.Property(e => e.AssignmentReason).IsRequired().HasMaxLength(512);
        builder.Property(e => e.PolicyHash).IsRequired().HasMaxLength(64);
        builder.HasIndex(e => new { e.TenantId, e.RunId, e.SpeakerId }).IsUnique();
        builder.HasIndex(e => new { e.TenantId, e.ProjectId });
    }
}
