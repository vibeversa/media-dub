using DubbingPlatform.Application.Segments;
using DubbingPlatform.Contracts.Messages;
using MassTransit;

namespace DubbingPlatform.Infrastructure.Messaging;

/// <summary>
/// MassTransit-backed <see cref="ISegmentSelectionEventPublisher"/>: publishes
/// <see cref="SegmentSelectionChanged"/> invalidation events. The service calls
/// this after the selection transaction commits; in controller request scopes
/// the EF outbox captures the publish for at-least-once delivery.
/// </summary>
public sealed class MassTransitSegmentSelectionEventPublisher : ISegmentSelectionEventPublisher
{
    private readonly IPublishEndpoint _publish;

    public MassTransitSegmentSelectionEventPublisher(IPublishEndpoint publish)
    {
        ArgumentNullException.ThrowIfNull(publish);
        _publish = publish;
    }

    public Task PublishAsync(SegmentSelectionChanged message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        return _publish.Publish(message, cancellationToken);
    }
}
