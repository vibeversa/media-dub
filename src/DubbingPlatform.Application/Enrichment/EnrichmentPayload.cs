using System.Text.Json;
using DubbingPlatform.Application.Abstractions.Providers.Dtos;

namespace DubbingPlatform.Application.Enrichment;

/// <summary>
/// Outcome of one isolated enrichment execution. <c>Failed</c> never affects the
/// core run: the run stays <c>Completed</c>, no <c>RunFailed</c> is published,
/// and the core <c>OutputAsset</c>/download is untouched.
/// </summary>
public enum EnrichmentOutcome
{
    Succeeded,
    Failed,
    Skipped,
}

/// <summary>
/// Pure builders/validators for enrichment payloads (hermetic, no I/O).
/// Face data is tenant-scoped JSON retained per intermediate 30d policy; no
/// cross-tenant access and no biometric export without consent.
/// </summary>
public static class EnrichmentPayload
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Builds the deterministic video-intelligence JSON document (schema v1).
    /// </summary>
    public static string BuildVideoIntelligenceJson(
        Guid runId,
        VideoIntelligenceResponse response,
        string? sourceArtifactId = null)
    {
        ArgumentNullException.ThrowIfNull(response);
        return JsonSerializer.Serialize(new
        {
            schemaVersion = "1",
            runId = runId.ToString("N"),
            sourceArtifactId,
            faceCount = response.Faces.Count,
            faces = response.Faces.Select(f => new
            {
                trackId = f.TrackId,
                startMs = f.StartMs,
                endMs = f.EndMs,
                confidence = f.Confidence,
            }),
            activeSpeakers = response.ActiveSpeakers.Select(s => new
            {
                speakerLabel = s.SpeakerLabel,
                startMs = s.StartMs,
                endMs = s.EndMs,
                confidence = s.Confidence,
            }),
            confidence = response.Confidence,
            provider = "Mock",
            model = response.Model,
            modelVersion = response.ModelVersion,
            deployment = response.Deployment,
        }, JsonOptions);
    }

    /// <summary>
    /// Builds the deterministic lip-sync JSON document (schema v1) carrying
    /// <c>lipSyncScore</c> for the optional mouth-transform preview.
    /// </summary>
    public static string BuildLipSyncJson(
        Guid runId,
        double lipSyncScore,
        int durationMs,
        string? sourceArtifactId = null)
    {
        return JsonSerializer.Serialize(new
        {
            schemaVersion = "1",
            runId = runId.ToString("N"),
            sourceArtifactId,
            lipSyncScore,
            durationMs,
        }, JsonOptions);
    }

    /// <summary>
    /// Post-validates a lip-sync preview: decodable AND duration within
    /// tolerance of the source. Returns false (enrichment Failed, core
    /// untouched) when undecodable or outside tolerance.
    /// </summary>
    public static bool IsLipSyncOutputValid(
        bool decodable,
        long outputDurationMs,
        long sourceDurationMs,
        double toleranceMs)
    {
        if (!decodable)
        {
            return false;
        }

        if (outputDurationMs < 0 || sourceDurationMs < 0 || double.IsNaN(toleranceMs) || toleranceMs < 0)
        {
            return false;
        }

        return Math.Abs(outputDurationMs - sourceDurationMs) <= toleranceMs;
    }

    /// <summary>
    /// Tolerance for enrichment previews: video 1 frame at 25fps (40ms) plus
    /// 100ms audio-only slack, matching core render tolerances loosely without
    /// coupling to <c>RenderService</c>.
    /// </summary>
    public static double PreviewToleranceMs(bool hasVideo)
    {
        return hasVideo ? 140.0 : 100.0;
    }
}
