using System.Data.Common;
using System.Net.Sockets;
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
using DubbingPlatform.Domain.Identity;
using DubbingPlatform.Infrastructure.Persistence;
using DubbingPlatform.Infrastructure.Persistence.Interceptors;
using EFCore.NamingConventions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Testcontainers.PostgreSql;
using Xunit.Abstractions;

namespace DubbingPlatform.IntegrationTests.Previews;

public sealed class PreviewArtifactTests
{
    private readonly ITestOutputHelper _output;

    public PreviewArtifactTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void Preview_Text_Requires_NonEmpty_And_Bounded()
    {
        Assert.Throws<ErrorCodeException>(() => VoicePreviewService.RequirePreviewText(null));
        Assert.Throws<ErrorCodeException>(() => VoicePreviewService.RequirePreviewText("   "));
        var overlong = Assert.Throws<ErrorCodeException>(() => VoicePreviewService.RequirePreviewText(new string('x', VoicePreviewJob.MaxTextLength + 1)));
        Assert.Equal(ErrorCodes.ValidationFailed, overlong.ErrorCode);
        Assert.Equal(400, overlong.StatusCode);
        Assert.Contains(VoicePreviewService.PreviewTextInvalidMarker, overlong.Message, StringComparison.Ordinal);
        Assert.Equal("hello", VoicePreviewService.RequirePreviewText("  hello  "));
    }

    [Fact]
    public void Voice_Id_Server_Resolved_Rejects_Urls()
    {
        Assert.Throws<ErrorCodeException>(() => VoicePreviewService.RequireVoiceId(null));
        Assert.Throws<ErrorCodeException>(() => VoicePreviewService.RequireVoiceId("https://example.com/sample.mp3"));
        Assert.Equal("mock-1", VoicePreviewService.RequireVoiceId("  mock-1  "));
    }

    [Fact]
    public void Preview_State_Machine_Allows_Only_Legal_Transitions()
    {
        var now = DateTimeOffset.UtcNow;
        var job = NewJob(now);

        var running = Assert.Throws<DomainException>(() => job.MarkCompleted(Guid.NewGuid(), Guid.NewGuid(), now));
        Assert.Contains(VoicePreviewService.PreviewStateConflictMarker, running.Message, StringComparison.Ordinal);

        job.MarkRunning(now);
        Assert.Equal(VoicePreviewStatus.Running, job.Status);

        var artifactId = Guid.NewGuid();
        var executionId = Guid.NewGuid();
        job.MarkCompleted(artifactId, executionId, now);
        Assert.Equal(VoicePreviewStatus.Completed, job.Status);
        Assert.Equal(artifactId, job.ArtifactId);
        Assert.Equal(executionId, job.ProviderExecutionId);
        Assert.True(job.IsTerminal);

        var cancelTerminal = Assert.Throws<DomainException>(() => job.MarkCancelled(now));
        Assert.Contains(VoicePreviewService.PreviewStateConflictMarker, cancelTerminal.Message, StringComparison.Ordinal);

        var cancellable = NewJob(now);
        cancellable.MarkCancelled(now);
        Assert.Equal(VoicePreviewStatus.Cancelled, cancellable.Status);
    }

    [Fact]
    public void Preview_Text_Truncated_At_Entity_Boundary()
    {
        var now = DateTimeOffset.UtcNow;
        var job = NewJob(now, new string('t', VoicePreviewJob.MaxTextLength + 50));
        Assert.Equal(VoicePreviewJob.MaxTextLength, job.Text.Length);
        Assert.Throws<DomainException>(() => NewJob(now, "   "));
    }

    [Fact]
    public void Peaks_Contain_All_Resolutions_And_Are_Deterministic()
    {
        var json = MediaPreviewGenerator.BuildWaveformPeaksJson(new string('a', 64), 30000, 44100);
        using var first = JsonDocument.Parse(json);
        var resolutions = first.RootElement.GetProperty("resolutions");
        Assert.Equal(64, resolutions.GetProperty("64").GetArrayLength());
        Assert.Equal(256, resolutions.GetProperty("256").GetArrayLength());
        Assert.Equal(1024, resolutions.GetProperty("1024").GetArrayLength());

        var again = MediaPreviewGenerator.BuildWaveformPeaksJson(new string('a', 64), 30000, 44100);
        Assert.Equal(json, again);

        var empty = MediaPreviewGenerator.BuildEmptyPeaksJson(0, 8000);
        using var parsed = JsonDocument.Parse(empty);
        Assert.True(parsed.RootElement.GetProperty("peaksMissing").GetBoolean());
        Assert.Equal(0, parsed.RootElement.GetProperty("resolutions").GetProperty("64").GetArrayLength());

        Assert.False(MediaPreviewGenerator.HasAudioTrack(null));
        Assert.False(MediaPreviewGenerator.HasAudioTrack("none"));
        Assert.True(MediaPreviewGenerator.HasAudioTrack("aac"));
        Assert.True(MediaPreviewGenerator.HasVideoTrack("h264"));
        Assert.False(MediaPreviewGenerator.HasVideoTrack(null));
    }

    [Fact]
    public void Clip_Range_Requires_Consistent_Bounds()
    {
        QcEvidenceLinker.RequireClipRange(null, null);
        QcEvidenceLinker.RequireClipRange(0, 1000);
        Assert.Throws<DomainException>(() => QcEvidenceLinker.RequireClipRange(0, null));
        Assert.Throws<DomainException>(() => QcEvidenceLinker.RequireClipRange(1000, 0));
        Assert.Throws<DomainException>(() => QcEvidenceLinker.RequireClipRange(-1, 10));
        Assert.Throws<DomainException>(() => new VoicePreviewJob(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            "voice-1", "hi", VoicePreviewStatus.Pending, Guid.NewGuid(), null,
            VoicePreviewQuotaCheck.Allowed, null, VoicePreviewConsentState.Verified,
            null, null, null, null, DateTimeOffset.UtcNow, null, null).MarkRunning(DateTimeOffset.UtcNow.AddHours(-1)));
    }

