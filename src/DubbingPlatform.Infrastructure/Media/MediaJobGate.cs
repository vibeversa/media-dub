using DubbingPlatform.Application.Options;
using Microsoft.Extensions.Options;

namespace DubbingPlatform.Infrastructure.Media;

/// <summary>
/// Process-wide FFmpeg concurrency gate. A singleton <c>SemaphoreSlim</c>
/// sized from <c>Media:MaxConcurrentMediaJobs</c> at construction; audio
/// preparation acquires one slot per FFmpeg execution so concurrent stage
/// deliveries never exceed the configured parallelism. Workers share the
/// exposed <see cref="Semaphore"/>.
/// </summary>
public sealed class MediaJobGate
{
    public MediaJobGate(IOptions<MediaOptions> media)
    {
        ArgumentNullException.ThrowIfNull(media);
        var max = Math.Clamp(media.Value.MaxConcurrentMediaJobs, 1, 64);
        Semaphore = new SemaphoreSlim(max, 64);
    }

    public SemaphoreSlim Semaphore { get; }
}
