// Task 039C: voice-preview quota and consent unit gap closure.

using System.Text;
using System.Text.Json;
using DubbingPlatform.Application.Abstractions;
using DubbingPlatform.Application.Abstractions.Providers;
using DubbingPlatform.Application.Abstractions.Providers.Dtos;
using DubbingPlatform.Application.Errors;
using DubbingPlatform.Application.Exceptions;
using DubbingPlatform.Application.MultiTenancy;
using DubbingPlatform.Application.Options;
using DubbingPlatform.Application.Previews;
using DubbingPlatform.Application.Providers;
using DubbingPlatform.Application.Services;
using DubbingPlatform.Contracts.Messages;
using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Domain.Enums;
using DubbingPlatform.Domain.Exceptions;
using DubbingPlatform.Domain.Voice;
using DubbingPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace DubbingPlatform.UnitTests.Previews;

/// <summary>
/// Unit coverage for the fast-lane preview lane: the pure
/// <see cref="ConsentGate"/> decision matrix, the per-tenant preview quota
/// evaluated at its exact boundaries (remaining 1, 0, negative, unlimited), the
/// tenant-scoping negative cases, <see cref="PreviewAudio"/> duration math,
/// <see cref="MediaPreviewGenerator"/> payload shape,
/// <see cref="QcEvidenceLinker"/> link construction, and the
/// <see cref="VoicePreviewService"/> consent-before-quota ordering. Entity
/// services run over an InMemory <c>AppDbContext</c> with the tenant query
/// filter active (no containers, no network, no object store); the TTS provider,
/// the event publisher, and blob storage are fakes. No wall-clock assertions:
/// quota windows are driven by the <c>CreatedAt</c> stamp the test seeds.
/// </summary>
public sealed class VoicePreviewQuotaConsentTests
{
    private static readonly DateTimeOffset Granted = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    // ==================================================================
    // ConsentGate — state resolution
    // ==================================================================

    [Fact]
    public void ConsentGate_No_Record_Is_Unknown_And_Blocks()
    {
        Assert.Equal(ConsentState.Unknown, ConsentGate.ResolveState(null));
        Assert.Equal(ConsentDecision.Blocked, ConsentGate.Evaluate(null, Guid.NewGuid(), null));
        Assert.False(ConsentGate.IsUsable(ConsentState.Unknown));
    }

    [Fact]
    public void ConsentGate_Incomplete_Evidence_Cannot_Be_Constructed_And_Always_Fails_Closed()
    {
        // Subject, evidence, scope, and jurisdiction are the four evidence
        // fields. The entity refuses a blank one outright, so the gate's
        // "incomplete evidence" branch is defence in depth only: it can never be
        // reached through a persisted record. The gate still resolves such a
        // record to Unknown and blocks, which is asserted via the status-based
        // Unknown paths below.
        Assert.Equal(
            "ConsentRecord SubjectIdentity must not be empty.",
            Assert.Throws<DomainException>(() =>
                NewConsent(ConsentStatus.Granted, " ", "evidence-ref", "scope:*", "EU", revokedAt: null)).Message);
        Assert.Equal(
            "ConsentRecord EvidenceReference must not be empty.",
            Assert.Throws<DomainException>(() =>
                NewConsent(ConsentStatus.Granted, "subject", " ", "scope:*", "EU", revokedAt: null)).Message);
        Assert.Equal(
            "ConsentRecord Scope must not be empty.",
            Assert.Throws<DomainException>(() =>
                NewConsent(ConsentStatus.Granted, "subject", "evidence-ref", " ", "EU", revokedAt: null)).Message);
        Assert.Equal(
            "ConsentRecord Jurisdiction must not be empty.",
            Assert.Throws<DomainException>(() =>
                NewConsent(ConsentStatus.Granted, "subject", "evidence-ref", "scope:*", " ", revokedAt: null)).Message);
    }

    [Fact]
    public void ConsentGate_Granted_Record_Is_Usable()
    {
        var record = NewConsent(ConsentStatus.Granted, "subject", "evidence", "*", "EU", revokedAt: null);

        Assert.Equal(ConsentState.Granted, ConsentGate.ResolveState(record));
        Assert.True(ConsentGate.IsUsable(ConsentState.Granted));
    }

    [Fact]
    public void ConsentGate_Revoked_Status_Or_Stamp_Resolves_To_Revoked()
    {
        var byStatus = NewConsent(ConsentStatus.Revoked, "subject", "evidence", "*", "EU", revokedAt: null);
        var byStamp = NewConsent(ConsentStatus.Granted, "subject", "evidence", "*", "EU", revokedAt: Granted.AddHours(1));

        Assert.Equal(ConsentState.Revoked, ConsentGate.ResolveState(byStatus));
        Assert.Equal(ConsentState.Revoked, ConsentGate.ResolveState(byStamp));
        Assert.Equal(ConsentDecision.Blocked, ConsentGate.Evaluate(byStamp, Guid.NewGuid(), null));
        Assert.False(ConsentGate.IsUsable(ConsentState.Revoked));
    }

    [Fact]
    public void ConsentGate_Expired_Record_Is_Expired_And_Blocks()
    {
        var record = NewConsent(ConsentStatus.Expired, "subject", "evidence", "*", "EU", revokedAt: null);

        Assert.Equal(ConsentState.Expired, ConsentGate.ResolveState(record));
        Assert.Equal(ConsentDecision.Blocked, ConsentGate.Evaluate(record, Guid.NewGuid(), null));
        Assert.False(ConsentGate.IsUsable(ConsentState.Expired));
    }

    [Fact]
    public void ConsentGate_Pending_Record_Is_Unknown_Not_Granted()
    {
        var record = NewConsent(ConsentStatus.Pending, "subject", "evidence", "*", "EU", revokedAt: null);

        Assert.Equal(ConsentState.Unknown, ConsentGate.ResolveState(record));
        Assert.Equal(ConsentDecision.Blocked, ConsentGate.Evaluate(record, Guid.NewGuid(), null));
    }

    // ==================================================================
    // ConsentGate — decision matrix
    // ==================================================================

    [Fact]
    public void ConsentGate_Evaluate_Allows_Wildcard_And_Project_Scopes()
    {
        var projectId = Guid.NewGuid();
        var voiceId = Guid.NewGuid();
        var wildcard = NewConsent(ConsentStatus.Granted, "s", "e", "*", "EU", voiceId, revokedAt: null);
        var byNForm = NewConsent(ConsentStatus.Granted, "s", "e", $"project:{projectId:N}", "EU", null, revokedAt: null);
        var byDForm = NewConsent(ConsentStatus.Granted, "s", "e", $"project:{projectId:D}", "EU", null, revokedAt: null);
        var padded = NewConsent(ConsentStatus.Granted, "s", "e", "  *  ", "EU", null, revokedAt: null);

        Assert.Equal(ConsentDecision.Allowed, ConsentGate.Evaluate(wildcard, projectId, voiceId));
        Assert.Equal(ConsentDecision.Allowed, ConsentGate.Evaluate(byNForm, projectId, voiceId));
        Assert.Equal(ConsentDecision.Allowed, ConsentGate.Evaluate(byDForm, projectId, voiceId));
        Assert.Equal(ConsentDecision.Allowed, ConsentGate.Evaluate(padded, projectId, voiceId));
    }

    [Fact]
    public void ConsentGate_Evaluate_Blocks_A_Scope_For_Another_Project()
    {
        var record = NewConsent(
            ConsentStatus.Granted, "s", "e", $"project:{Guid.NewGuid():N}", "EU", null, revokedAt: null);

        Assert.Equal(ConsentDecision.Blocked, ConsentGate.Evaluate(record, Guid.NewGuid(), null));
    }

    [Fact]
    public void ConsentGate_Evaluate_Voice_Relation_Matrix()
    {
        var projectId = Guid.NewGuid();
        var boundVoice = Guid.NewGuid();
        var otherVoice = Guid.NewGuid();

        var boundToThis = NewConsent(ConsentStatus.Granted, "s", "e", "*", "EU", boundVoice, revokedAt: null);
        var boundToOther = NewConsent(ConsentStatus.Granted, "s", "e", "*", "EU", otherVoice, revokedAt: null);
        var unbound = NewConsent(ConsentStatus.Granted, "s", "e", "*", "EU", null, revokedAt: null);

        Assert.Equal(ConsentDecision.Allowed, ConsentGate.Evaluate(boundToThis, projectId, boundVoice));
        Assert.Equal(ConsentDecision.Allowed, ConsentGate.Evaluate(boundToThis, projectId, null));
        Assert.Equal(ConsentDecision.Blocked, ConsentGate.Evaluate(boundToOther, projectId, boundVoice));
        Assert.Equal(ConsentDecision.Allowed, ConsentGate.Evaluate(unbound, projectId, boundVoice));
    }

    [Fact]
    public void ConsentGate_Evaluate_Blocks_Empty_Project_Or_Empty_Voice_Id()
    {
        var record = NewConsent(ConsentStatus.Granted, "s", "e", "*", "EU", null, revokedAt: null);

        Assert.Equal(ConsentDecision.Blocked, ConsentGate.Evaluate(record, Guid.Empty, null));
        Assert.Equal(ConsentDecision.Blocked, ConsentGate.Evaluate(record, Guid.NewGuid(), Guid.Empty));
    }

    [Theory]
    [InlineData(ConsentState.Granted, true)]
    [InlineData(ConsentState.Unknown, false)]
    [InlineData(ConsentState.Revoked, false)]
    [InlineData(ConsentState.Expired, false)]
    public void ConsentGate_IsUsable_Only_For_Granted(ConsentState state, bool expected)
    {
        Assert.Equal(expected, ConsentGate.IsUsable(state));
    }

    [Fact]
    public void ConsentGate_DecideInflight_Allows_Only_While_Still_Granted()
    {
        var projectId = Guid.NewGuid();
        var granted = NewConsent(ConsentStatus.Granted, "s", "e", "*", "EU", null, revokedAt: null);
        var revoked = NewConsent(ConsentStatus.Revoked, "s", "e", "*", "EU", null, revokedAt: null);

        Assert.Equal(ConsentDecision.Allowed, ConsentGate.DecideInflight(granted, projectId, null));
        Assert.Equal(ConsentDecision.AllowDrain, ConsentGate.DecideInflight(revoked, projectId, null));
        Assert.Equal(ConsentDecision.AllowDrain, ConsentGate.DecideInflight(null, projectId, null));
    }