    [Fact]
    public void Preview_Audio_Is_Valid_Wav()
    {
        var wav = PreviewAudio.BuildWav(1000);
        Assert.Equal((byte)'R', wav[0]);
        Assert.Equal((byte)'I', wav[1]);
        Assert.Equal(44 + 16000 * 2, wav.Length);
    }

    [Fact]
    public void Preview_Message_Uses_Frozen_Envelope()
    {
        var tenantId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var runScope = Guid.NewGuid();
        var job = NewJob(DateTimeOffset.UtcNow);

        var message = VoicePreviewService.BuildCompletedMessage(tenantId, projectId, runScope, job, null);

        Assert.Equal(MessageVersionPolicy.CurrentVersion, message.SchemaVersion);
        Assert.Equal(tenantId, message.TenantId);
        Assert.Equal(projectId, message.ProjectId);
        Assert.Equal(runScope, message.ProcessingRunId);
        Assert.Equal("VoicePreview", message.ScopeType);
        Assert.Equal(job.Id, message.VoicePreviewJobId);
        Assert.Equal(job.Status.ToString(), message.Status);
        Assert.Null(message.SegmentId);
        Assert.NotEqual(Guid.Empty, message.MessageId);
    }

    [Fact]
    public void Voice_Preview_Public_Id_Uses_Vpv_Prefix()
    {
        var id = Guid.NewGuid();
        Assert.Equal("vpv_" + id.ToString("N"), PublicIdMapper.ToPublic(id, PublicIdMapper.VoicePreviewPrefix));
        Assert.Equal($"vpv_{id:N}", PublicIdMapper.ToPublic(id, PublicIdMapper.PrefixFor<VoicePreviewJob>()));
    }

    [SkippableFact]
    public async Task Lifecycle_Pending_Running_Completed()
    {
        var container = await StartContainerAsync().ConfigureAwait(true);
        await using (container.ConfigureAwait(true))
        {
            var options = CreateOptions(container);
            await MigrateAsync(options).ConfigureAwait(true);
            var seed = await SeedAsync(options).ConfigureAwait(true);
            var tts = new FakeTts();
            var publisher = new FakePublisher();
            var storage = new FakeStorage();

            var service = CreatePreviewService(options, tts, publisher, storage, seed);
            var result = await service.RequestPreviewAsync(
                seed.TenantId, seed.ProjectId, seed.SpeakerId, seed.StockVoiceId,
                "Hello preview", seed.RequesterId, "lifecycle-key").ConfigureAwait(true);

            Assert.False(result.IsDuplicate);
            Assert.Equal(VoicePreviewStatus.Completed, result.Job.Status);
            Assert.NotNull(result.Job.ArtifactId);
            Assert.NotNull(result.Job.ProviderExecutionId);
            Assert.Equal(VoicePreviewQuotaCheck.Allowed, result.Job.QuotaCheck);
            Assert.Equal(VoicePreviewConsentState.Verified, result.Job.ConsentState);
            Assert.Equal("Hello preview", result.Job.Text);
            Assert.Single(publisher.Messages);
            Assert.Equal(result.Job.Id, publisher.Messages[0].VoicePreviewJobId);
            Assert.Equal("Completed", publisher.Messages[0].Status);
            Assert.Equal(result.Job.ArtifactId, publisher.Messages[0].ArtifactId);
            Assert.Equal(1, tts.Calls);

            using (TenantContext.BeginScope(seed.TenantId))
            {
                using var db = new AppDbContext(options);
                var artifact = await db.Set<Artifact>()
                    .AsNoTracking()
                    .FirstOrDefaultAsync(a => a.Id == result.Job.ArtifactId!.Value).ConfigureAwait(true);
                Assert.NotNull(artifact);
                Assert.Equal(ArtifactType.VoicePreviewAudio, artifact.Type);
                Assert.Contains("voicePreviewJobId", artifact.MetadataJson ?? string.Empty, StringComparison.Ordinal);
                Assert.DoesNotContain("Hello preview", artifact.MetadataJson ?? string.Empty, StringComparison.Ordinal);

                var execution = await db.Set<ProviderExecution>()
                    .AsNoTracking()
                    .FirstOrDefaultAsync(e => e.Id == result.Job.ProviderExecutionId!.Value).ConfigureAwait(true);
                Assert.NotNull(execution);
                Assert.Equal(ProviderType.Mock, execution.Provider);
                Assert.Equal(ProviderCapability.Tts, execution.Capability);
                Assert.Equal(OutcomeClass.Success, execution.Outcome);
                Assert.True(execution.LatencyMs >= 0);
                Assert.Null(execution.FallbackReason);

                var audits = await db.Set<AuditEvent>()
                    .AsNoTracking()
                    .Where(a => a.ResourceId == result.Job.Id.ToString("N"))
                    .ToListAsync().ConfigureAwait(true);
                Assert.Contains(audits, a => a.Action == VoicePreviewService.AuditRequested);
                Assert.Contains(audits, a => a.Action == VoicePreviewService.AuditCompleted);
                Assert.DoesNotContain(audits, a => (a.DetailsJson ?? string.Empty).Contains("Hello preview", StringComparison.Ordinal));
            }
        }
    }

