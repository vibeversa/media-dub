using DubbingPlatform.Application.Previews;
using DubbingPlatform.Infrastructure.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace DubbingPlatform.Infrastructure.Previews;

/// <summary>
/// Preview-lane wiring (Task B-004). Registers the voice preview service, the
/// media preview generator, and the QC evidence linker as scoped, plus the
/// MassTransit-backed completion publisher. Called by both the API host
/// (Task 010 endpoints drive previews) and the worker host (analysis and
/// audio-prep completion hooks generate media previews). All preview
/// dependencies (<c>ArtifactService</c>, <c>ITtsProvider</c>,
/// <c>ProviderExecutionRecorder</c>, <c>AuditService</c>) are registered by
/// the storage/provider/media registrations of each host.
/// </summary>
public static class PreviewRegistration
{
    public static void AddDubbingPreviews(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<MediaPreviewGenerator>();
        services.AddScoped<VoicePreviewService>();
        services.AddScoped<QcEvidenceLinker>();
        services.AddScoped<IVoicePreviewEventPublisher, MassTransitVoicePreviewEventPublisher>();
    }
}