    [Fact]
    public void ConsentGate_AuditEvent_Carries_Ids_Only_And_Trims_The_Endpoint()
    {
        var tenantId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var consentId = Guid.NewGuid();

        var payload = ConsentGate.AuditEvent(
            tenantId, projectId, consentId, ConsentState.Revoked, ConsentDecision.Blocked, "  /api/voices/preview  ");

        Assert.Equal(tenantId.ToString("N"), payload["tenantId"]);
        Assert.Equal(projectId.ToString("N"), payload["projectId"]);
        Assert.Equal(consentId.ToString("N"), payload["consentId"]);
        Assert.Equal(nameof(ConsentState.Revoked), payload["state"]);
        Assert.Equal(nameof(ConsentDecision.Blocked), payload["decision"]);
        Assert.Equal("/api/voices/preview", payload["endpoint"]);

        var noConsent = ConsentGate.AuditEvent(tenantId, projectId, null, ConsentState.Unknown, ConsentDecision.Blocked, "x");
        Assert.Null(noConsent["consentId"]);

        // Never subject identity, evidence reference, or media.
        Assert.DoesNotContain(payload.Keys, k => k.Contains("subject", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(payload.Keys, k => k.Contains("evidence", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(null, typeof(ArgumentNullException))]
    [InlineData("", typeof(ArgumentException))]
    [InlineData("   ", typeof(ArgumentException))]
    public void ConsentGate_AuditEvent_Requires_An_Endpoint(string? endpoint, Type expected)
    {
        var ex = Record.Exception(
            () => ConsentGate.AuditEvent(
                Guid.NewGuid(), Guid.NewGuid(), null, ConsentState.Unknown, ConsentDecision.Blocked, endpoint!));

        Assert.NotNull(ex);
        Assert.IsType(expected, ex);
        Assert.Contains("endpoint", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void VoicePreviewConsentState_Exposes_Verified_And_Blocked_Only()
    {
        Assert.Equal(
            new[] { nameof(VoicePreviewConsentState.Verified), nameof(VoicePreviewConsentState.Blocked) },
            Enum.GetNames<VoicePreviewConsentState>());

        // Not-required consent (stock voice) is recorded as Verified; only
        // Blocked may gate a preview.
        Assert.True(ConsentGate.IsUsable(ConsentState.Granted));
    }

    // ==================================================================
    // PreviewAudio — duration and format math
    // ==================================================================

    [Fact]
    public void PreviewAudio_Writes_A_Canonical_44_Byte_Mono_Pcm_Header()
    {
        Assert.Equal(16000, PreviewAudio.SampleRateHz);

        var wav = PreviewAudio.BuildWav(1);

        // 1ms => 16 samples => 32 data bytes => 44-byte header + 32.
        Assert.Equal(44 + 32, wav.Length);
        Assert.Equal("RIFF", Ascii(wav, 0, 4));
        Assert.Equal(36 + 32, BitConverter.ToInt32(wav, 4));
        Assert.Equal("WAVE", Ascii(wav, 8, 4));
        Assert.Equal("fmt ", Ascii(wav, 12, 4));
        Assert.Equal(16, BitConverter.ToInt32(wav, 16));      // fmt chunk size
        Assert.Equal((short)1, BitConverter.ToInt16(wav, 20)); // PCM
        Assert.Equal((short)1, BitConverter.ToInt16(wav, 22)); // mono
        Assert.Equal(16000, BitConverter.ToInt32(wav, 24));    // sample rate
        Assert.Equal(32000, BitConverter.ToInt32(wav, 28));    // byte rate
        Assert.Equal((short)2, BitConverter.ToInt16(wav, 32));  // block align
        Assert.Equal((short)16, BitConverter.ToInt16(wav, 34)); // bits per sample
        Assert.Equal("data", Ascii(wav, 36, 4));
        Assert.Equal(32, BitConverter.ToInt32(wav, 40));
    }

    [Theory]
    [InlineData(0, 44 + 32)]
    [InlineData(-1, 44 + 32)]
    [InlineData(int.MinValue, 44 + 32)]
    [InlineData(1, 44 + 32)]
    [InlineData(2, 44 + 64)]
    [InlineData(100, 44 + 3200)]
    [InlineData(1000, 44 + 32000)]
    [InlineData(9999, 44 + 319968)]
    [InlineData(10000, 44 + 320000)]
    public void PreviewAudio_Duration_And_Size_Are_Linear_And_Clamped(int durationMs, int expectedLength)
    {
        Assert.Equal(expectedLength, PreviewAudio.BuildWav(durationMs).Length);
    }

    [Fact]
    public void PreviewAudio_Is_Byte_Identical_For_The_Same_Duration()
    {
        Assert.Equal(PreviewAudio.BuildWav(250), PreviewAudio.BuildWav(250));
        Assert.NotEqual(PreviewAudio.BuildWav(250), PreviewAudio.BuildWav(251));
    }

    [Fact]
    public void PreviewAudio_Synthesises_A_Bounded_440Hz_Sine()
    {
        var wav = PreviewAudio.BuildWav(100);
        var samples = new short[(wav.Length - 44) / 2];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = BitConverter.ToInt16(wav, 44 + (i * 2));
        }

        Assert.Equal(0, samples[0]); // sin(0) = 0

        // Amplitude is 32767 * 0.5, slightly below because the 440Hz peaks do not
        // land on an exact sample at 16kHz.
        Assert.InRange(samples.Max(s => Math.Abs((int)s)), 16000, 16383);
        Assert.True(samples.Any(s => s > 0), "sine must have a positive half-cycle");
        Assert.True(samples.Any(s => s < 0), "sine must have a negative half-cycle");
    }

    [Fact]
    public void PreviewAudio_At_The_Internal_Sample_Count_Overflow_Point_Emits_A_Header_Only_Buffer()
    {
        // 16 samples per ms: 16 * 134_217_728 ms = 2^31, the first duration whose
        // sample count overflows a signed 32-bit int. The result degrades to a
        // 44-byte header with a zero-length data chunk instead of mis-sizing it.
        var wav = PreviewAudio.BuildWav(134_217_728);

        Assert.Equal(44, wav.Length);
        Assert.Equal("RIFF", Ascii(wav, 0, 4));
        Assert.Equal(36, BitConverter.ToInt32(wav, 4));
        Assert.Equal("WAVE", Ascii(wav, 8, 4));
        Assert.Equal("data", Ascii(wav, 36, 4));
        Assert.Equal(0, BitConverter.ToInt32(wav, 40));
    }

    [Fact]
    public void PreviewAudio_Absurd_Durations_Throw_Known_Gap()
    {
        // KNOWN GAP (Task 039C): the sample-count multiplication is unclamped, so
        // a duration far past the wrap point (here 2^31 - 1 ms) under-sizes the
        // buffer and the header write throws ArgumentException. The preview lane
        // contract is "never throws" (see MediaPreviewGenerator's degrade path),
        // so this should clamp to the cap instead. Pinned here so the fix shows up
        // as a deliberate diff.
        var ex = Record.Exception(() => PreviewAudio.BuildWav(int.MaxValue));
        Assert.NotNull(ex);
        Assert.IsType<ArgumentException>(ex);
    }

    // ==================================================================
    // MediaPreviewGenerator — track detection and peaks payload shape
    // ==================================================================

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("none", false)]
    [InlineData("NONE", false)]
    [InlineData("None", false)]
    [InlineData(" none ", false)]
    [InlineData("aac", true)]
    [InlineData("pcm_s16le", true)]
    [InlineData(" mp3 ", true)]
    public void MediaPreviewGenerator_HasAudioTrack(string? codec, bool expected)
    {
        Assert.Equal(expected, MediaPreviewGenerator.HasAudioTrack(codec));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("h264", true)]
    [InlineData(" vp9 ", true)]
    public void MediaPreviewGenerator_HasVideoTrack(string? videoCodec, bool expected)
    {
        Assert.Equal(expected, MediaPreviewGenerator.HasVideoTrack(videoCodec));
    }

    [Fact]
    public void MediaPreviewGenerator_Published_Constants()
    {
        Assert.Equal(new[] { 64, 256, 1024 }, MediaPreviewGenerator.PeakResolutions);
        Assert.Equal(10000, MediaPreviewGenerator.PreviewAudioCapMs);
        Assert.Equal("media.preview_generated", MediaPreviewGenerator.AuditAction);
    }

    [Fact]
    public void MediaPreviewGenerator_Peaks_Json_Has_All_Resolutions_In_Range_And_Is_Deterministic()
    {
        var json = MediaPreviewGenerator.BuildWaveformPeaksJson(new string('a', 64), 4200, 48000);
        var again = MediaPreviewGenerator.BuildWaveformPeaksJson(new string('a', 64), 4200, 48000);

        Assert.Equal(json, again);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal(4200, root.GetProperty("durationMs").GetInt32());
        Assert.Equal(48000, root.GetProperty("sampleRate").GetInt32());
        Assert.False(root.TryGetProperty("peaksMissing", out _));

        var resolutions = root.GetProperty("resolutions");
        foreach (var count in MediaPreviewGenerator.PeakResolutions)
        {
            var peaks = resolutions.GetProperty(count.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Assert.Equal(count, peaks.GetArrayLength());

            foreach (var peak in peaks.EnumerateArray())
            {
                var value = peak.GetDouble();
                Assert.InRange(value, 0d, 1d);
                // Rounded to 4 decimals.
                Assert.Equal(Math.Round(value, 4), value);
            }
        }
    }

    [Fact]
    public void MediaPreviewGenerator_Peaks_Json_Is_Sensitive_To_The_Content_Hash()
    {
        var a = MediaPreviewGenerator.BuildWaveformPeaksJson(new string('a', 64), 1000, 16000);
        var b = MediaPreviewGenerator.BuildWaveformPeaksJson(new string('b', 64), 1000, 16000);

        Assert.NotEqual(a, b);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(0, int.MaxValue)]
    [InlineData(int.MaxValue, 1)]
    public void MediaPreviewGenerator_Peaks_Json_Accepts_Exact_Zero_Duration_And_Unit_Sample_Rate(
        int durationMs, int sampleRate)
    {
        var json = MediaPreviewGenerator.BuildWaveformPeaksJson("hash", durationMs, sampleRate);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(durationMs, doc.RootElement.GetProperty("durationMs").GetInt32());
        Assert.Equal(sampleRate, doc.RootElement.GetProperty("sampleRate").GetInt32());
    }

    [Fact]
    public void MediaPreviewGenerator_Peaks_Json_Guards_Reject_Bad_Arguments()
    {
        Assert.Throws<DomainException>(() => MediaPreviewGenerator.BuildWaveformPeaksJson(null!, 100, 16000));
        Assert.Throws<DomainException>(() => MediaPreviewGenerator.BuildWaveformPeaksJson("", 100, 16000));
        Assert.Throws<DomainException>(() => MediaPreviewGenerator.BuildWaveformPeaksJson("   ", 100, 16000));
        Assert.Throws<DomainException>(() => MediaPreviewGenerator.BuildWaveformPeaksJson("hash", -1, 16000));
        Assert.Throws<DomainException>(() => MediaPreviewGenerator.BuildWaveformPeaksJson("hash", int.MinValue, 16000));
        Assert.Throws<DomainException>(() => MediaPreviewGenerator.BuildWaveformPeaksJson("hash", 100, 0));
        Assert.Throws<DomainException>(() => MediaPreviewGenerator.BuildWaveformPeaksJson("hash", 100, -1));
    }

    [Fact]
    public void MediaPreviewGenerator_Empty_Peaks_Json_Flags_Missing_And_Keeps_Resolution_Keys()
    {
        var json = MediaPreviewGenerator.BuildEmptyPeaksJson(1000, 16000);
        var again = MediaPreviewGenerator.BuildEmptyPeaksJson(1000, 16000);

        Assert.Equal(json, again);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.True(root.GetProperty("peaksMissing").GetBoolean());
        Assert.Equal(1000, root.GetProperty("durationMs").GetInt32());
        Assert.Equal(16000, root.GetProperty("sampleRate").GetInt32());

        var resolutions = root.GetProperty("resolutions");
        foreach (var count in MediaPreviewGenerator.PeakResolutions)
        {
            Assert.Empty(resolutions.GetProperty(count.ToString(System.Globalization.CultureInfo.InvariantCulture)).EnumerateArray());
        }
    }

    [Fact]
    public void MediaPreviewGenerator_Empty_Peaks_Json_Guards_Arguments()
    {
        Assert.Throws<DomainException>(() => MediaPreviewGenerator.BuildEmptyPeaksJson(-1, 16000));
        Assert.Throws<DomainException>(() => MediaPreviewGenerator.BuildEmptyPeaksJson(0, 0));
    }

    [Fact]
    public void MediaPreviewGenerator_Rejects_Null_Dependencies()
    {
        var factory = new ThrowingContextFactory();
        var artifacts = new ArtifactService(factory, new UnusedStorage());
        var audit = new AuditService(factory);
        var options = MsOptions.Create(new PreviewOptions());
        var logger = NullLogger<MediaPreviewGenerator>.Instance;

        Assert.Throws<ArgumentNullException>(() => new MediaPreviewGenerator(null!, artifacts, audit, options, logger));
        Assert.Throws<ArgumentNullException>(() => new MediaPreviewGenerator(factory, null!, audit, options, logger));
        Assert.Throws<ArgumentNullException>(() => new MediaPreviewGenerator(factory, artifacts, null!, options, logger));
        Assert.Throws<ArgumentNullException>(() => new MediaPreviewGenerator(factory, artifacts, audit, null!, logger));
        Assert.Throws<ArgumentNullException>(() => new MediaPreviewGenerator(factory, artifacts, audit, options, null!));
    }

    [Fact]
    public async Task MediaPreviewGenerator_Rejects_Empty_Tenant_Project_And_Run_Before_Touching_The_Db()
    {
        var generator = NewGenerator(new ThrowingContextFactory());

        await Assert.ThrowsAsync<DomainException>(() =>
            generator.GenerateForRunAsync(Guid.Empty, Guid.NewGuid(), Guid.NewGuid()));
        await Assert.ThrowsAsync<DomainException>(() =>
            generator.GenerateForRunAsync(Guid.NewGuid(), Guid.Empty, Guid.NewGuid()));
        await Assert.ThrowsAsync<DomainException>(() =>
            generator.GenerateForRunAsync(Guid.NewGuid(), Guid.NewGuid(), Guid.Empty));
    }

    [Fact]
    public async Task MediaPreviewGenerator_Degrades_And_Audits_When_The_Project_Is_Missing()
    {
        var tenantId = Guid.NewGuid();
        using var factory = CreateFactory();
        var generator = NewGenerator(factory);
        var runId = Guid.NewGuid();

        var result = await generator.GenerateForRunAsync(tenantId, Guid.NewGuid(), runId);

        Assert.True(result.PreviewDegraded);
        Assert.True(result.PeaksMissing);
        Assert.Null(result.MediaPreviewAudioArtifactId);
        Assert.Null(result.WaveformPeaksArtifactId);
        Assert.Null(result.VideoPreviewArtifactId);

        // The failure is auditable: one degraded event for this run, ids only.
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = factory.CreateSetup();
            var events = db.Set<AuditEvent>()
                .Where(e => e.Action == MediaPreviewGenerator.AuditAction)
                .ToList();
            var auditEvent = Assert.Single(events);
            Assert.Equal(runId.ToString("N"), auditEvent.ResourceId);
            Assert.Equal("ProcessingRun", auditEvent.ResourceType);
            Assert.NotNull(auditEvent.DetailsJson);
        }
    }

    // ==================================================================
    // QcEvidenceLinker — link construction
    // ==================================================================

    [Fact]
    public void QcEvidenceLinker_Published_Constants()
    {
        Assert.Equal(64, QcEvidenceLinker.MaxKindLength);
        Assert.Equal(256, QcEvidenceLinker.MaxPeakSliceLength);
        Assert.Equal("qc.evidence_linked", QcEvidenceLinker.AuditAction);
    }

    [Fact]
    public void QcEvidenceLinker_ClipRange_Accepts_Unset_And_Ordered_Non_Negative_Bounds()
    {
        QcEvidenceLinker.RequireClipRange(null, null);
        QcEvidenceLinker.RequireClipRange(0, 0);
        QcEvidenceLinker.RequireClipRange(5, 5);
        QcEvidenceLinker.RequireClipRange(0, 10);
        QcEvidenceLinker.RequireClipRange(0, int.MaxValue);
    }

    [Theory]
    [InlineData(5, null)]
    [InlineData(null, 5)]
    public void QcEvidenceLinker_ClipRange_Rejects_Half_Specified_Ranges(int? start, int? end)
    {
        var ex = Assert.Throws<DomainException>(() => QcEvidenceLinker.RequireClipRange(start, end));
        Assert.Equal("Clip range requires both ClipStartMs and ClipEndMs.", ex.Message);
    }

    [Theory]
    [InlineData(-1, 5)]
    [InlineData(5, -1)]
    [InlineData(int.MinValue, 5)]
    [InlineData(0, int.MinValue)]
    public void QcEvidenceLinker_ClipRange_Rejects_Negative_Bounds(int start, int end)
    {
        var ex = Assert.Throws<DomainException>(() => QcEvidenceLinker.RequireClipRange(start, end));
        Assert.Equal("Clip range bounds must be >= 0.", ex.Message);
    }

    [Theory]
    [InlineData(6, 5)]
    [InlineData(int.MaxValue, 0)]
    public void QcEvidenceLinker_ClipRange_Rejects_Inverted_Bounds(int start, int end)
    {
        var ex = Assert.Throws<DomainException>(() => QcEvidenceLinker.RequireClipRange(start, end));
        Assert.Equal("ClipStartMs must not exceed ClipEndMs.", ex.Message);
    }

    [Fact]
    public async Task QcEvidenceLinker_Validates_Arguments_Before_Any_Database_Or_Storage_Call()
    {
        // The strict factory throws on CreateDbContext: a DomainException proves
        // validation happened before any I/O.
        var linker = NewLinker(new ThrowingContextFactory());
        var tenantId = Guid.NewGuid();
        var issueId = Guid.NewGuid();

        Assert.Equal("TenantId must not be empty.",
            (await Assert.ThrowsAsync<DomainException>(() => linker.LinkAsync(Guid.Empty, issueId, "clip"))).Message);
        Assert.Equal("qcIssueId must not be empty.",
            (await Assert.ThrowsAsync<DomainException>(() => linker.LinkAsync(tenantId, Guid.Empty, "clip"))).Message);
        Assert.Equal("ArtifactKind must not be empty.",
            (await Assert.ThrowsAsync<DomainException>(() => linker.LinkAsync(tenantId, issueId, "  "))).Message);
        Assert.Equal("ArtifactKind must be at most 64 chars.",
            (await Assert.ThrowsAsync<DomainException>(() => linker.LinkAsync(tenantId, issueId, new string('k', 65)))).Message);
        Assert.Equal("PeakSlice must be at most 256 chars.",
            (await Assert.ThrowsAsync<DomainException>(
                () => linker.LinkAsync(tenantId, issueId, "clip", peakSlice: new string('p', 257)))).Message);
        Assert.Equal("Clip range bounds must be >= 0.",
            (await Assert.ThrowsAsync<DomainException>(() => linker.LinkAsync(tenantId, issueId, "clip", -1, 5))).Message);

        // Boundary: exactly 64 kind chars and 256 slice chars pass validation and
        // therefore reach the database, which the strict factory refuses.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => linker.LinkAsync(tenantId, issueId, new string('k', 64), peakSlice: new string('p', 256)));
    }

    [Fact]
    public async Task QcEvidenceLinker_Unknown_Issue_Reads_As_Not_Found()
    {
        using var factory = CreateFactory();
        var linker = NewLinker(factory);

        var ex = await Assert.ThrowsAsync<NotFoundException>(
            () => linker.LinkAsync(Guid.NewGuid(), Guid.NewGuid(), "clip"));
        Assert.Equal(ErrorCodes.NotFound, ex.ErrorCode);
        Assert.Equal(404, ex.StatusCode);
    }

    [Fact]
    public async Task QcEvidenceLinker_Cross_Tenant_Issue_Reads_Identically_To_A_Missing_One()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        using var factory = CreateFactory();
        var issueId = SeedQcIssue(factory, tenantB, Guid.NewGuid());
        var linker = NewLinker(factory);

        // The row really exists (proving the 404 below is scoping, not absence).
        using (TenantContext.BeginScope(tenantB))
        {
            using var db = factory.CreateSetup();
            Assert.Equal(issueId, db.Set<QualityResult>().Select(q => q.Id).First());
        }

        var crossTenant = await Assert.ThrowsAsync<NotFoundException>(
            () => linker.LinkAsync(tenantA, issueId, "clip"));
        var missing = await Assert.ThrowsAsync<NotFoundException>(
            () => linker.LinkAsync(tenantA, Guid.NewGuid(), "clip"));

        Assert.Equal(ErrorCodes.NotFound, crossTenant.ErrorCode);
        Assert.Equal(crossTenant.StatusCode, missing.StatusCode);

        // No existence leak: the cross-tenant message carries the requested id
        // only, and never the owning tenant.
        Assert.Contains(issueId.ToString("D"), crossTenant.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(tenantB.ToString(), crossTenant.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void QcEvidenceLinker_Rejects_Null_Dependencies()
    {
        var factory = new ThrowingContextFactory();
        var artifacts = new ArtifactService(factory, new UnusedStorage());
        var audit = new AuditService(factory);
        var logger = NullLogger<QcEvidenceLinker>.Instance;

        Assert.Throws<ArgumentNullException>(() => new QcEvidenceLinker(null!, artifacts, audit, logger));
        Assert.Throws<ArgumentNullException>(() => new QcEvidenceLinker(factory, null!, audit, logger));
        Assert.Throws<ArgumentNullException>(() => new QcEvidenceLinker(factory, artifacts, null!, logger));
        Assert.Throws<ArgumentNullException>(() => new QcEvidenceLinker(factory, artifacts, audit, null!));
    }

    // ==================================================================
    // VoicePreviewService — pure validation helpers
    // ==================================================================

    [Fact]
    public void Preview_Markers_Are_Stable_Sub_Codes()
    {
        Assert.Equal("PREVIEW_STATE_CONFLICT", VoicePreviewService.PreviewStateConflictMarker);
        Assert.Equal("PREVIEW_QUOTA_EXCEEDED", VoicePreviewService.PreviewQuotaExceededMarker);
        Assert.Equal("VOICE_CONSENT_REQUIRED", VoicePreviewService.VoiceConsentRequiredMarker);
        Assert.Equal("PREVIEW_TEXT_INVALID", VoicePreviewService.PreviewTextInvalidMarker);
        Assert.Equal("PREVIEW_PROVIDER_TIMEOUT", VoicePreviewService.PreviewProviderTimeoutMarker);
        Assert.Equal("PREVIEW_PROVIDER_FAILED", VoicePreviewService.PreviewProviderFailedMarker);
        Assert.Equal("voice.preview_requested", VoicePreviewService.AuditRequested);
        Assert.Equal("voice.preview_completed", VoicePreviewService.AuditCompleted);
        Assert.Equal("voice.preview_failed", VoicePreviewService.AuditFailed);
        Assert.Equal("voice.preview_denied", VoicePreviewService.AuditDenied);
        Assert.Equal("voice.preview_blocked", VoicePreviewService.AuditBlocked);
        Assert.Equal("voice.preview_cancelled", VoicePreviewService.AuditCancelled);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public void RequirePreviewText_Rejects_Empty_And_Whitespace(string? text)
    {
        var ex = Assert.Throws<ErrorCodeException>(() => VoicePreviewService.RequirePreviewText(text));
        Assert.Equal(ErrorCodes.ValidationFailed, ex.ErrorCode);
        Assert.Equal(400, ex.StatusCode);
        Assert.StartsWith(VoicePreviewService.PreviewTextInvalidMarker, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RequirePreviewText_Trims_And_Enforces_The_Exact_Length_Bound()
    {
        Assert.Equal("hello", VoicePreviewService.RequirePreviewText("  hello \n"));

        var atLimit = new string('t', VoicePreviewJob.MaxTextLength);
        Assert.Equal(atLimit.Length, VoicePreviewService.RequirePreviewText(atLimit).Length);

        var overLimit = new string('t', VoicePreviewJob.MaxTextLength + 1);
        var ex = Assert.Throws<ErrorCodeException>(() => VoicePreviewService.RequirePreviewText(overLimit));
        Assert.StartsWith(VoicePreviewService.PreviewTextInvalidMarker, ex.Message, StringComparison.Ordinal);
        Assert.Contains("500", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void RequireVoiceId_Rejects_Empty(string? voiceId)
    {
        var ex = Assert.Throws<ErrorCodeException>(() => VoicePreviewService.RequireVoiceId(voiceId));
        Assert.Equal(ErrorCodes.ValidationFailed, ex.ErrorCode);
    }

    [Theory]
    [InlineData("https://provider.example/sample.wav")]
    [InlineData("mock://voice/1")]
    [InlineData("any://")]
    public void RequireVoiceId_Rejects_Url_Shaped_Values_Ssrf_Guard(string voiceId)
    {
        var ex = Assert.Throws<ErrorCodeException>(() => VoicePreviewService.RequireVoiceId(voiceId));
        Assert.Equal(ErrorCodes.ValidationFailed, ex.ErrorCode);
        Assert.Contains("server-resolved", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RequireVoiceId_Trims_And_Enforces_The_Exact_Length_Bound()
    {
        Assert.Equal("mock-voice-1", VoicePreviewService.RequireVoiceId("  mock-voice-1  "));

        var atLimit = new string('v', VoicePreviewJob.MaxVoiceIdLength);
        Assert.Equal(atLimit.Length, VoicePreviewService.RequireVoiceId(atLimit).Length);

        var overLimit = new string('v', VoicePreviewJob.MaxVoiceIdLength + 1);
        var ex = Assert.Throws<ErrorCodeException>(() => VoicePreviewService.RequireVoiceId(overLimit));
        Assert.Contains("256", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildCompletedMessage_Carries_Ids_And_Status_Only()
    {
        var tenantId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var runScope = Guid.NewGuid();
        var artifactId = Guid.NewGuid();
        var executionId = Guid.NewGuid();
        var job = NewJob(tenantId, projectId, Guid.NewGuid(), VoicePreviewStatus.Completed, artifactId, executionId);

        var message = VoicePreviewService.BuildCompletedMessage(tenantId, projectId, runScope, job, null);

        Assert.NotEqual(Guid.Empty, message.MessageId);
        Assert.Equal(32, message.CorrelationId.Length);
        Assert.Equal(tenantId, message.TenantId);
        Assert.Equal(projectId, message.ProjectId);
        Assert.Equal(runScope, message.ProcessingRunId);
        Assert.Null(message.StageExecutionId);
        Assert.Null(message.StageType);
        Assert.Equal("VoicePreview", message.ScopeType);
        Assert.Equal(job.Id.ToString("N"), message.ScopeId);
        Assert.Null(message.SegmentId);
        Assert.Equal(MessageVersionPolicy.CurrentVersion, message.SchemaVersion);
        Assert.Equal(0, message.Attempt);
        Assert.Null(message.InputHash);
        Assert.Null(message.ConfigurationHash);
        Assert.Null(message.ExecutionSnapshotHash);
        Assert.Equal(job.Id, message.VoicePreviewJobId);
        Assert.Equal(nameof(VoicePreviewStatus.Completed), message.Status);
        Assert.Equal(artifactId, message.ArtifactId);
        Assert.Equal(executionId, message.ProviderExecutionId);
        Assert.Null(message.ErrorCode);

        var failed = VoicePreviewService.BuildCompletedMessage(
            tenantId, projectId, runScope, job, ErrorCodes.ProviderTimeout);
        Assert.Equal(ErrorCodes.ProviderTimeout, failed.ErrorCode);
        Assert.NotEqual(message.MessageId, failed.MessageId);
    }

    [Fact]
    public void BuildCompletedMessage_Rejects_A_Null_Job()
    {
        Assert.Throws<ArgumentNullException>(() => VoicePreviewService.BuildCompletedMessage(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null!, null));
    }

    // ==================================================================
    // VoicePreviewService — argument guards (no I/O)
    // ==================================================================

    [Fact]
    public async Task Preview_Service_Rejects_Empty_Ids_Before_Any_IO()
    {
        var service = NewService(new ThrowingContextFactory());
        var tenantId = Guid.NewGuid();

        await Assert.ThrowsAsync<DomainException>(() => service.GetAsync(Guid.Empty, Guid.NewGuid()));
        await Assert.ThrowsAsync<DomainException>(() => service.GetAsync(tenantId, Guid.Empty));
        await Assert.ThrowsAsync<DomainException>(() => service.CancelAsync(Guid.Empty, Guid.NewGuid(), Guid.NewGuid()));
        await Assert.ThrowsAsync<DomainException>(() => service.CancelAsync(tenantId, Guid.Empty, Guid.NewGuid()));
        await Assert.ThrowsAsync<DomainException>(() => service.CancelAsync(tenantId, Guid.NewGuid(), Guid.Empty));

        await Assert.ThrowsAsync<DomainException>(() => service.RequestPreviewAsync(
            Guid.Empty, Guid.NewGuid(), Guid.NewGuid(), "mock-voice-1", "hello", Guid.NewGuid()));
        await Assert.ThrowsAsync<DomainException>(() => service.RequestPreviewAsync(
            tenantId, Guid.Empty, Guid.NewGuid(), "mock-voice-1", "hello", Guid.NewGuid()));
        await Assert.ThrowsAsync<DomainException>(() => service.RequestPreviewAsync(
            tenantId, Guid.NewGuid(), Guid.Empty, "mock-voice-1", "hello", Guid.NewGuid()));
        await Assert.ThrowsAsync<DomainException>(() => service.RequestPreviewAsync(
            tenantId, Guid.NewGuid(), Guid.NewGuid(), "mock-voice-1", "hello", Guid.Empty));

        // An explicitly set but empty run id is a domain error, not a silent null.
        var emptyRun = await Assert.ThrowsAsync<DomainException>(() => service.RequestPreviewAsync(
            tenantId, Guid.NewGuid(), Guid.NewGuid(), "mock-voice-1", "hello", Guid.NewGuid(), runId: Guid.Empty));
        Assert.Equal("RunId must not be empty when set.", emptyRun.Message);
    }

    [Fact]
    public async Task Preview_Service_Rejects_Bad_Text_And_Voice_Before_Any_IO()
    {
        var service = NewService(new ThrowingContextFactory());
        var tenantId = Guid.NewGuid();

        var badText = await Assert.ThrowsAsync<ErrorCodeException>(() => service.RequestPreviewAsync(
            tenantId, Guid.NewGuid(), Guid.NewGuid(), "mock-voice-1", "   ", Guid.NewGuid()));
        Assert.Equal(ErrorCodes.ValidationFailed, badText.ErrorCode);

        var badVoice = await Assert.ThrowsAsync<ErrorCodeException>(() => service.RequestPreviewAsync(
            tenantId, Guid.NewGuid(), Guid.NewGuid(), "https://provider.example/a.wav", "hello", Guid.NewGuid()));
        Assert.Equal(ErrorCodes.ValidationFailed, badVoice.ErrorCode);
    }

    [Fact]
    public async Task Preview_Service_Rejects_An_Over_Long_Idempotency_Key()
    {
        var service = NewService(new ThrowingContextFactory());

        var ex = await Assert.ThrowsAsync<ErrorCodeException>(() => service.RequestPreviewAsync(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "mock-voice-1", "hello", Guid.NewGuid(),
            idempotencyKey: new string('k', VoicePreviewJob.MaxIdempotencyKeyLength + 1)));

        Assert.Equal(ErrorCodes.ValidationFailed, ex.ErrorCode);
        Assert.Contains("128", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Preview_Service_Rejects_Null_Dependencies()
    {
        var factory = new ThrowingContextFactory();
        var artifacts = new ArtifactService(factory, new UnusedStorage());
        var recorder = new ProviderExecutionRecorder(factory);
        var audit = new AuditService(factory);
        var preview = MsOptions.Create(new PreviewOptions());
        var voices = MsOptions.Create(new VoiceOptions());
        var logger = NullLogger<VoicePreviewService>.Instance;

        Assert.Throws<ArgumentNullException>(() => new VoicePreviewService(
            null!, new ThrowingTts(), recorder, artifacts, audit, new RecordingPublisher(), preview, voices, logger));
        Assert.Throws<ArgumentNullException>(() => new VoicePreviewService(
            factory, null!, recorder, artifacts, audit, new RecordingPublisher(), preview, voices, logger));
        Assert.Throws<ArgumentNullException>(() => new VoicePreviewService(
            factory, new ThrowingTts(), null!, artifacts, audit, new RecordingPublisher(), preview, voices, logger));
        Assert.Throws<ArgumentNullException>(() => new VoicePreviewService(
            factory, new ThrowingTts(), recorder, null!, audit, new RecordingPublisher(), preview, voices, logger));
        Assert.Throws<ArgumentNullException>(() => new VoicePreviewService(
            factory, new ThrowingTts(), recorder, artifacts, null!, new RecordingPublisher(), preview, voices, logger));
        Assert.Throws<ArgumentNullException>(() => new VoicePreviewService(
            factory, new ThrowingTts(), recorder, artifacts, audit, null!, preview, voices, logger));
        Assert.Throws<ArgumentNullException>(() => new VoicePreviewService(
            factory, new ThrowingTts(), recorder, artifacts, audit, new RecordingPublisher(), null!, voices, logger));
        Assert.Throws<ArgumentNullException>(() => new VoicePreviewService(
            factory, new ThrowingTts(), recorder, artifacts, audit, new RecordingPublisher(), preview, null!, logger));
        Assert.Throws<ArgumentNullException>(() => new VoicePreviewService(
            factory, new ThrowingTts(), recorder, artifacts, audit, new RecordingPublisher(), preview, voices, null!));
    }

    // ==================================================================
    // VoicePreviewService — quota boundaries (per tenant, per UTC day)
    // ==================================================================

    [Fact]
    public async Task Quota_Allows_When_Remaining_Is_Exactly_One()
    {
        using var fixture = new PreviewFixture(dayCap: 2, minuteCap: 1000, cloningEnabled: false);
        SeedPreviewUsage(fixture.Factory, fixture.TenantId, 1);

        var tts = new ThrowingTts();

        // Allowed: the request ran past the quota gate into the provider call.
        var ex = await Assert.ThrowsAsync<ErrorCodeException>(() => fixture.RequestAsync(fixture.CreateService(tts)));
        Assert.Equal(ErrorCodes.ProviderTimeout, ex.ErrorCode);
        Assert.StartsWith(VoicePreviewService.PreviewProviderTimeoutMarker, ex.Message, StringComparison.Ordinal);
        Assert.Equal(1, tts.Calls);
    }

    [Fact]
    public async Task Quota_Denies_When_Remaining_Is_Exactly_Zero()
    {
        using var fixture = new PreviewFixture(dayCap: 1, minuteCap: 1000, cloningEnabled: false);
        SeedPreviewUsage(fixture.Factory, fixture.TenantId, 1);

        var tts = new ThrowingTts();
        var ex = await Assert.ThrowsAsync<QuotaExceededException>(() => fixture.RequestAsync(fixture.CreateService(tts)));
        Assert.Equal(429, ex.StatusCode);
        Assert.Contains("daily cap of 1 previews exceeded", ex.Message, StringComparison.Ordinal);
        Assert.StartsWith(VoicePreviewService.PreviewQuotaExceededMarker, ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, tts.Calls);

        // The denial is persisted as a Failed/Denied row before the throw.
        var denied = Assert.Single(
            ReadJobs(fixture.Factory, fixture.TenantId),
            j => j.QuotaCheck == VoicePreviewQuotaCheck.Denied);
        Assert.Equal(VoicePreviewStatus.Failed, denied.Status);
        Assert.Equal(VoicePreviewConsentState.Verified, denied.ConsentState);
        Assert.Equal(ErrorCodes.QuotaExceeded, denied.ErrorCode);
        Assert.Contains("daily cap", denied.QuotaCheckReason!, StringComparison.Ordinal);
        Assert.NotNull(denied.CompletedAt);
        Assert.Null(denied.ArtifactId);
        Assert.Null(denied.ProviderExecutionId);
    }

    [Fact]
    public async Task Quota_Denies_When_Remaining_Is_Negative()
    {
        using var fixture = new PreviewFixture(dayCap: 1, minuteCap: 1000, cloningEnabled: false);
        SeedPreviewUsage(fixture.Factory, fixture.TenantId, 3);

        var tts = new ThrowingTts();
        var ex = await Assert.ThrowsAsync<QuotaExceededException>(() => fixture.RequestAsync(fixture.CreateService(tts)));
        Assert.Contains("daily cap of 1 previews exceeded", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, tts.Calls);
    }

    [Fact]
    public async Task Quota_Allows_On_An_Explicitly_Unlimited_Tier()
    {
        using var fixture = new PreviewFixture(dayCap: int.MaxValue, minuteCap: 1000, cloningEnabled: false);
        SeedPreviewUsage(fixture.Factory, fixture.TenantId, 5);

        var tts = new ThrowingTts();
        var ex = await Assert.ThrowsAsync<ErrorCodeException>(() => fixture.RequestAsync(fixture.CreateService(tts)));
        Assert.Equal(ErrorCodes.ProviderTimeout, ex.ErrorCode);
        Assert.Equal(1, tts.Calls);
    }

    [Fact]
    public async Task Quota_Per_Minute_Throttle_Fires_Before_The_Daily_Cap()
    {
        using var fixture = new PreviewFixture(dayCap: 1000, minuteCap: 1, cloningEnabled: false);
        SeedPreviewUsage(fixture.Factory, fixture.TenantId, 1);

        var tts = new ThrowingTts();
        var ex = await Assert.ThrowsAsync<QuotaExceededException>(() => fixture.RequestAsync(fixture.CreateService(tts)));
        Assert.Contains("per-minute throttle of 1 previews exceeded", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, tts.Calls);
    }

    [Fact]
    public async Task Quota_Ignores_Usage_Older_Than_The_Per_Minute_Window()
    {
        using var fixture = new PreviewFixture(dayCap: 1000, minuteCap: 1, cloningEnabled: false);
        SeedPreviewUsage(fixture.Factory, fixture.TenantId, 1, minutesAgo: 5);

        var tts = new ThrowingTts();
        var ex = await Assert.ThrowsAsync<ErrorCodeException>(() => fixture.RequestAsync(fixture.CreateService(tts)));
        Assert.Equal(ErrorCodes.ProviderTimeout, ex.ErrorCode);
        Assert.Equal(1, tts.Calls);
    }

    // ==================================================================
    // VoicePreviewService — tenant scoping (the negative case)
    // ==================================================================

    [Fact]
    public async Task Quota_For_Tenant_A_Is_Never_Satisfied_By_Tenant_B_Usage()
    {
        using var a = new PreviewFixture(dayCap: 1, minuteCap: 1000, cloningEnabled: false);
        using var b = new PreviewFixture(dayCap: 1, minuteCap: 1000, cloningEnabled: false, shared: a);

        // Tenant B is already over its cap; tenant A is untouched.
        SeedPreviewUsage(b.Factory, b.TenantId, 3);

        var tts = new ThrowingTts();
        var ex = await Assert.ThrowsAsync<ErrorCodeException>(() => a.RequestAsync(a.CreateService(tts)));
        Assert.Equal(ErrorCodes.ProviderTimeout, ex.ErrorCode);
        Assert.Equal(1, tts.Calls);

        // ... and B is still denied.
        var denied = await Assert.ThrowsAsync<QuotaExceededException>(
            () => b.RequestAsync(b.CreateService(new ThrowingTts())));
        Assert.Contains("daily cap of 1 previews exceeded", denied.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Quota_For_Tenant_A_Is_Not_Rescued_By_Tenant_B_Free_Quota()
    {
        using var a = new PreviewFixture(dayCap: 1, minuteCap: 1000, cloningEnabled: false);
        using var b = new PreviewFixture(dayCap: 1, minuteCap: 1000, cloningEnabled: false, shared: a);

        // Tenant A is at its cap even though tenant B has spent nothing.
        SeedPreviewUsage(a.Factory, a.TenantId, 1);

        var denied = await Assert.ThrowsAsync<QuotaExceededException>(
            () => a.RequestAsync(a.CreateService(new ThrowingTts())));
        Assert.Contains("daily cap of 1 previews exceeded", denied.Message, StringComparison.Ordinal);

        var tts = new ThrowingTts();
        var ex = await Assert.ThrowsAsync<ErrorCodeException>(() => b.RequestAsync(b.CreateService(tts)));
        Assert.Equal(ErrorCodes.ProviderTimeout, ex.ErrorCode);
        Assert.Equal(1, tts.Calls);
    }

    [Fact]
    public async Task A_Job_Belonging_To_Another_Tenant_Is_Invisible_And_Uncancellable()
    {
        using var a = new PreviewFixture(dayCap: 1000, minuteCap: 1000, cloningEnabled: false);
        using var b = new PreviewFixture(dayCap: 1000, minuteCap: 1000, cloningEnabled: false, shared: a);

        var jobId = SeedPreviewUsage(
            b.Factory, b.TenantId, 1, status: VoicePreviewStatus.Pending, projectId: b.ProjectId)[0];
        var service = a.CreateService(new ThrowingTts());

        Assert.Null(await service.GetAsync(a.TenantId, jobId));
        await Assert.ThrowsAsync<NotFoundException>(() => service.CancelAsync(a.TenantId, jobId, a.UserId));

        // The owner can still see and cancel it.
        var ownerService = b.CreateService(new ThrowingTts());
        Assert.NotNull(await ownerService.GetAsync(b.TenantId, jobId));
        var cancelled = await ownerService.CancelAsync(b.TenantId, jobId, b.UserId);
        Assert.Equal(VoicePreviewStatus.Cancelled, cancelled.Status);
    }

    // ==================================================================
    // VoicePreviewService — consent gate decision matrix
    // ==================================================================

    [Fact]
    public async Task Consent_Stock_Voice_Needs_No_Consent_Even_With_The_Kill_Switch_Off()
    {
        using var fixture = new PreviewFixture(dayCap: 100, minuteCap: 1000, cloningEnabled: false);
        var tts = new ThrowingTts();

        var ex = await Assert.ThrowsAsync<ErrorCodeException>(() => fixture.RequestAsync(fixture.CreateService(tts)));

        Assert.Equal(ErrorCodes.ProviderTimeout, ex.ErrorCode);
        Assert.Equal(1, tts.Calls);

        var job = Assert.Single(ReadJobs(fixture.Factory, fixture.TenantId));
        Assert.Equal(VoicePreviewConsentState.Verified, job.ConsentState);
    }

    [Fact]
    public async Task Consent_Cloned_Voice_With_Granted_Covering_Consent_Proceeds()
    {
        using var fixture = new PreviewFixture(dayCap: 100, minuteCap: 1000, cloningEnabled: true);
        SeedConsent(fixture.Factory, fixture.TenantId, ConsentStatus.Granted, $"project:{fixture.ProjectId:N}", fixture.VoiceId);
        var tts = new ThrowingTts();

        var ex = await Assert.ThrowsAsync<ErrorCodeException>(() => fixture.RequestAsync(fixture.CreateService(tts)));

        Assert.Equal(ErrorCodes.ProviderTimeout, ex.ErrorCode);
        Assert.Equal(1, tts.Calls);

        var job = Assert.Single(ReadJobs(fixture.Factory, fixture.TenantId));
        Assert.Equal(VoicePreviewQuotaCheck.Allowed, job.QuotaCheck);
        Assert.Equal(VoicePreviewConsentState.Verified, job.ConsentState);
    }

    [Fact]
    public async Task Consent_Cloned_Voice_Without_Any_Consent_Is_Blocked_And_Persisted()
    {
        using var fixture = new PreviewFixture(dayCap: 100, minuteCap: 1000, cloningEnabled: true);
        var tts = new ThrowingTts();

        var ex = await Assert.ThrowsAsync<ErrorCodeException>(() => fixture.RequestAsync(fixture.CreateService(tts)));
        Assert.Equal(ErrorCodes.ConsentRequired, ex.ErrorCode);
        Assert.Equal(403, ex.StatusCode);
        Assert.StartsWith(VoicePreviewService.VoiceConsentRequiredMarker, ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, tts.Calls);

        var blocked = Assert.Single(ReadJobs(fixture.Factory, fixture.TenantId));
        Assert.Equal(VoicePreviewStatus.Failed, blocked.Status);
        Assert.Equal(VoicePreviewQuotaCheck.Allowed, blocked.QuotaCheck);
        Assert.Equal(VoicePreviewConsentState.Blocked, blocked.ConsentState);
        Assert.Equal(ErrorCodes.ConsentRequired, blocked.ErrorCode);
        Assert.True(blocked.IsTerminal);
    }

    [Fact]
    public async Task Consent_Cloned_Voice_With_Revoked_Consent_Is_Blocked()
    {
        using var fixture = new PreviewFixture(dayCap: 100, minuteCap: 1000, cloningEnabled: true);
        SeedConsent(
            fixture.Factory, fixture.TenantId, ConsentStatus.Revoked, "*", fixture.VoiceId,
            revokedAt: DateTimeOffset.UtcNow);
        var tts = new ThrowingTts();

        var ex = await Assert.ThrowsAsync<ErrorCodeException>(() => fixture.RequestAsync(fixture.CreateService(tts)));

        Assert.Equal(ErrorCodes.ConsentRequired, ex.ErrorCode);
        Assert.Equal(0, tts.Calls);
    }

    [Fact]
    public async Task Consent_Cloned_Voice_With_Expired_Consent_Is_Blocked()
    {
        using var fixture = new PreviewFixture(dayCap: 100, minuteCap: 1000, cloningEnabled: true);
        SeedConsent(fixture.Factory, fixture.TenantId, ConsentStatus.Expired, "*", fixture.VoiceId);
        var tts = new ThrowingTts();

        var ex = await Assert.ThrowsAsync<ErrorCodeException>(() => fixture.RequestAsync(fixture.CreateService(tts)));

        Assert.Equal(ErrorCodes.ConsentRequired, ex.ErrorCode);
        Assert.Equal(0, tts.Calls);
    }

    [Fact]
    public async Task Consent_Scoped_To_Another_Project_Is_Blocked()
    {
        using var fixture = new PreviewFixture(dayCap: 100, minuteCap: 1000, cloningEnabled: true);
        SeedConsent(fixture.Factory, fixture.TenantId, ConsentStatus.Granted, $"project:{Guid.NewGuid():N}", fixture.VoiceId);
        var tts = new ThrowingTts();

        var ex = await Assert.ThrowsAsync<ErrorCodeException>(() => fixture.RequestAsync(fixture.CreateService(tts)));

        Assert.Equal(ErrorCodes.ConsentRequired, ex.ErrorCode);
        Assert.Equal(0, tts.Calls);
    }

    [Fact]
    public async Task Consent_Bound_To_Another_Voice_Is_Blocked()
    {
        using var fixture = new PreviewFixture(dayCap: 100, minuteCap: 1000, cloningEnabled: true);
        SeedConsent(fixture.Factory, fixture.TenantId, ConsentStatus.Granted, "*", Guid.NewGuid());
        var tts = new ThrowingTts();

        var ex = await Assert.ThrowsAsync<ErrorCodeException>(() => fixture.RequestAsync(fixture.CreateService(tts)));

        Assert.Equal(ErrorCodes.ConsentRequired, ex.ErrorCode);
        Assert.Equal(0, tts.Calls);
    }

    [Fact]
    public async Task Consent_From_Another_Tenant_Never_Covers_The_Project()
    {
        using var fixture = new PreviewFixture(dayCap: 100, minuteCap: 1000, cloningEnabled: true);
        SeedConsent(fixture.Factory, Guid.NewGuid(), ConsentStatus.Granted, $"project:{fixture.ProjectId:N}", fixture.VoiceId);
        var tts = new ThrowingTts();

        var ex = await Assert.ThrowsAsync<ErrorCodeException>(() => fixture.RequestAsync(fixture.CreateService(tts)));

        Assert.Equal(ErrorCodes.ConsentRequired, ex.ErrorCode);
        Assert.Equal(0, tts.Calls);
    }

    [Fact]
    public async Task Consent_Kill_Switch_Off_Blocks_Even_A_Granted_Consent()
    {
        using var fixture = new PreviewFixture(
            dayCap: 100, minuteCap: 1000, cloningEnabled: false, voiceType: VoiceType.Cloned);
        SeedConsent(fixture.Factory, fixture.TenantId, ConsentStatus.Granted, "*", fixture.VoiceId);
        var tts = new ThrowingTts();

        var ex = await Assert.ThrowsAsync<ErrorCodeException>(() => fixture.RequestAsync(fixture.CreateService(tts)));

        Assert.Equal(ErrorCodes.ConsentRequired, ex.ErrorCode);
        Assert.Equal(0, tts.Calls);

        var blocked = Assert.Single(ReadJobs(fixture.Factory, fixture.TenantId));
        Assert.Equal(VoicePreviewConsentState.Blocked, blocked.ConsentState);
    }

    [Fact]
    public async Task Consent_Is_Validated_Before_The_Quota_Gate()
    {
        // Cloned + no consent + quota exhausted: the consent denial wins, so no
        // quota row is written and the provider is never reached.
        using var fixture = new PreviewFixture(dayCap: 1, minuteCap: 1000, cloningEnabled: true);
        SeedPreviewUsage(fixture.Factory, fixture.TenantId, 1);
        var tts = new ThrowingTts();

        var ex = await Assert.ThrowsAsync<ErrorCodeException>(() => fixture.RequestAsync(fixture.CreateService(tts)));

        Assert.Equal(ErrorCodes.ConsentRequired, ex.ErrorCode);
        Assert.Equal(0, tts.Calls);

        var jobs = ReadJobs(fixture.Factory, fixture.TenantId);
        Assert.Equal(2, jobs.Count);
        Assert.Contains(
            jobs,
            j => j.ConsentState == VoicePreviewConsentState.Blocked && j.QuotaCheck == VoicePreviewQuotaCheck.Allowed);
        Assert.DoesNotContain(jobs, j => j.QuotaCheck == VoicePreviewQuotaCheck.Denied);
    }

    [Fact]
    public async Task Preview_Failure_Records_The_Provider_Execution_And_Publishes_The_Terminal_Event()
    {
        using var fixture = new PreviewFixture(dayCap: 100, minuteCap: 1000, cloningEnabled: false);
        var publisher = new RecordingPublisher();
        var service = fixture.CreateService(
            new ThrowingTts(new InvalidOperationException("synthetic provider failure")), publisher);

        var ex = await Assert.ThrowsAsync<ErrorCodeException>(() => fixture.RequestAsync(service));
        Assert.Equal(ErrorCodes.ProviderFailed, ex.ErrorCode);
        Assert.Equal(502, ex.StatusCode);
        Assert.StartsWith(VoicePreviewService.PreviewProviderFailedMarker, ex.Message, StringComparison.Ordinal);

        var job = Assert.Single(ReadJobs(fixture.Factory, fixture.TenantId));
        Assert.Equal(VoicePreviewStatus.Failed, job.Status);
        Assert.Equal(ErrorCodes.ProviderFailed, job.ErrorCode);

        // A failed run has no artifact, and the execution row is what carries
        // the provider/latency record (never linked onto a failed job).
        Assert.Null(job.ArtifactId);
        Assert.Null(job.ProviderExecutionId);
        var execution = Assert.Single(ReadExecutions(fixture.Factory, fixture.TenantId));
        Assert.Equal(ProviderCapability.Tts, execution.Capability);
        Assert.Equal(ProviderType.Mock, execution.Provider);

        var message = Assert.Single(publisher.Messages);
        Assert.Equal(job.Id, message.VoicePreviewJobId);
        Assert.Equal(nameof(VoicePreviewStatus.Failed), message.Status);
        Assert.Equal(ErrorCodes.ProviderFailed, message.ErrorCode);
    }

    [Fact]
    public async Task Preview_Timeout_Clocks_The_Run_And_Leaves_No_Artifact()
    {
        using var fixture = new PreviewFixture(dayCap: 100, minuteCap: 1000, cloningEnabled: false);
        var publisher = new RecordingPublisher();

        var ex = await Assert.ThrowsAsync<ErrorCodeException>(
            () => fixture.RequestAsync(fixture.CreateService(new ThrowingTts(), publisher)));

        Assert.Equal(ErrorCodes.ProviderTimeout, ex.ErrorCode);
        Assert.Equal(504, ex.StatusCode);

        var job = Assert.Single(ReadJobs(fixture.Factory, fixture.TenantId));
        Assert.Equal(VoicePreviewStatus.Failed, job.Status);
        Assert.Null(job.ArtifactId);
        Assert.NotNull(job.StartedAt);
        Assert.NotNull(job.CompletedAt);
        Assert.True(job.CompletedAt >= job.StartedAt);
        Assert.Single(ReadExecutions(fixture.Factory, fixture.TenantId));
        Assert.Equal(nameof(VoicePreviewStatus.Failed), Assert.Single(publisher.Messages).Status);
    }

    // ==================================================================
    // VoicePreviewService — ownership, membership, and provider guards
    // ==================================================================

    [Fact]
    public async Task Request_Unknown_Voice_Is_Not_Found()
    {
        using var fixture = new PreviewFixture(dayCap: 100, minuteCap: 1000, cloningEnabled: false);
        var tts = new ThrowingTts();
        var service = fixture.CreateService(tts);

        var ex = await Assert.ThrowsAsync<NotFoundException>(
            () => service.RequestPreviewAsync(
                fixture.TenantId, fixture.ProjectId, fixture.SpeakerId, "voice-that-does-not-exist", "hello", fixture.UserId));

        Assert.Equal(ErrorCodes.NotFound, ex.ErrorCode);
        Assert.Equal(404, ex.StatusCode);
        Assert.Equal(0, tts.Calls);
    }

    [Fact]
    public async Task Request_Unknown_Speaker_Is_Not_Found()
    {
        using var fixture = new PreviewFixture(dayCap: 100, minuteCap: 1000, cloningEnabled: false);
        var tts = new ThrowingTts();

        var ex = await Assert.ThrowsAsync<NotFoundException>(() => fixture.RequestAsync(fixture.CreateService(tts), Guid.NewGuid()));

        Assert.Equal(ErrorCodes.NotFound, ex.ErrorCode);
        Assert.Equal(0, tts.Calls);
    }

    [Fact]
    public async Task Request_Speaker_Owned_By_Another_Project_Is_Forbidden()
    {
        using var fixture = new PreviewFixture(dayCap: 100, minuteCap: 1000, cloningEnabled: false);
        var otherSpeaker = Guid.NewGuid();
        SeedSpeaker(fixture.Factory, fixture.TenantId, Guid.NewGuid(), otherSpeaker);
        var tts = new ThrowingTts();

        var ex = await Assert.ThrowsAsync<ForbiddenException>(
            () => fixture.RequestAsync(fixture.CreateService(tts), otherSpeaker));

        Assert.Equal(ErrorCodes.Forbidden, ex.ErrorCode);
        Assert.Equal(403, ex.StatusCode);
        Assert.Equal(0, tts.Calls);
    }

    [Fact]
    public async Task Request_By_A_Non_Member_Is_Forbidden()
    {
        using var fixture = new PreviewFixture(dayCap: 100, minuteCap: 1000, cloningEnabled: false);
        var tts = new ThrowingTts();

        var ex = await Assert.ThrowsAsync<ForbiddenException>(
            () => fixture.RequestAsAsync(fixture.CreateService(tts), Guid.NewGuid()));

        Assert.Equal(ErrorCodes.Forbidden, ex.ErrorCode);
        Assert.Equal(0, tts.Calls);
    }

    [Fact]
    public async Task Request_Project_Of_Another_Tenant_Is_Forbidden()
    {
        using var fixture = new PreviewFixture(dayCap: 100, minuteCap: 1000, cloningEnabled: false);
        var tts = new ThrowingTts();

        var ex = await Assert.ThrowsAsync<ForbiddenException>(() => fixture.CreateService(tts).RequestPreviewAsync(
            Guid.NewGuid(), fixture.ProjectId, fixture.SpeakerId, "mock-voice-1", "hello", fixture.UserId));

        Assert.Equal(ErrorCodes.Forbidden, ex.ErrorCode);
        Assert.Equal(0, tts.Calls);
    }

    [Fact]
    public async Task Request_A_Non_Mock_Provider_Is_A_Configuration_Error_Not_A_Provider_Call()
    {
        using var fixture = new PreviewFixture(dayCap: 100, minuteCap: 1000, cloningEnabled: false);
        SetVoiceProvider(fixture.Factory, fixture.TenantId, fixture.VoiceId, "openai");
        var tts = new ThrowingTts();

        var ex = await Assert.ThrowsAsync<ErrorCodeException>(() => fixture.RequestAsync(fixture.CreateService(tts)));

        Assert.Equal(ErrorCodes.ProviderConfigurationError, ex.ErrorCode);
        Assert.Contains("openai", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, tts.Calls);
        Assert.Empty(ReadJobs(fixture.Factory, fixture.TenantId));
    }

    [Fact]
    public async Task Request_With_A_Duplicate_Idempotency_Key_Returns_The_Existing_Row_And_Makes_No_Provider_Call()
    {
        using var fixture = new PreviewFixture(dayCap: 100, minuteCap: 1000, cloningEnabled: false);
        var existing = SeedPreviewUsage(
            fixture.Factory, fixture.TenantId, 1, status: VoicePreviewStatus.Completed, projectId: fixture.ProjectId)[0];
        var tts = new ThrowingTts();

        var result = await fixture.CreateService(tts).RequestPreviewAsync(
            fixture.TenantId, fixture.ProjectId, fixture.SpeakerId, "mock-voice-1", "hello", fixture.UserId,
            idempotencyKey: IdempotencyKeyFor(fixture.Factory, fixture.TenantId));

        Assert.True(result.IsDuplicate);
        Assert.Equal(existing, result.Job.Id);
        Assert.Equal(VoicePreviewStatus.Completed, result.Job.Status);
        Assert.Equal(0, tts.Calls);
    }

    [Fact]
    public async Task Request_With_A_Duplicate_Key_Of_Another_Tenant_Is_Not_A_Duplicate()
    {
        using var a = new PreviewFixture(dayCap: 100, minuteCap: 1000, cloningEnabled: false);
        using var b = new PreviewFixture(dayCap: 100, minuteCap: 1000, cloningEnabled: false, shared: a);
        SeedPreviewUsage(b.Factory, b.TenantId, 1, status: VoicePreviewStatus.Completed, projectId: b.ProjectId);
        var tts = new ThrowingTts();

        // Tenant B's idempotency key is invisible to tenant A, so A proceeds all
        // the way to the provider instead of short-circuiting as a duplicate.
        var ex = await Assert.ThrowsAsync<ErrorCodeException>(() => a.CreateService(tts).RequestPreviewAsync(
            a.TenantId, a.ProjectId, a.SpeakerId, "mock-voice-1", "hello", a.UserId,
            idempotencyKey: IdempotencyKeyFor(b.Factory, b.TenantId)));

        Assert.Equal(ErrorCodes.ProviderTimeout, ex.ErrorCode);
        Assert.Equal(1, tts.Calls);
    }

    [Fact]
    public async Task Request_Unknown_Project_Is_Not_Found()
    {
        using var fixture = new PreviewFixture(dayCap: 100, minuteCap: 1000, cloningEnabled: false);
        var tts = new ThrowingTts();

        var ex = await Assert.ThrowsAsync<NotFoundException>(() => fixture.CreateService(tts).RequestPreviewAsync(
            fixture.TenantId, Guid.NewGuid(), fixture.SpeakerId, "mock-voice-1", "hello", fixture.UserId));

        Assert.Equal(ErrorCodes.NotFound, ex.ErrorCode);
        Assert.Equal(0, tts.Calls);
    }

    [Fact]
    public async Task Request_Soft_Deleted_Project_Reads_As_Not_Found()
    {
        using var fixture = new PreviewFixture(dayCap: 100, minuteCap: 1000, cloningEnabled: false);
        SoftDeleteProject(fixture.Factory, fixture.TenantId, fixture.ProjectId);
        var tts = new ThrowingTts();

        var ex = await Assert.ThrowsAsync<NotFoundException>(() => fixture.RequestAsync(fixture.CreateService(tts)));

        Assert.Equal(ErrorCodes.NotFound, ex.ErrorCode);
        Assert.Equal(0, tts.Calls);
    }

    [Fact]
    public async Task Preview_Cancelled_By_The_Caller_Leaves_The_Job_Running_And_Records_No_Failure()
    {
        using var fixture = new PreviewFixture(dayCap: 100, minuteCap: 1000, cloningEnabled: false);
        using var cts = new CancellationTokenSource();
        var tts = new ThrowingTts(new OperationCanceledException(cts.Token), cts);
        var service = fixture.CreateService(tts);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.RequestPreviewAsync(
                fixture.TenantId, fixture.ProjectId, fixture.SpeakerId, "mock-voice-1", "hello", fixture.UserId,
                cancellationToken: cts.Token));

        Assert.Equal(1, tts.Calls);

        // An aborted call is not a provider failure: the row stays Running so a
        // later cancel or retry can finish it, and nothing is recorded as failed.
        var job = Assert.Single(ReadJobs(fixture.Factory, fixture.TenantId));
        Assert.Equal(VoicePreviewStatus.Running, job.Status);
        Assert.Null(job.ErrorCode);
        Assert.Null(job.CompletedAt);
        Assert.Empty(ReadExecutions(fixture.Factory, fixture.TenantId));
    }

    // ==================================================================
    // VoicePreviewService — cancel semantics
    // ==================================================================

    [Fact]
    public async Task Cancel_Pending_Job_Publishes_A_Cancelled_Terminal_Event()
    {
        using var fixture = new PreviewFixture(dayCap: 100, minuteCap: 1000, cloningEnabled: false);
        var publisher = new RecordingPublisher();
        var service = fixture.CreateService(new ThrowingTts(), publisher);
        var jobId = SeedPreviewUsage(
            fixture.Factory, fixture.TenantId, 1, status: VoicePreviewStatus.Pending, projectId: fixture.ProjectId)[0];

        var cancelled = await service.CancelAsync(fixture.TenantId, jobId, fixture.UserId);

        Assert.Equal(VoicePreviewStatus.Cancelled, cancelled.Status);
        Assert.True(cancelled.IsTerminal);
        Assert.NotNull(cancelled.CompletedAt);

        var message = Assert.Single(publisher.Messages);
        Assert.Equal(jobId, message.VoicePreviewJobId);
        Assert.Equal(nameof(VoicePreviewStatus.Cancelled), message.Status);
        Assert.Null(message.ErrorCode);
    }

    [Theory]
    [InlineData(VoicePreviewStatus.Completed)]
    [InlineData(VoicePreviewStatus.Failed)]
    [InlineData(VoicePreviewStatus.Cancelled)]
    public async Task Cancel_Terminal_Job_Conflicts_And_Leaves_The_Row_Unchanged(VoicePreviewStatus status)
    {
        using var fixture = new PreviewFixture(dayCap: 100, minuteCap: 1000, cloningEnabled: false);
        var service = fixture.CreateService(new ThrowingTts());
        var jobId = SeedPreviewUsage(
            fixture.Factory, fixture.TenantId, 1, status: status, projectId: fixture.ProjectId)[0];

        var ex = await Assert.ThrowsAsync<ErrorCodeException>(
            () => service.CancelAsync(fixture.TenantId, jobId, fixture.UserId));

        Assert.Equal(ErrorCodes.Conflict, ex.ErrorCode);
        Assert.Equal(409, ex.StatusCode);
        Assert.StartsWith(VoicePreviewService.PreviewStateConflictMarker, ex.Message, StringComparison.Ordinal);

        var stored = Assert.Single(ReadJobs(fixture.Factory, fixture.TenantId));
        Assert.Equal(status, stored.Status);
    }

    [Fact]
    public async Task Cancel_Requires_A_Project_Membership()
    {
        using var fixture = new PreviewFixture(dayCap: 100, minuteCap: 1000, cloningEnabled: false);
        var service = fixture.CreateService(new ThrowingTts());
        var jobId = SeedPreviewUsage(
            fixture.Factory, fixture.TenantId, 1, status: VoicePreviewStatus.Pending, projectId: fixture.ProjectId)[0];

        var ex = await Assert.ThrowsAsync<ForbiddenException>(
            () => service.CancelAsync(fixture.TenantId, jobId, Guid.NewGuid()));
        Assert.Equal(ErrorCodes.Forbidden, ex.ErrorCode);
    }

    // ==================================================================
    // VoicePreviewJob — quota/consent state surface
    // ==================================================================

    [Fact]
    public void VoicePreviewJob_Quota_And_Consent_Enums_Are_Closed_Sets()
    {
        Assert.Equal(
            new[] { nameof(VoicePreviewQuotaCheck.Allowed), nameof(VoicePreviewQuotaCheck.Denied) },
            Enum.GetNames<VoicePreviewQuotaCheck>());

        Assert.Equal(
            new[] { nameof(VoicePreviewConsentState.Verified), nameof(VoicePreviewConsentState.Blocked) },
            Enum.GetNames<VoicePreviewConsentState>());
    }

    [Theory]
    [InlineData(VoicePreviewStatus.Completed)]
    [InlineData(VoicePreviewStatus.Failed)]
    [InlineData(VoicePreviewStatus.Cancelled)]
    public void VoicePreviewJob_Quota_Denied_And_Consent_Blocked_Rows_Are_Terminal_And_Not_Cancellable(
        VoicePreviewStatus status)
    {
        var job = NewJob(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), status,
            quotaCheck: VoicePreviewQuotaCheck.Denied,
            quotaCheckReason: "daily cap of 1 previews exceeded",
            consentState: VoicePreviewConsentState.Blocked,
            errorCode: ErrorCodes.QuotaExceeded,
            errorMessage: VoicePreviewService.PreviewQuotaExceededMarker + ": daily cap of 1 previews exceeded; no provider call was made.");

        Assert.True(job.IsTerminal);
        Assert.Null(job.ArtifactId);
        Assert.Null(job.ProviderExecutionId);

        var ex = Assert.Throws<DomainException>(() => job.MarkCancelled(job.CreatedAt.AddMinutes(1)));
        Assert.Contains(VoicePreviewService.PreviewStateConflictMarker, ex.Message, StringComparison.Ordinal);
        Assert.Equal(status, job.Status);
    }

    [Fact]
    public void VoicePreviewJob_Denied_Or_Blocked_Failed_Row_Cannot_Start_Or_Complete()
    {
        var job = NewJob(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), VoicePreviewStatus.Failed,
            quotaCheck: VoicePreviewQuotaCheck.Denied,
            quotaCheckReason: "daily cap of 1 previews exceeded",
            consentState: VoicePreviewConsentState.Blocked,
            errorCode: ErrorCodes.QuotaExceeded,
            errorMessage: "denied");

        var start = Assert.Throws<DomainException>(() => job.MarkRunning(job.CreatedAt.AddMinutes(1)));
        Assert.Contains(VoicePreviewService.PreviewStateConflictMarker, start.Message, StringComparison.Ordinal);

        var complete = Assert.Throws<DomainException>(
            () => job.MarkCompleted(Guid.NewGuid(), Guid.NewGuid(), job.CreatedAt.AddMinutes(1)));
        Assert.Contains(VoicePreviewService.PreviewStateConflictMarker, complete.Message, StringComparison.Ordinal);

        Assert.Equal(VoicePreviewStatus.Failed, job.Status);
        Assert.Null(job.ArtifactId);
    }

    [Fact]
    public void VoicePreviewJob_Denied_Quota_Row_Normalises_Blank_Reason_And_Error_Code_To_Null()
    {
        // Explicit safe default: a blank reason/error code is stored as null
        // rather than rejected, so a persisted denial row is always readable.
        var job = NewJob(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), VoicePreviewStatus.Failed,
            quotaCheck: VoicePreviewQuotaCheck.Denied,
            quotaCheckReason: "   ",
            consentState: VoicePreviewConsentState.Blocked,
            errorCode: "   ",
            errorMessage: "   ");

        Assert.Null(job.QuotaCheckReason);
        Assert.Null(job.ErrorCode);
        Assert.Null(job.ErrorMessage);
        Assert.Equal(VoicePreviewConsentState.Blocked, job.ConsentState);
    }

    // ==================================================================
    // Fixtures
    // ==================================================================

    private static ConsentRecord NewConsent(
        ConsentStatus status,
        string subject,
        string evidence,
        string scope,
        string jurisdiction,
        Guid? voiceProfileId = null,
        DateTimeOffset? revokedAt = null)
    {
        return new ConsentRecord(
            Guid.NewGuid(), Guid.NewGuid(), subject, evidence, scope, jurisdiction,
            status, voiceProfileId, Granted, revokedAt);
    }

    private static VoicePreviewJob NewJob(
        Guid tenantId,
        Guid projectId,
        Guid speakerId,
        VoicePreviewStatus status,
        Guid? artifactId = null,
        Guid? providerExecutionId = null,
        VoicePreviewQuotaCheck quotaCheck = VoicePreviewQuotaCheck.Allowed,
        string? quotaCheckReason = null,
        VoicePreviewConsentState consentState = VoicePreviewConsentState.Verified,
        string? errorCode = null,
        string? errorMessage = null)
    {
        return new VoicePreviewJob(
            Guid.NewGuid(), tenantId, projectId, speakerId, "mock-voice-1", "hello",
            status, Guid.NewGuid(), null, quotaCheck, quotaCheckReason, consentState,
            providerExecutionId, artifactId, errorCode, errorMessage,
            Granted, status == VoicePreviewStatus.Pending ? null : Granted.AddMinutes(1), Granted.AddMinutes(1));
    }

    private static TestContextFactory CreateFactory()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase("VoicePreviewQuotaConsentTests-" + Guid.NewGuid().ToString("N"))
            .ReplaceService<IModelCacheKeyFactory, TenantModelCacheKeyFactory>()
            .Options;
        return new TestContextFactory(options);
    }

    private static MediaPreviewGenerator NewGenerator(IStageExecutionContextFactory factory)
    {
        return new MediaPreviewGenerator(
            factory,
            new ArtifactService(factory, new UnusedStorage()),
            new AuditService(factory),
            MsOptions.Create(new PreviewOptions()),
            NullLogger<MediaPreviewGenerator>.Instance);
    }

    private static QcEvidenceLinker NewLinker(IStageExecutionContextFactory factory)
    {
        return new QcEvidenceLinker(
            factory,
            new ArtifactService(factory, new UnusedStorage()),
            new AuditService(factory),
            NullLogger<QcEvidenceLinker>.Instance);
    }

    private static VoicePreviewService NewService(IStageExecutionContextFactory factory)
    {
        return new ServiceBuilder(factory).Build();
    }

    private static string Ascii(byte[] buffer, int offset, int length)
        => Encoding.ASCII.GetString(buffer, offset, length);

    private static Guid SeedQcIssue(TestContextFactory factory, Guid tenantId, Guid projectId)
    {
        var id = Guid.NewGuid();
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = factory.CreateSetup();
            db.Set<QualityResult>().Add(new QualityResult(
                id, tenantId, projectId, Guid.NewGuid(), ScopeType.Run, "run", null,
                QualityStatus.RetryRequired, "QC_CLIP", "warning", "Synthetic QC issue",
                null, null, DateTimeOffset.UtcNow));
            db.SaveChanges();
        }

        return id;
    }

    private static void SeedConsent(
        TestContextFactory factory,
        Guid tenantId,
        ConsentStatus status,
        string scope,
        Guid? boundVoiceProfileId = null,
        DateTimeOffset? revokedAt = null)
    {
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = factory.CreateSetup();
            db.Set<ConsentRecord>().Add(new ConsentRecord(
                Guid.NewGuid(), tenantId, "synthetic-subject", "synthetic-evidence", scope, "EU",
                status, boundVoiceProfileId, Granted, revokedAt));
            db.SaveChanges();
        }
    }

    private static List<Guid> SeedPreviewUsage(
        TestContextFactory factory,
        Guid tenantId,
        int count,
        int minutesAgo = 0,
        VoicePreviewStatus status = VoicePreviewStatus.Completed,
        Guid? projectId = null)
    {
        var stamp = DateTimeOffset.UtcNow.AddMinutes(-minutesAgo);
        var terminal = status is VoicePreviewStatus.Completed or VoicePreviewStatus.Failed or VoicePreviewStatus.Cancelled;
        var ids = new List<Guid>(count);
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = factory.CreateSetup();
            for (var i = 0; i < count; i++)
            {
                var id = Guid.NewGuid();
                ids.Add(id);
                db.Set<VoicePreviewJob>().Add(new VoicePreviewJob(
                    id, tenantId, projectId ?? Guid.NewGuid(), Guid.NewGuid(), "mock-voice-1", "hello",
                    status, Guid.NewGuid(), "seed-" + id.ToString("N"),
                    VoicePreviewQuotaCheck.Allowed, null, VoicePreviewConsentState.Verified,
                    terminal ? Guid.NewGuid() : null,
                    terminal ? Guid.NewGuid() : null,
                    null, null, stamp, terminal ? stamp : null, terminal ? stamp : null));
            }

            db.SaveChanges();
        }

        return ids;
    }

    private static List<VoicePreviewJob> ReadJobs(TestContextFactory factory, Guid tenantId)
    {
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = factory.CreateSetup();
            return db.Set<VoicePreviewJob>().AsNoTracking().ToList();
        }
    }

    private static string IdempotencyKeyFor(TestContextFactory factory, Guid tenantId)
    {
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = factory.CreateSetup();
            return db.Set<VoicePreviewJob>()
                .Select(j => j.IdempotencyKey)
                .First(k => k != null)!;
        }
    }

    private static void SeedSpeaker(TestContextFactory factory, Guid tenantId, Guid projectId, Guid speakerId)
    {
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = factory.CreateSetup();
            db.Set<Speaker>().Add(new Speaker(
                speakerId, tenantId, projectId, "speaker-2", "Synthetic speaker 2",
                0, 1000, "manual", "v1", 1.0, null, DateTimeOffset.UtcNow));
            db.SaveChanges();
        }
    }

    private static void SetVoiceProvider(TestContextFactory factory, Guid tenantId, Guid voiceId, string provider)
    {
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = factory.CreateSetup();
            var existing = db.Set<VoiceProfile>().Single(v => v.Id == voiceId);
            db.Set<VoiceProfile>().Remove(existing);
            db.SaveChanges();

            db.Set<VoiceProfile>().Add(new VoiceProfile(
                voiceId, tenantId, provider, "mock-voice-1", "1", "es", VoiceType.Stock, false, null, DateTimeOffset.UtcNow));
            db.SaveChanges();
        }
    }

    private static void SoftDeleteProject(TestContextFactory factory, Guid tenantId, Guid projectId)
    {
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = factory.CreateSetup();
            var project = db.Set<DubbingProject>().Single(p => p.Id == projectId);
            db.Entry(project).Property("IsDeleted").CurrentValue = true;
            db.SaveChanges();
        }
    }

    private static List<ProviderExecution> ReadExecutions(TestContextFactory factory, Guid tenantId)
    {
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = factory.CreateSetup();
            return db.Set<ProviderExecution>().AsNoTracking().ToList();
        }
    }

    /// <summary>
    /// A fully seeded tenant: project, owner, speaker, membership, and a mock
    /// voice profile. Two fixtures can share one <see cref="TestContextFactory"/>
    /// to model two tenants in the same database.
    /// </summary>
    private sealed class PreviewFixture : IDisposable
    {
        private readonly int _dayCap;
        private readonly int _minuteCap;
        private readonly bool _cloningEnabled;
        private readonly VoiceType _voiceType;
        private readonly bool _ownsFactory;

        public PreviewFixture(
            int dayCap,
            int minuteCap,
            bool cloningEnabled,
            VoiceType? voiceType = null,
            Guid? tenant = null,
            PreviewFixture? shared = null)
        {
            _dayCap = dayCap;
            _minuteCap = minuteCap;
            _cloningEnabled = cloningEnabled;
            _voiceType = voiceType ?? (cloningEnabled ? VoiceType.Cloned : VoiceType.Stock);

            Factory = shared?.Factory ?? CreateFactory();
            _ownsFactory = shared is null;

            TenantId = tenant ?? Guid.NewGuid();
            ProjectId = Guid.NewGuid();
            SpeakerId = Guid.NewGuid();
            UserId = Guid.NewGuid();
            VoiceId = Guid.NewGuid();

            Seed();
        }

        public TestContextFactory Factory { get; }

        public Guid TenantId { get; }

        public Guid ProjectId { get; }

        public Guid SpeakerId { get; }

        public Guid UserId { get; }

        public Guid VoiceId { get; }

        public VoicePreviewService CreateService(ITtsProvider tts, IVoicePreviewEventPublisher? publisher = null)
        {
            return new ServiceBuilder(Factory)
                .With(_dayCap, _minuteCap, _cloningEnabled)
                .Build(tts, publisher ?? new RecordingPublisher());
        }

        public Task<VoicePreviewRequestResult> RequestAsync(VoicePreviewService service)
        {
            return service.RequestPreviewAsync(
                TenantId, ProjectId, SpeakerId, "mock-voice-1", "synthetic preview text", UserId);
        }

        public Task<VoicePreviewRequestResult> RequestAsync(VoicePreviewService service, Guid speakerId)
        {
            return service.RequestPreviewAsync(
                TenantId, ProjectId, speakerId, "mock-voice-1", "synthetic preview text", UserId);
        }

        public Task<VoicePreviewRequestResult> RequestAsAsync(VoicePreviewService service, Guid actorUserId)
        {
            return service.RequestPreviewAsync(
                TenantId, ProjectId, SpeakerId, "mock-voice-1", "synthetic preview text", actorUserId);
        }

        public void Dispose()
        {
            if (_ownsFactory)
            {
                Factory.Dispose();
            }
        }

        private void Seed()
        {
            using (TenantContext.BeginScope(TenantId))
            {
                using var db = Factory.CreateSetup();
                var now = DateTimeOffset.UtcNow;
                db.Set<DubbingProject>().Add(new DubbingProject(
                    ProjectId, TenantId, "en", "es", ProjectStatus.Created, "{}", new string('a', 64),
                    null, null, now, now, name: "Synthetic preview project", ownerUserId: UserId));
                db.Set<Speaker>().Add(new Speaker(
                    SpeakerId, TenantId, ProjectId, "speaker-1", "Synthetic speaker",
                    0, 1000, "manual", "v1", 1.0, null, now));
                db.Set<ProjectMembership>().Add(new ProjectMembership(
                    Guid.NewGuid(), TenantId, ProjectId, UserId, ProjectRole.ProjectEditor, null, now));
                db.Set<VoiceProfile>().Add(new VoiceProfile(
                    VoiceId, TenantId, "mock", "mock-voice-1", "1", "es", _voiceType, _cloningEnabled, null, now));
                db.SaveChanges();
            }
        }
    }

    private sealed class ServiceBuilder
    {
        private readonly IStageExecutionContextFactory _factory;
        private int _dayCap = 200;
        private int _minuteCap = 10;
        private bool _cloningEnabled;

        public ServiceBuilder(IStageExecutionContextFactory factory)
        {
            _factory = factory;
        }

        public ServiceBuilder With(int dayCap, int minuteCap, bool cloningEnabled)
        {
            _dayCap = dayCap;
            _minuteCap = minuteCap;
            _cloningEnabled = cloningEnabled;
            return this;
        }

        public VoicePreviewService Build(ITtsProvider? tts = null, IVoicePreviewEventPublisher? publisher = null)
        {
            return new VoicePreviewService(
                _factory,
                tts ?? new ThrowingTts(),
                new ProviderExecutionRecorder(_factory),
                new ArtifactService(_factory, new UnusedStorage()),
                new AuditService(_factory),
                publisher ?? new RecordingPublisher(),
                MsOptions.Create(new PreviewOptions
                {
                    MaxPreviewsPerDayPerTenant = _dayCap,
                    MaxPreviewsPerMinutePerTenant = _minuteCap,
                }),
                MsOptions.Create(new VoiceOptions { CloningEnabled = _cloningEnabled }),
                NullLogger<VoicePreviewService>.Instance);
        }
    }

    private sealed class ThrowingTts : ITtsProvider
    {
        private readonly Exception _failure;
        private readonly CancellationTokenSource? _cancel;

        public ThrowingTts(Exception? failure = null, CancellationTokenSource? cancel = null)
        {
            _failure = failure ?? new ErrorCodeException(ErrorCodes.ProviderTimeout, "synthetic timeout");
            _cancel = cancel;
        }

        public int Calls { get; private set; }

        public Task<TtsResponse> SynthesizeAsync(TtsRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            _cancel?.Cancel();
            throw _failure;
        }
    }

    private sealed class RecordingPublisher : IVoicePreviewEventPublisher
    {
        public List<VoicePreviewCompleted> Messages { get; } = [];

        public Task PublishAsync(VoicePreviewCompleted message, CancellationToken cancellationToken = default)
        {
            Messages.Add(message);
            return Task.CompletedTask;
        }
    }

    private sealed class UnusedStorage : IArtifactStorage
    {
        public Task UploadAsync(Stream content, string storageKey, string contentType, CancellationToken ct)
            => throw new InvalidOperationException("Object storage must not be reached in unit tests.");

        public Task<Stream> DownloadAsync(string storageKey, CancellationToken ct)
            => throw new InvalidOperationException("Object storage must not be reached in unit tests.");

        public Task<bool> ExistsAsync(string storageKey, CancellationToken ct)
            => throw new InvalidOperationException("Object storage must not be reached in unit tests.");

        public Task DeleteAsync(string storageKey, CancellationToken ct)
            => throw new InvalidOperationException("Object storage must not be reached in unit tests.");

        public Task<string> GetPresignedDownloadUrlAsync(string storageKey, TimeSpan expiry, CancellationToken ct)
            => throw new InvalidOperationException("Object storage must not be reached in unit tests.");

        public Task<string> GetPresignedUploadUrlAsync(string storageKey, TimeSpan expiry, CancellationToken ct)
            => throw new InvalidOperationException("Object storage must not be reached in unit tests.");

        public Task<string?> GetStorageChecksumAsync(string storageKey, CancellationToken ct)
            => throw new InvalidOperationException("Object storage must not be reached in unit tests.");
    }

    /// <summary>
    /// Proves "no database access" by failing loudly on the first context.
    /// </summary>
    private sealed class ThrowingContextFactory : IStageExecutionContextFactory
    {
        public DbContext CreateDbContext()
            => throw new InvalidOperationException("The database must not be reached for this call.");
    }

    private sealed class TestAppDbContext : AppDbContext
    {
        public TestAppDbContext(DbContextOptions<AppDbContext> options)
            : base(options)
        {
        }
    }

    private sealed class TestContextFactory : IStageExecutionContextFactory, IDisposable
    {
        private readonly DbContextOptions<AppDbContext> _options;
        private readonly List<TestAppDbContext> _all = [];

        public TestContextFactory(DbContextOptions<AppDbContext> options)
        {
            _options = options;
        }

        public TestAppDbContext CreateSetup() => new(_options);

        DbContext IStageExecutionContextFactory.CreateDbContext()
        {
            var context = CreateSetup();
            _all.Add(context);
            return context;
        }

        public void Dispose()
        {
            foreach (var context in _all)
            {
                context.Dispose();
            }
        }
    }
}