    [SkippableFact]
    public async Task Cancel_Pending_Job_And_Reject_Terminal_Cancel()
    {
        var container = await StartContainerAsync().ConfigureAwait(true);
        await using (container.ConfigureAwait(true))
        {
            var options = CreateOptions(container);
            await MigrateAsync(options).ConfigureAwait(true);
            var seed = await SeedAsync(options).ConfigureAwait(true);
            var publisher = new FakePublisher();

            var pendingId = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;
            using (TenantContext.BeginScope(seed.TenantId))
            {
                using var db = new AppDbContext(options);
                db.Set<VoicePreviewJob>().Add(new VoicePreviewJob(
                    pendingId, seed.TenantId, seed.ProjectId, seed.SpeakerId, seed.StockVoiceId,
                    "cancel me", VoicePreviewStatus.Pending, seed.RequesterId, "cancel-key",
                    VoicePreviewQuotaCheck.Allowed, null, VoicePreviewConsentState.Verified,
                    null, null, null, null, now, null, null));
                await db.SaveChangesAsync().ConfigureAwait(true);
            }

            var service = CreatePreviewService(options, new FakeTts(), publisher, new FakeStorage(), seed);
            var cancelled = await service.CancelAsync(seed.TenantId, pendingId, seed.RequesterId).ConfigureAwait(true);
            Assert.Equal(VoicePreviewStatus.Cancelled, cancelled.Status);
            Assert.Single(publisher.Messages);
            Assert.Equal("Cancelled", publisher.Messages[0].Status);

            var rejected = await Assert.ThrowsAsync<ErrorCodeException>(() => service.CancelAsync(
                seed.TenantId, pendingId, seed.RequesterId)).ConfigureAwait(true);
            Assert.Equal(ErrorCodes.Conflict, rejected.ErrorCode);
            Assert.Equal(409, rejected.StatusCode);
            Assert.Contains(VoicePreviewService.PreviewStateConflictMarker, rejected.Message, StringComparison.Ordinal);

            using (TenantContext.BeginScope(seed.TenantId))
            {
                using var db = new AppDbContext(options);
                var current = await db.Set<VoicePreviewJob>()
                    .AsNoTracking()
                    .FirstOrDefaultAsync(j => j.Id == pendingId).ConfigureAwait(true);
                Assert.NotNull(current);
                Assert.Equal(VoicePreviewStatus.Cancelled, current.Status);
            }

            Assert.Single(publisher.Messages);
        }
    }

    [SkippableFact]
    public async Task Quota_Denied_Makes_No_Provider_Call()
    {
        var container = await StartContainerAsync().ConfigureAwait(true);
        await using (container.ConfigureAwait(true))
        {
            var options = CreateOptions(container);
            await MigrateAsync(options).ConfigureAwait(true);
            var seed = await SeedAsync(options).ConfigureAwait(true);
            var tts = new FakeTts();
            var publisher = new FakePublisher();

            var previewOptions = new PreviewOptions { MaxPreviewsPerDayPerTenant = 1, MaxPreviewsPerMinutePerTenant = 1000 };
            var service = CreatePreviewService(options, tts, publisher, new FakeStorage(), seed, previewOptions);
            await service.RequestPreviewAsync(
                seed.TenantId, seed.ProjectId, seed.SpeakerId, seed.StockVoiceId,
                "first", seed.RequesterId, "quota-1").ConfigureAwait(true);
            Assert.Equal(1, tts.Calls);

            var denied = await Assert.ThrowsAsync<QuotaExceededException>(() => service.RequestPreviewAsync(
                seed.TenantId, seed.ProjectId, seed.SpeakerId, seed.StockVoiceId,
                "second", seed.RequesterId, "quota-2")).ConfigureAwait(true);
            Assert.Equal(ErrorCodes.QuotaExceeded, denied.ErrorCode);
            Assert.Equal(429, denied.StatusCode);
            Assert.Contains(VoicePreviewService.PreviewQuotaExceededMarker, denied.Message, StringComparison.Ordinal);
            Assert.Equal(1, tts.Calls);
            Assert.Single(publisher.Messages);

            using (TenantContext.BeginScope(seed.TenantId))
            {
                using var db = new AppDbContext(options);
                var row = await db.Set<VoicePreviewJob>()
                    .AsNoTracking()
                    .FirstOrDefaultAsync(j => j.IdempotencyKey == "quota-2").ConfigureAwait(true);
                Assert.NotNull(row);
                Assert.Equal(VoicePreviewStatus.Failed, row.Status);
                Assert.Equal(VoicePreviewQuotaCheck.Denied, row.QuotaCheck);
                Assert.Null(row.ProviderExecutionId);
                Assert.Null(row.ArtifactId);
            }
        }
    }

    [SkippableFact]
    public async Task Per_Minute_Throttle_Denies_Bursts()
    {
        var container = await StartContainerAsync().ConfigureAwait(true);
        await using (container.ConfigureAwait(true))
        {
            var options = CreateOptions(container);
            await MigrateAsync(options).ConfigureAwait(true);
            var seed = await SeedAsync(options).ConfigureAwait(true);
            var tts = new FakeTts();

            var previewOptions = new PreviewOptions { MaxPreviewsPerDayPerTenant = 1000, MaxPreviewsPerMinutePerTenant = 1 };
            var service = CreatePreviewService(options, tts, new FakePublisher(), new FakeStorage(), seed, previewOptions);
            await service.RequestPreviewAsync(
                seed.TenantId, seed.ProjectId, seed.SpeakerId, seed.StockVoiceId,
                "first", seed.RequesterId, "throttle-1").ConfigureAwait(true);

            var denied = await Assert.ThrowsAsync<QuotaExceededException>(() => service.RequestPreviewAsync(
                seed.TenantId, seed.ProjectId, seed.SpeakerId, seed.StockVoiceId,
                "second", seed.RequesterId, "throttle-2")).ConfigureAwait(true);
            Assert.Contains(VoicePreviewService.PreviewQuotaExceededMarker, denied.Message, StringComparison.Ordinal);
            Assert.Equal(1, tts.Calls);
        }
    }

