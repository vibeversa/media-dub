using DubbingPlatform.Application.Previews;
using DubbingPlatform.Contracts.Messages;
using MassTransit;

namespace DubbingPlatform.Infrastructure.Messaging;

/// <summary>
/// MassTransit-backed <see cref="IVoicePreviewEventPublisher"/>: publishes
/// <see cref="VoicePreviewCompleted"/> terminal events. The service calls this
/// after the preview transaction commits; in controller request scopes the EF
/// outbox captures the publish for at-least-once delivery. Task 010 consumes
/// these events at the endpoint layer.
/// </summary>
public sealed class MassTransitVoicePreviewEventPublisher : IVoicePreviewEventPublisher
{
    private readonly IPublishEndpoint _publish;

    public MassTransitVoicePreviewEventPublisher(IPublishEndpoint publish)
    {
        ArgumentNullException.ThrowIfNull(publish);
        _publish = publish;
    }

    public Task PublishAsync(VoicePreviewCompleted message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        return _publish.Publish(message, cancellationToken);
    }
}
