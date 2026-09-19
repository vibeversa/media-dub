using DubbingPlatform.Application.Errors;
using DubbingPlatform.Application.Exceptions;
using DubbingPlatform.Application.Options;
using DubbingPlatform.Application.Services;
using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Domain.Enums;

namespace DubbingPlatform.UnitTests.Pipeline;

/// <summary>
/// Hermetic voice-assignment tests (no Docker): stable deterministic mapping,
/// cloned-consent rejection, revocation blocking, inventory incompatibility,
/// and override precedence use the pure <see cref="VoiceAssignmentService"/>
/// and <see cref="ConsentService"/> planners directly. DB persistence lives in
/// the service/worker (PG, covered in CI).
/// </summary>
public sealed class VoiceAssignmentTests
{
    private static IReadOnlyList<VoiceCandidate> StockInventory(string language = "es")
    {
        return
        [
            new VoiceCandidate("mock", "mock-voice-1", language, VoiceType.Stock, "1"),
            new VoiceCandidate("mock", "mock-voice-2", language, VoiceType.Stock, "1"),
            new VoiceCandidate("mock", "mock-voice-3", language, VoiceType.Stock, "1"),
        ];
    }

    private static IReadOnlyList<VoiceCandidate> ClonedInventory(string language = "es")
    {
        return
        [
            new VoiceCandidate("mock", "cloned-voice-1", language, VoiceType.Cloned, "1"),
            new VoiceCandidate("mock", "mock-voice-1", language, VoiceType.Stock, "1"),
        ];
    }

    private static ConsentRecord GrantedConsent(Guid tenantId, Guid projectId, Guid? voiceProfileId = null)
    {
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        return new ConsentRecord(
            Guid.NewGuid(), tenantId, "subject-1", "evidence-1",
            string.Concat("project:", projectId.ToString("N")), "EU",
            ConsentStatus.Granted, voiceProfileId, now, null);
    }

    [Fact]
    public void Stable_Mapping()
    {
        var inventory = StockInventory();
        var first = VoiceAssignmentService.SelectCandidate("proj:speaker-a", inventory, "es");
        var repeat = VoiceAssignmentService.SelectCandidate("proj:speaker-a", inventory, "es");

        Assert.Equal(first.VoiceId, repeat.VoiceId);
        Assert.Equal(first.Provider, repeat.Provider);

        // Shuffled input still yields the same pick (sorted voiceIds).
        var shuffled = inventory.AsEnumerable().Reverse().ToList();
        var resorted = VoiceAssignmentService.SelectCandidate("proj:speaker-a", shuffled, "es");
        Assert.Equal(first.VoiceId, resorted.VoiceId);

        // Deterministic profile ids are stable per (tenant, provider, voice, lang).
        var tenantId = Guid.NewGuid();
        var one = VoiceAssignmentService.VoiceProfileIdFor(tenantId, "mock", first.VoiceId, "es");
        var two = VoiceAssignmentService.VoiceProfileIdFor(tenantId, "MOCK", first.VoiceId, "ES");
        Assert.Equal(one, two);

        var options = new VoiceOptions();
        Assert.False(options.CloningEnabled);
        Assert.Equal("mock", options.DefaultProvider);
        Assert.True(new VoiceOptionsValidator().Validate(null, options).Succeeded);
        Assert.True(new VoiceOptionsValidator().Validate(null, new VoiceOptions { DefaultProvider = " " }).Failed);
    }

    [Fact]
    public void Cloning_Without_Consent_Rejected()
    {
        var projectId = Guid.NewGuid();
        var cloned = new VoiceCandidate("mock", "cloned-voice-1", "es", VoiceType.Cloned, "1");

        // Cloning kill-switch off rejects even with a granted consent.
        var consent = GrantedConsent(Guid.NewGuid(), projectId);
        var disabled = Assert.Throws<ErrorCodeException>(() =>
            VoiceAssignmentService.ValidateClonedUse(cloned, false, consent, projectId, null, null));
        Assert.Equal(ErrorCodes.ConsentRequired, disabled.ErrorCode);

        // Cloning on but no consent rejects.
        var missing = Assert.Throws<ErrorCodeException>(() =>
            VoiceAssignmentService.ValidateClonedUse(cloned, true, null, projectId, null, null));
        Assert.Equal(ErrorCodes.ConsentRequired, missing.ErrorCode);

        // Stock voices never require consent.
        var stock = new VoiceCandidate("mock", "mock-voice-1", "es", VoiceType.Stock, "1");
        VoiceAssignmentService.ValidateClonedUse(stock, false, null, projectId, null, null);
    }

