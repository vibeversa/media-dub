using DubbingPlatform.Application.Voices;
using DubbingPlatform.Contracts.Messages;
using MassTransit;

namespace DubbingPlatform.Infrastructure.Messaging;

/// <summary>
/// MassTransit-backed <see cref="ISpeakerVoiceEventPublisher"/>: publishes
/// <see cref="SpeakerVoiceChanged"/> invalidation events. The service calls
/// this after committing the assignment; publish failures propagate so the
/// caller observes them (same convention as segment selection).
/// </summary>
public sealed class MassTransitSpeakerVoiceEventPublisher : ISpeakerVoiceEventPublisher
{
    private readonly IPublishEndpoint _publish;

    public MassTransitSpeakerVoiceEventPublisher(IPublishEndpoint publish)
    {
        ArgumentNullException.ThrowIfNull(publish);
        _publish = publish;
    }

    public Task PublishAsync(SpeakerVoiceChanged message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        return _publish.Publish(message, cancellationToken);
    }
}
