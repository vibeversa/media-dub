using DubbingPlatform.Domain.Enums;
using DubbingPlatform.Domain.Exceptions;

namespace DubbingPlatform.Domain.Entities;

public sealed class MediaAsset
{
    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid ProjectId { get; private set; }

    public Guid ContentObjectId { get; private set; }

    public string FileName { get; private set; }

    public string Container { get; private set; }

    public string AudioCodec { get; private set; }

    public string? VideoCodec { get; private set; }

    public long SizeBytes { get; private set; }

    public int DurationMs { get; private set; }

    public int SampleRate { get; private set; }

    public int Channels { get; private set; }

    public string ChannelLayout { get; private set; }

    public MediaAssetStatus Status { get; private set; }

    public string? FailureReason { get; private set; }

    public string ContentHash { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    private MediaAsset()
    {
        FileName = string.Empty;
        Container = string.Empty;
        AudioCodec = string.Empty;
        ChannelLayout = string.Empty;
        ContentHash = string.Empty;
    }

    public MediaAsset(
        Guid id,
        Guid tenantId,
        Guid projectId,
        Guid contentObjectId,
        string fileName,
        string container,
        string audioCodec,
        string? videoCodec,
        long sizeBytes,
        int durationMs,
        int sampleRate,
        int channels,
        string channelLayout,
        MediaAssetStatus status,
        string? failureReason,
        string contentHash,
        DateTimeOffset createdAt)
    {
        Id = id;
        TenantId = tenantId;
        ProjectId = projectId;
        ContentObjectId = contentObjectId;
        FileName = fileName;
        Container = container;
        AudioCodec = audioCodec;
        VideoCodec = videoCodec;
        SizeBytes = sizeBytes;
        DurationMs = durationMs;
        SampleRate = sampleRate;
        Channels = channels;
        ChannelLayout = channelLayout;
        Status = status;
        FailureReason = failureReason;
        ContentHash = contentHash;
        CreatedAt = createdAt;

        Validate();
    }

    public void Validate()
    {
        if (Id == Guid.Empty)
        {
            throw new DomainException("MediaAsset Id must not be empty.");
        }

        if (TenantId == Guid.Empty)
        {
            throw new DomainException("MediaAsset TenantId must not be empty.");
        }

        if (ProjectId == Guid.Empty)
        {
            throw new DomainException("MediaAsset ProjectId must not be empty.");
        }

        if (ContentObjectId == Guid.Empty)
        {
            throw new DomainException("MediaAsset ContentObjectId must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(FileName))
        {
            throw new DomainException("MediaAsset FileName must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(Container))
        {
            throw new DomainException("MediaAsset Container must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(AudioCodec))
        {
            throw new DomainException("MediaAsset AudioCodec must not be empty.");
        }

        if (SizeBytes < 0)
        {
            throw new DomainException("MediaAsset SizeBytes must be >= 0.");
        }

        if (DurationMs < 0)
        {
            throw new DomainException("MediaAsset DurationMs must be >= 0.");
        }

        if (SampleRate <= 0)
        {
            throw new DomainException("MediaAsset SampleRate must be > 0.");
        }

        if (Channels <= 0)
        {
            throw new DomainException("MediaAsset Channels must be > 0.");
        }

        if (string.IsNullOrWhiteSpace(ChannelLayout))
        {
            throw new DomainException("MediaAsset ChannelLayout must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(ContentHash))
        {
            throw new DomainException("MediaAsset ContentHash must not be empty.");
        }
    }
}