    [Fact]
    public void Revocation_Blocks()
    {
        var tenantId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var cloned = new VoiceCandidate("mock", "cloned-voice-1", "es", VoiceType.Cloned, "1");
        var profileId = VoiceAssignmentService.VoiceProfileIdFor(tenantId, "mock", "cloned-voice-1", "es");

        var revoked = GrantedConsent(tenantId, projectId, profileId);
        revoked.Revoke(revoked.GrantedAt.AddHours(1));
        Assert.Equal(ConsentStatus.Revoked, revoked.Status);
        Assert.NotNull(revoked.RevokedAt);

        var ex = Assert.Throws<ErrorCodeException>(() =>
            VoiceAssignmentService.ValidateClonedUse(cloned, true, revoked, projectId, profileId, null));
        Assert.Equal(ErrorCodes.ConsentRequired, ex.ErrorCode);

        // Revoke is idempotent at the domain level.
        revoked.Revoke(revoked.RevokedAt!.Value);
        Assert.Equal(ConsentStatus.Revoked, revoked.Status);

        // A granted consent for the same voice still passes.
        var granted = GrantedConsent(tenantId, projectId, profileId);
        VoiceAssignmentService.ValidateClonedUse(cloned, true, granted, projectId, profileId, null);

        // Scope that does not cover the project blocks.
        var otherProject = GrantedConsent(tenantId, Guid.NewGuid(), profileId);
        var scoped = Assert.Throws<ErrorCodeException>(() =>
            VoiceAssignmentService.ValidateClonedUse(cloned, true, otherProject, projectId, profileId, null));
        Assert.Equal(ErrorCodes.ConsentRequired, scoped.ErrorCode);
    }

    [Fact]
    public void Incompatibility_Fails()
    {
        // Empty inventory for the language fails fast.
        var empty = Assert.Throws<ErrorCodeException>(() =>
            VoiceAssignmentService.SelectCandidate("proj:speaker-a", [], "es"));
        Assert.Equal(ErrorCodes.ProviderConfigurationError, empty.ErrorCode);

        // Inventory for another language does not satisfy the target.
        var german = StockInventory("de");
        var mismatch = Assert.Throws<ErrorCodeException>(() =>
            VoiceAssignmentService.SelectCandidate("proj:speaker-a", german, "es"));
        Assert.Equal(ErrorCodes.ProviderConfigurationError, mismatch.ErrorCode);

        // Unknown override voice fails fast instead of silently picking.
        var inventory = StockInventory();
        var overrides = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["proj:speaker-a"] = "voice-that-does-not-exist",
        };
        var unknown = Assert.Throws<ErrorCodeException>(() =>
            VoiceAssignmentService.ResolveVoice("proj:speaker-a", overrides, inventory, "es", "mock"));
        Assert.Equal(ErrorCodes.ProviderConfigurationError, unknown.ErrorCode);

        // Jurisdiction mismatch surfaces as POLICY_DENIED (not consent-required).
        var tenantId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var cloned = new VoiceCandidate("mock", "cloned-voice-1", "es", VoiceType.Cloned, "1");
        var now = DateTimeOffset.UtcNow;
        var policy = new ProcessingPolicy(
            Guid.NewGuid(), tenantId, true, ["mock"], "EU",
            "allow", "allow", true, null, now, now);
        var consent = new ConsentRecord(
            Guid.NewGuid(), tenantId, "subject-1", "evidence-1",
            string.Concat("project:", projectId.ToString("N")), "US",
            ConsentStatus.Granted, null, now, null);
        var denied = Assert.Throws<ErrorCodeException>(() =>
            VoiceAssignmentService.ValidateClonedUse(cloned, true, consent, projectId, null, policy));
        Assert.Equal(ErrorCodes.PolicyDenied, denied.ErrorCode);
    }

    [Fact]
    public void Override_Respected()
    {
        var inventory = ClonedInventory();
        var overrides = VoiceAssignmentService.ParseVoiceOverrides(
            """{"voiceOverrides": {"proj:speaker-a": "mock-voice-1"}}""");

        Assert.True(overrides.TryGetValue("proj:speaker-a", out var wanted));
        Assert.Equal("mock-voice-1", wanted);

        var (candidate, reason) = VoiceAssignmentService.ResolveVoice(
            "proj:speaker-a", overrides, inventory, "es", "mock");

        Assert.Equal("mock-voice-1", candidate.VoiceId);
        Assert.Equal(VoiceAssignmentService.ReasonOverride, reason);

        // No override falls back to the deterministic pick.
        var (picked, pickedReason) = VoiceAssignmentService.ResolveVoice(
            "proj:speaker-a",
            new Dictionary<string, string>(StringComparer.Ordinal),
            inventory, "es", "mock");
        Assert.Equal(VoiceAssignmentService.ReasonDeterministic, pickedReason);
        Assert.Contains(picked.VoiceId, new[] { "cloned-voice-1", "mock-voice-1" }, StringComparer.Ordinal);

        // Invalid settings fail closed to empty (no override applied).
        Assert.Empty(VoiceAssignmentService.ParseVoiceOverrides("{ invalid json"));
        Assert.Empty(VoiceAssignmentService.ParseVoiceOverrides(null));

        var reasons = new[]
        {
            VoiceAssignmentService.ReasonOverride,
            VoiceAssignmentService.ReasonDeterministic,
            VoiceAssignmentService.ReasonClonedConsented,
        };
        Assert.Equal(3, reasons.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal("cloned-", VoiceAssignmentService.ClonedVoicePrefix);
        Assert.Equal(VoiceType.Cloned, VoiceAssignmentService.InferVoiceType("cloned-aria"));
        Assert.Equal(VoiceType.Stock, VoiceAssignmentService.InferVoiceType("mock-voice-1"));
    }
}