    [SkippableFact]
    public async Task Consent_Blocked_Cloning_Voice_Makes_No_Provider_Call()
    {
        var container = await StartContainerAsync().ConfigureAwait(true);
        await using (container.ConfigureAwait(true))
        {
            var options = CreateOptions(container);
            await MigrateAsync(options).ConfigureAwait(true);
            var seed = await SeedAsync(options).ConfigureAwait(true);
            var tts = new FakeTts();
            var publisher = new FakePublisher();

            var service = CreatePreviewService(options, tts, publisher, new FakeStorage(), seed);

            var blocked = await Assert.ThrowsAsync<ErrorCodeException>(() => service.RequestPreviewAsync(
                seed.TenantId, seed.ProjectId, seed.SpeakerId, seed.ClonedVoiceBId,
                "clone without consent", seed.RequesterId, "consent-blocked")).ConfigureAwait(true);
            Assert.Equal(ErrorCodes.ConsentRequired, blocked.ErrorCode);
            Assert.Equal(403, blocked.StatusCode);
            Assert.Contains(VoicePreviewService.VoiceConsentRequiredMarker, blocked.Message, StringComparison.Ordinal);
            Assert.Equal(0, tts.Calls);
            Assert.Empty(publisher.Messages);

            var stock = await service.RequestPreviewAsync(
                seed.TenantId, seed.ProjectId, seed.SpeakerId, seed.StockVoiceId,
                "stock needs no consent", seed.RequesterId, "consent-stock").ConfigureAwait(true);
            Assert.Equal(VoicePreviewStatus.Completed, stock.Job.Status);

            var consented = await service.RequestPreviewAsync(
                seed.TenantId, seed.ProjectId, seed.SpeakerId, seed.ClonedVoiceAId,
                "clone with consent", seed.RequesterId, "consent-ok").ConfigureAwait(true);
            Assert.Equal(VoicePreviewStatus.Completed, consented.Job.Status);
            Assert.Equal(VoicePreviewConsentState.Verified, consented.Job.ConsentState);
            Assert.Equal(2, tts.Calls);

            using (TenantContext.BeginScope(seed.TenantId))
            {
                using var db = new AppDbContext(options);
                var row = await db.Set<VoicePreviewJob>()
                    .AsNoTracking()
                    .FirstOrDefaultAsync(j => j.IdempotencyKey == "consent-blocked").ConfigureAwait(true);
                Assert.NotNull(row);
                Assert.Equal(VoicePreviewStatus.Failed, row.Status);
                Assert.Equal(VoicePreviewConsentState.Blocked, row.ConsentState);
                Assert.Null(row.ProviderExecutionId);
            }
        }
    }

    [SkippableFact]
    public async Task Provider_Timeout_Fails_Job_And_Allows_Retry_With_New_Key()
    {
        var container = await StartContainerAsync().ConfigureAwait(true);
        await using (container.ConfigureAwait(true))
        {
            var options = CreateOptions(container);
            await MigrateAsync(options).ConfigureAwait(true);
            var seed = await SeedAsync(options).ConfigureAwait(true);
            var tts = new FakeTts { Timeout = true };
            var publisher = new FakePublisher();

            var service = CreatePreviewService(options, tts, publisher, new FakeStorage(), seed);
            var timedOut = await Assert.ThrowsAsync<ErrorCodeException>(() => service.RequestPreviewAsync(
                seed.TenantId, seed.ProjectId, seed.SpeakerId, seed.StockVoiceId,
                "slow voice", seed.RequesterId, "timeout-1")).ConfigureAwait(true);
            Assert.Equal(ErrorCodes.ProviderTimeout, timedOut.ErrorCode);
            Assert.Contains(VoicePreviewService.PreviewProviderTimeoutMarker, timedOut.Message, StringComparison.Ordinal);
            Assert.Single(publisher.Messages);
            Assert.Equal("Failed", publisher.Messages[0].Status);
            Assert.Equal(ErrorCodes.ProviderTimeout, publisher.Messages[0].ErrorCode);

            tts.Timeout = false;
            var retry = await service.RequestPreviewAsync(
                seed.TenantId, seed.ProjectId, seed.SpeakerId, seed.StockVoiceId,
                "slow voice", seed.RequesterId, "timeout-2").ConfigureAwait(true);
            Assert.Equal(VoicePreviewStatus.Completed, retry.Job.Status);
            Assert.False(retry.IsDuplicate);

            using (TenantContext.BeginScope(seed.TenantId))
            {
                using var db = new AppDbContext(options);
                var row = await db.Set<VoicePreviewJob>()
                    .AsNoTracking()
                    .FirstOrDefaultAsync(j => j.IdempotencyKey == "timeout-1").ConfigureAwait(true);
                Assert.NotNull(row);
                Assert.Equal(VoicePreviewStatus.Failed, row.Status);
                Assert.Contains(VoicePreviewService.PreviewProviderTimeoutMarker, row.ErrorMessage ?? string.Empty, StringComparison.Ordinal);
            }
        }
    }

    [SkippableFact]
    public async Task Duplicate_Idempotency_Key_Returns_Existing_Without_Second_Call()
    {
        var container = await StartContainerAsync().ConfigureAwait(true);
        await using (container.ConfigureAwait(true))
        {
            var options = CreateOptions(container);
            await MigrateAsync(options).ConfigureAwait(true);
            var seed = await SeedAsync(options).ConfigureAwait(true);
            var tts = new FakeTts();
            var publisher = new FakePublisher();

            var service = CreatePreviewService(options, tts, publisher, new FakeStorage(), seed);
            var first = await service.RequestPreviewAsync(
                seed.TenantId, seed.ProjectId, seed.SpeakerId, seed.StockVoiceId,
                "dedup me", seed.RequesterId, "dup-key").ConfigureAwait(true);
            var second = await service.RequestPreviewAsync(
                seed.TenantId, seed.ProjectId, seed.SpeakerId, seed.StockVoiceId,
                "dedup me", seed.RequesterId, "dup-key").ConfigureAwait(true);

            Assert.False(first.IsDuplicate);
            Assert.True(second.IsDuplicate);
            Assert.Equal(first.Job.Id, second.Job.Id);
            Assert.Equal(1, tts.Calls);
            Assert.Single(publisher.Messages);
        }
    }

