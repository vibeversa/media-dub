namespace DubbingPlatform.Api.Models;

/// <summary>
/// Task 010 speaker summary (list rows + detail). <c>AssignedVoice</c> is null
/// when no stable voice is assigned yet.
/// </summary>
public sealed record SpeakerSummaryResponse(
    string Id,
    string ProjectId,
    string SpeakerKey,
    string DisplayName,
    int SegmentCount,
    SpeakerAssignedVoiceResponse? AssignedVoice);

/// <summary>
/// Assigned-voice pointer for a speaker.
/// </summary>
public sealed record SpeakerAssignedVoiceResponse(
    string VoiceProfileId,
    string VoiceId,
    string Provider,
    string Language,
    string Type);

/// <summary>
/// Speaker detail (summary plus mapping metadata + voice pointer).
/// </summary>
public sealed record SpeakerDetailResponse(
    string Id,
    string ProjectId,
    string SpeakerKey,
    string DisplayName,
    int FirstAppearanceMs,
    int LastAppearanceMs,
    double Confidence,
    int SegmentCount,
    SpeakerAssignedVoiceResponse? AssignedVoice);

/// <summary>
/// Compatible voice entry for available-voices (selectable).
/// </summary>
public sealed record AvailableVoiceResponse(
    string VoiceProfileId,
    string VoiceId,
    string Provider,
    string Language,
    string Type,
    bool CloningEnabled);

/// <summary>
/// Excluded voice entry (never selectable; reasons explain why).
/// </summary>
public sealed record ExcludedVoiceResponse(
    string VoiceId,
    IReadOnlyList<string> Reasons);

/// <summary>
/// Available-voices envelope: compatible-only list plus exclusion summary.
/// Example: <c>{ voices: [...], excludedCount: 1, excluded: [{ voiceId,
/// reasons }] }</c>.
/// </summary>
public sealed record AvailableVoicesResponse(
    IReadOnlyList<AvailableVoiceResponse> Voices,
    int ExcludedCount,
    IReadOnlyList<ExcludedVoiceResponse> Excluded);

/// <summary>
/// Voice-assignment request body: <c>{ voiceId, reason? }</c>. <c>VoiceId</c>
/// accepts a public id (<c>voice_</c>), raw GUID, or inventory voice key.
/// <c>Reason</c> nullable, max 500, sanitized (HTML stripped).
/// Example: <c>{ voiceId: "voice_...", reason: "prefer warmer tone" }</c>.
/// </summary>
public sealed class VoiceAssignmentRequest
{
    public string? VoiceId { get; set; }

    public string? Reason { get; set; }
}

/// <summary>
/// Voice-assignment response. Same-voice calls return <c>changed:false</c>
/// with no invalidation; changes return <c>outputStale:true</c> +
/// <c>OUTPUT_STALE</c> when final output exists.
/// </summary>
public sealed record VoiceAssignmentResponse(
    string SpeakerId,
    string VoiceProfileId,
    string VoiceId,
    bool Changed,
    bool OutputStale,
    string? WarningCode,
    bool UnusedSpeaker,
    string? OldVoiceProfileId);

/// <summary>
/// Voice-preview creation body: <c>{ speakerId, voiceId, text? }</c>.
/// <c>SpeakerId</c> accepts <c>spk_</c> or raw GUID; <c>voiceId</c> accepts
/// <c>voice_</c>, raw GUID, or inventory key. <c>Text</c> defaults to a short
/// sample line when omitted (1..500 chars; empty/overlong → 400
/// <c>PREVIEW_TEXT_INVALID</c>). Idempotency via <c>Idempotency-Key</c> header.
/// </summary>
public sealed class CreateVoicePreviewRequest
{
    public string? SpeakerId { get; set; }

    public string? VoiceId { get; set; }

    public string? Text { get; set; }
}

/// <summary>
/// Voice-preview creation response (202 first, 200 duplicate).
/// </summary>
public sealed record VoicePreviewResponse(
    string PreviewId,
    string Status,
    bool IsDuplicate);

/// <summary>
/// Voice-preview list item (no signed URL; detail mints one when Completed).
/// </summary>
public sealed record VoicePreviewSummaryResponse(
    string PreviewId,
    string ProjectId,
    string SpeakerId,
    string VoiceId,
    string Status,
    DateTimeOffset CreatedAt);

/// <summary>
/// Voice-preview detail (status + artifact reference when Completed;
/// <c>downloadUrl</c> is a 15-minute presigned URL, never an internal path).
/// </summary>
public sealed record VoicePreviewDetailResponse(
    string PreviewId,
    string ProjectId,
    string SpeakerId,
    string VoiceId,
    string Status,
    string? ArtifactId,
    string? DownloadUrl,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset CreatedAt);