    [SkippableFact]
    public async Task Cross_Tenant_Job_Id_Reads_As_Not_Found()
    {
        var container = await StartContainerAsync().ConfigureAwait(true);
        await using (container.ConfigureAwait(true))
        {
            var options = CreateOptions(container);
            await MigrateAsync(options).ConfigureAwait(true);
            var seed = await SeedAsync(options).ConfigureAwait(true);

            var service = CreatePreviewService(options, new FakeTts(), new FakePublisher(), new FakeStorage(), seed);
            var created = await service.RequestPreviewAsync(
                seed.TenantId, seed.ProjectId, seed.SpeakerId, seed.StockVoiceId,
                "isolated", seed.RequesterId, "isolation-key").ConfigureAwait(true);

            var foreign = CreatePreviewService(options, new FakeTts(), new FakePublisher(), new FakeStorage(), seed);
            Assert.Null(await foreign.GetAsync(Guid.NewGuid(), created.Job.Id).ConfigureAwait(true));
            var missing = await Assert.ThrowsAsync<NotFoundException>(() => foreign.CancelAsync(
                Guid.NewGuid(), created.Job.Id, seed.RequesterId)).ConfigureAwait(true);
            Assert.Equal(404, missing.StatusCode);
        }
    }

    [SkippableFact]
    public async Task Media_Previews_Contain_All_Resolutions()
    {
        var container = await StartContainerAsync().ConfigureAwait(true);
        await using (container.ConfigureAwait(true))
        {
            var options = CreateOptions(container);
            await MigrateAsync(options).ConfigureAwait(true);
            var seed = await SeedAsync(options).ConfigureAwait(true);

            var generator = CreateGenerator(options, new FakeStorage(), new PreviewOptions { VideoPreviewEnabled = false });
            var result = await generator.GenerateForRunAsync(
                seed.TenantId, seed.ProjectId, seed.RunId, "corr-1").ConfigureAwait(true);

            Assert.False(result.PreviewDegraded);
            Assert.False(result.PeaksMissing);
            Assert.NotNull(result.MediaPreviewAudioArtifactId);
            Assert.NotNull(result.WaveformPeaksArtifactId);
            Assert.Null(result.VideoPreviewArtifactId);

            using (TenantContext.BeginScope(seed.TenantId))
            {
                using var db = new AppDbContext(options);
                var peaks = await db.Set<Artifact>()
                    .AsNoTracking()
                    .FirstOrDefaultAsync(a => a.Id == result.WaveformPeaksArtifactId!.Value).ConfigureAwait(true);
                Assert.NotNull(peaks);
                Assert.Equal(ArtifactType.WaveformPeaks, peaks.Type);
                Assert.Contains("64", peaks.MetadataJson ?? string.Empty, StringComparison.Ordinal);
                Assert.Contains("256", peaks.MetadataJson ?? string.Empty, StringComparison.Ordinal);
                Assert.Contains("1024", peaks.MetadataJson ?? string.Empty, StringComparison.Ordinal);
                Assert.Contains(seed.MediaAssetId.ToString("N"), peaks.MetadataJson ?? string.Empty, StringComparison.Ordinal);

                var audio = await db.Set<Artifact>()
                    .AsNoTracking()
                    .FirstOrDefaultAsync(a => a.Id == result.MediaPreviewAudioArtifactId!.Value).ConfigureAwait(true);
                Assert.NotNull(audio);
                Assert.Equal(ArtifactType.MediaPreviewAudio, audio.Type);
                Assert.NotEqual(audio.ContentObjectId, seed.SourceContentId);

                var contentIds = await db.Set<Artifact>()
                    .AsNoTracking()
                    .Where(a => a.ProcessingRunId == seed.RunId)
                    .Select(a => a.ContentObjectId)
                    .ToListAsync().ConfigureAwait(true);
                Assert.DoesNotContain(seed.SourceContentId, contentIds);
            }

            var rerun = await generator.GenerateForRunAsync(
                seed.TenantId, seed.ProjectId, seed.RunId, "corr-2").ConfigureAwait(true);
            Assert.Equal(result.MediaPreviewAudioArtifactId, rerun.MediaPreviewAudioArtifactId);
            Assert.Equal(result.WaveformPeaksArtifactId, rerun.WaveformPeaksArtifactId);

            var withVideo = CreateGenerator(options, new FakeStorage(), new PreviewOptions { VideoPreviewEnabled = true });
            var video = await withVideo.GenerateForRunAsync(
                seed.TenantId, seed.ProjectId, seed.RunId, "corr-3").ConfigureAwait(true);
            Assert.NotNull(video.VideoPreviewArtifactId);
            Assert.Equal(result.MediaPreviewAudioArtifactId, video.MediaPreviewAudioArtifactId);
        }
    }

    [SkippableFact]
    public async Task Media_Without_Audio_Track_Skips_Audio_And_Flags_Peaks_Missing()
    {
        var container = await StartContainerAsync().ConfigureAwait(true);
        await using (container.ConfigureAwait(true))
        {
            var options = CreateOptions(container);
            await MigrateAsync(options).ConfigureAwait(true);
            var seed = await SeedAsync(options).ConfigureAwait(true);

            var generator = CreateGenerator(options, new FakeStorage(), new PreviewOptions());
            var result = await generator.GenerateForRunAsync(
                seed.TenantId, seed.SilentProjectId, seed.RunId, "corr-silent").ConfigureAwait(true);

            Assert.False(result.PreviewDegraded);
            Assert.True(result.PeaksMissing);
            Assert.Null(result.MediaPreviewAudioArtifactId);
            Assert.NotNull(result.WaveformPeaksArtifactId);

            using (TenantContext.BeginScope(seed.TenantId))
            {
                using var db = new AppDbContext(options);
                var peaks = await db.Set<Artifact>()
                    .AsNoTracking()
                    .FirstOrDefaultAsync(a => a.Id == result.WaveformPeaksArtifactId!.Value).ConfigureAwait(true);
                Assert.NotNull(peaks);
                Assert.Contains("peaksMissing", peaks.MetadataJson ?? string.Empty, StringComparison.Ordinal);
            }
        }
    }

    [SkippableFact]
    public async Task Preview_Failure_Does_Not_Fail_Parent_Stage()
    {
        var container = await StartContainerAsync().ConfigureAwait(true);
        await using (container.ConfigureAwait(true))
        {
            var options = CreateOptions(container);
            await MigrateAsync(options).ConfigureAwait(true);
            var seed = await SeedAsync(options).ConfigureAwait(true);

            var failingStorage = new FakeStorage { ThrowOnUpload = true };
            var generator = CreateGenerator(options, failingStorage, new PreviewOptions());
            var degraded = await generator.GenerateForRunAsync(
                seed.TenantId, seed.ProjectId, seed.RunId, "corr-fail").ConfigureAwait(true);
            Assert.True(degraded.PreviewDegraded);
            Assert.Null(degraded.MediaPreviewAudioArtifactId);

            // The parent stage uses healthy storage for its own artifact while
            // the preview lane fails: analysis must still complete.
            var analysis = new MediaAnalysisService(
                new TestFactory(options),
                new ArtifactService(new TestFactory(options), new FakeStorage()),
                new FakeFfprobe(),
                Options.Create(new MediaOptions { VerifyOnUse = false }),
                Options.Create(new QuotaOptions()),
                NullLogger<MediaAnalysisService>.Instance,
                generator);
            var result = await analysis.AnalyzeAsync(
                seed.TenantId, seed.ProjectId, seed.RunId,
                seed.AnalysisExecutionId, "test-owner", "test-token").ConfigureAwait(true);

            Assert.Equal(30000, result.DurationMs);

            using (TenantContext.BeginScope(seed.TenantId))
            {
                using var db = new AppDbContext(options);
                var execution = await db.Set<StageExecution>()
                    .AsNoTracking()
                    .FirstOrDefaultAsync(e => e.Id == seed.AnalysisExecutionId).ConfigureAwait(true);
                Assert.NotNull(execution);
                Assert.Equal(StageStatus.Completed, execution.Status);

                var audit = await db.Set<AuditEvent>()
                    .AsNoTracking()
                    .FirstOrDefaultAsync(a => a.Action == MediaPreviewGenerator.AuditAction).ConfigureAwait(true);
                Assert.NotNull(audit);
                Assert.Contains("previewDegraded", audit.DetailsJson ?? string.Empty, StringComparison.Ordinal);
            }
        }
    }

    [SkippableFact]
    public async Task Qc_Evidence_Links_And_Dedupes_On_Rerun()
    {
        var container = await StartContainerAsync().ConfigureAwait(true);
        await using (container.ConfigureAwait(true))
        {
            var options = CreateOptions(container);
            await MigrateAsync(options).ConfigureAwait(true);
            var seed = await SeedAsync(options).ConfigureAwait(true);

            var linker = new QcEvidenceLinker(
                new TestFactory(options),
                new ArtifactService(new TestFactory(options), new FakeStorage()),
                new AuditService(new TestFactory(options)),
                NullLogger<QcEvidenceLinker>.Instance);

            var first = await linker.LinkAsync(
                seed.TenantId, seed.QcIssueId, "clip", 1000, 2000, "0-64").ConfigureAwait(true);
            Assert.Equal(ArtifactType.QcEvidenceArtifact, first.Type);

            var second = await linker.LinkAsync(
                seed.TenantId, seed.QcIssueId, "clip", 1000, 2000, "0-64").ConfigureAwait(true);
            Assert.Equal(first.Id, second.Id);

            var other = await linker.LinkAsync(
                seed.TenantId, seed.QcIssueId, "snapshot").ConfigureAwait(true);
            Assert.NotEqual(first.Id, other.Id);

            using (TenantContext.BeginScope(seed.TenantId))
            {
                using var db = new AppDbContext(options);
                var count = await db.Set<Artifact>()
                    .AsNoTracking()
                    .CountAsync(a => a.ProcessingRunId == seed.RunId && a.Type == ArtifactType.QcEvidenceArtifact).ConfigureAwait(true);
                Assert.Equal(2, count);

                var issue = await db.Set<QualityResult>()
                    .AsNoTracking()
                    .FirstOrDefaultAsync(q => q.Id == seed.QcIssueId).ConfigureAwait(true);
                Assert.NotNull(issue);
                Assert.NotNull(issue.ArtifactId);
                Assert.Contains("evidence", issue.DetailsJson ?? string.Empty, StringComparison.Ordinal);
                Assert.Contains("clip", issue.DetailsJson ?? string.Empty, StringComparison.Ordinal);
            }

            var foreign = new QcEvidenceLinker(
                new TestFactory(options),
                new ArtifactService(new TestFactory(options), new FakeStorage()),
                new AuditService(new TestFactory(options)),
                NullLogger<QcEvidenceLinker>.Instance);
            await Assert.ThrowsAsync<NotFoundException>(() => foreign.LinkAsync(
                Guid.NewGuid(), seed.QcIssueId, "clip")).ConfigureAwait(true);
        }
    }

    private static VoicePreviewJob NewJob(DateTimeOffset now, string? text = null)
    {
        return new VoicePreviewJob(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            "voice-1", text ?? "hello preview", VoicePreviewStatus.Pending, Guid.NewGuid(), null,
            VoicePreviewQuotaCheck.Allowed, null, VoicePreviewConsentState.Verified,
            null, null, null, null, now, null, null);
    }

    private VoicePreviewService CreatePreviewService(
        DbContextOptions<AppDbContext> options,
        FakeTts tts,
        FakePublisher publisher,
        FakeStorage storage,
        SeedData seed,
        PreviewOptions? previewOptions = null)
    {
        var factory = new TestFactory(options);
        return new VoicePreviewService(
            factory,
            tts,
            new ProviderExecutionRecorder(factory),
            new ArtifactService(factory, storage),
            new AuditService(factory),
            publisher,
            Options.Create(previewOptions ?? new PreviewOptions()),
            Options.Create(new VoiceOptions { CloningEnabled = true, DefaultProvider = "mock" }),
            NullLogger<VoicePreviewService>.Instance);
    }

    private static MediaPreviewGenerator CreateGenerator(
        DbContextOptions<AppDbContext> options,
        FakeStorage storage,
        PreviewOptions previewOptions)
    {
        var factory = new TestFactory(options);
        return new MediaPreviewGenerator(
            factory,
            new ArtifactService(factory, storage),
            new AuditService(factory),
            Options.Create(previewOptions),
            NullLogger<MediaPreviewGenerator>.Instance);
    }

    private sealed record SeedData(
        Guid TenantId,
        Guid ProjectId,
        Guid SilentProjectId,
        Guid OwnerId,
        Guid RequesterId,
        Guid RunId,
        Guid SpeakerId,
        string StockVoiceId,
        string ClonedVoiceAId,
        string ClonedVoiceBId,
        Guid MediaAssetId,
        Guid SourceContentId,
        Guid AnalysisExecutionId,
        Guid QcIssueId);

    private static async Task<SeedData> SeedAsync(DbContextOptions<AppDbContext> options)
    {
        var now = DateTimeOffset.UtcNow;
        var tenantId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var silentProjectId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        var requesterId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var speakerId = Guid.NewGuid();
        var stockVoiceId = Guid.NewGuid();
        var clonedVoiceAId = Guid.NewGuid();
        var clonedVoiceBId = Guid.NewGuid();
        var mediaAssetId = Guid.NewGuid();
        var silentAssetId = Guid.NewGuid();
        var sourceContentId = Guid.NewGuid();
        var silentContentId = Guid.NewGuid();
        var executionId = Guid.NewGuid();
        var qcIssueId = Guid.NewGuid();
        var contentHash = new string('a', 64);
        var silentHash = new string('b', 64);

        using (TenantContext.BeginScope(tenantId))
        {
            using var db = new AppDbContext(options);
            db.Tenants.Add(new Tenant(tenantId, "Preview", $"preview-{tenantId:N}", now));
            db.DubbingProjects.Add(new DubbingProject(
                projectId, tenantId, "en", "de", ProjectStatus.Created,
                "{}", new string('c', 64), mediaAssetId, null, now, now,
                "Preview project", null, ownerId));
            db.DubbingProjects.Add(new DubbingProject(
                silentProjectId, tenantId, "en", "de", ProjectStatus.Created,
                "{}", new string('d', 64), silentAssetId, null, now, now,
                "Silent project", null, ownerId));
            db.Set<TenantUser>().Add(new TenantUser(
                ownerId, tenantId, "sub-owner", "owner@example.com", "Owner",
                TenantUserStatus.Active, now, now));
            db.Set<TenantUser>().Add(new TenantUser(
                requesterId, tenantId, "sub-requester", "requester@example.com", "Requester",
                TenantUserStatus.Active, now, now));
            db.Set<ProjectMembership>().Add(new ProjectMembership(
                Guid.NewGuid(), tenantId, projectId, requesterId, ProjectRole.ProjectEditor, ownerId, now));
            db.Set<ProcessingRun>().Add(new ProcessingRun(
                runId, tenantId, projectId, 0, ProcessingRunStatus.Running, "v1",
                new string('e', 64), new string('f', 64), new string('a', 64),
                now, now, now, null));
            db.Set<Speaker>().Add(new Speaker(
                speakerId, tenantId, projectId, "spk-1", "Speaker One",
                0, 5000, "manual", "v1", 1.0, null, now));
            db.Set<VoiceProfile>().Add(new VoiceProfile(
                stockVoiceId, tenantId, "mock", "stock-1", "v1", "de",
                VoiceType.Stock, false, null, now));
            db.Set<VoiceProfile>().Add(new VoiceProfile(
                clonedVoiceAId, tenantId, "mock", "cloned-a", "v1", "de",
                VoiceType.Cloned, true, null, now));
            db.Set<VoiceProfile>().Add(new VoiceProfile(
                clonedVoiceBId, tenantId, "mock", "cloned-b", "v1", "de",
                VoiceType.Cloned, true, null, now));
            db.Set<ConsentRecord>().Add(new ConsentRecord(
                Guid.NewGuid(), tenantId, "subject-a", "evidence-a", "*",
                "EU", ConsentStatus.Granted, clonedVoiceAId, now, null));
            db.Set<ContentObject>().Add(new ContentObject(
                sourceContentId, tenantId, contentHash, new string('c', 64),
                1024, "video/mp4", "keys/source.mp4", ContentObjectStatus.Committed, now, now));
            db.Set<ContentObject>().Add(new ContentObject(
                silentContentId, tenantId, silentHash, new string('d', 64),
                512, "video/mp4", "keys/silent.mp4", ContentObjectStatus.Committed, now, now));
            db.Set<MediaAsset>().Add(new MediaAsset(
                mediaAssetId, tenantId, projectId, sourceContentId, "source.mp4", "mp4",
                "aac", "h264", 1024, 30000, 44100, 2, "stereo",
                MediaAssetStatus.Valid, null, contentHash, now));
            db.Set<MediaAsset>().Add(new MediaAsset(
                silentAssetId, tenantId, silentProjectId, silentContentId, "silent.mp4", "mp4",
                "none", null, 512, 10000, 8000, 1, "mono",
                MediaAssetStatus.Valid, null, silentHash, now));
            db.Set<StageExecution>().Add(new StageExecution(
                executionId, tenantId, projectId, runId,
                StageType.MediaAnalysis, ScopeType.Run, runId.ToString("N"), null, 0,
                StageStatus.Running, "test-owner", "test-token", 1,
                now.AddHours(1), now, null, "input",
                new string('e', 64), new string('f', 64), null, null, null, now, now));
            db.Set<QualityResult>().Add(new QualityResult(
                qcIssueId, tenantId, projectId, runId, ScopeType.Run, runId.ToString("N"), null,
                QualityStatus.Blocked, "QC_AUDIO_GAP", "high", "Audio gap detected.",
                null, null, now));
            await db.SaveChangesAsync().ConfigureAwait(true);
        }

        return new SeedData(
            tenantId, projectId, silentProjectId, ownerId, requesterId, runId, speakerId,
            "stock-1", "cloned-a", "cloned-b", mediaAssetId, sourceContentId,
            executionId, qcIssueId);
    }

    private static async Task MigrateAsync(DbContextOptions<AppDbContext> options)
    {
        using (TenantContext.BeginMaintenanceScope())
        {
            using var db = new AppDbContext(options);
            await db.Database.MigrateAsync().ConfigureAwait(true);
        }
    }

    private static DbContextOptions<AppDbContext> CreateOptions(PostgreSqlContainer container)
    {
        return new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(container.GetConnectionString())
            .UseSnakeCaseNamingConvention()
            .AddInterceptors(new TenantSessionInterceptor())
            .Options;
    }

    private async Task<PostgreSqlContainer> StartContainerAsync()
    {
        try
        {
            var container = new PostgreSqlBuilder("postgres:16-alpine").Build();
            await container.StartAsync().ConfigureAwait(true);
            return container;
        }
#pragma warning disable CA1031 // Docker availability probe: any transport-level failure means "skip", domain failures surface later.
        catch (Exception ex) when (IsInfrastructureUnavailable(ex))
#pragma warning restore CA1031
        {
            _output.WriteLine($"Docker/PostgreSQL unavailable, skipping preview test: {ex.Message}");
            Skip.If(true, $"Docker/PostgreSQL unavailable: {ex.Message}");
            throw new InvalidOperationException("Unreachable: Skip.If always throws.", ex);
        }
    }

    private static bool IsInfrastructureUnavailable(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            var name = current.GetType().FullName ?? string.Empty;
            if (name.Contains("Docker", StringComparison.Ordinal) ||
                name.Contains("Testcontainers", StringComparison.Ordinal))
            {
                return true;
            }

            if (current is HttpRequestException
                or TimeoutException
                or ObjectDisposedException
                or UnauthorizedAccessException
                or IOException
                or SocketException
                or DbException
                or InvalidOperationException)
            {
                return true;
            }
        }

        return false;
    }

    private sealed class FakeTts : ITtsProvider
    {
        private int _calls;

        public int Calls => _calls;

        public bool Timeout { get; set; }

        public Task<TtsResponse> SynthesizeAsync(TtsRequest request, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);
            Interlocked.Increment(ref _calls);
            if (Timeout)
            {
                throw new TimeoutException("Preview provider timed out.");
            }

            var durationMs = Math.Clamp(500 + (request.Text.Length * 10), 500, 5000);
            return Task.FromResult(new TtsResponse(
                $"mock-content-{_calls}",
                durationMs,
                request.VoiceId,
                0.95,
                "mock-1",
                "1",
                "mock",
                new ProviderUsage(request.Text.Length / 4, durationMs * 32, durationMs / 1000.0, 0),
                null));
        }
    }

    private sealed class FakePublisher : IVoicePreviewEventPublisher
    {
        private readonly object _gate = new();

        public List<VoicePreviewCompleted> Messages { get; } = [];

        public Task PublishAsync(VoicePreviewCompleted message, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(message);
            lock (_gate)
            {
                Messages.Add(message);
            }

            return Task.CompletedTask;
        }
    }

    private sealed class FakeStorage : IArtifactStorage
    {
        private readonly Dictionary<string, byte[]> _blobs = new(StringComparer.Ordinal);

        public bool ThrowOnUpload { get; set; }

        public Task UploadAsync(Stream content, string storageKey, string contentType, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(content);
            ArgumentException.ThrowIfNullOrWhiteSpace(storageKey);
            if (ThrowOnUpload)
            {
                throw new IOException("Preview storage unavailable.");
            }

            using var buffer = new MemoryStream();
            content.CopyTo(buffer);
            lock (_blobs)
            {
                _blobs[storageKey] = buffer.ToArray();
            }

            return Task.CompletedTask;
        }

        public Task<Stream> DownloadAsync(string storageKey, CancellationToken ct)
        {
            lock (_blobs)
            {
                if (_blobs.TryGetValue(storageKey, out var bytes))
                {
                    return Task.FromResult<Stream>(new MemoryStream(bytes, writable: false));
                }
            }

            throw new FileNotFoundException($"Blob '{storageKey}' was not found.");
        }

        public Task<bool> ExistsAsync(string storageKey, CancellationToken ct)
        {
            lock (_blobs)
            {
                return Task.FromResult(_blobs.ContainsKey(storageKey));
            }
        }

        public Task DeleteAsync(string storageKey, CancellationToken ct)
        {
            lock (_blobs)
            {
                _blobs.Remove(storageKey);
            }

            return Task.CompletedTask;
        }

        public Task<string> GetPresignedDownloadUrlAsync(string storageKey, TimeSpan expiry, CancellationToken ct)
        {
            return Task.FromResult($"https://preview.test/{storageKey}");
        }

        public Task<string> GetPresignedUploadUrlAsync(string storageKey, TimeSpan expiry, CancellationToken ct)
        {
            return Task.FromResult($"https://preview.test/{storageKey}");
        }

        public Task<string?> GetStorageChecksumAsync(string storageKey, CancellationToken ct)
        {
            return Task.FromResult<string?>(null);
        }
    }

    private sealed class FakeFfprobe : IFFprobeService
    {
        public Task<FfprobeResult> ProbeAsync(string storageKeyOrLocalPath, CancellationToken cancellationToken)
        {
            return Task.FromResult(new FfprobeResult(
                "mp4",
                30000,
                [new FfprobeStream("audio", "aac", null, null, null, 44100, 2, "stereo")]));
        }
    }

    private sealed class TestFactory : IStageExecutionContextFactory
    {
        private readonly DbContextOptions<AppDbContext> _options;

        public TestFactory(DbContextOptions<AppDbContext> options)
        {
            _options = options;
        }

        public DbContext CreateDbContext()
        {
            return new AppDbContext(_options);
        }
    }
}
