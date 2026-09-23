using DubbingPlatform.Application.Errors;
using DubbingPlatform.Application.Voices;
using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Domain.Enums;

namespace DubbingPlatform.UnitTests.Voices;

/// <summary>
/// Hermetic Task 010 coverage: new error codes, compatibility rule set, and
/// reason sanitization. Docker-backed <c>VoiceApiTests</c> replays the HTTP
/// paths in CI.
/// </summary>
public sealed class VoiceCompatibilityTests
{
    [Fact]
    public void New_Voice_Codes_Map()
    {
        Assert.Equal(422, ErrorCodes.StatusFor(ErrorCodes.VoiceIncompatible));
        Assert.Equal(403, ErrorCodes.StatusFor(ErrorCodes.VoiceConsentRequired));
        Assert.Equal(429, ErrorCodes.StatusFor(ErrorCodes.PreviewQuotaExceeded));
        Assert.Equal(404, ErrorCodes.StatusFor(ErrorCodes.VoiceNotFound));
        Assert.Equal(400, ErrorCodes.StatusFor(ErrorCodes.PreviewTextInvalid));
        Assert.Equal(57, ErrorCodes.All.Length);
    }

    [Fact]
    public void Language_Match_Required()
    {
        var project = Project("es");
        var ok = Voice("mock", "v-es", "es", VoiceType.Stock, false, null);
        Assert.Empty(VoiceCompatibility.Check(ok, project, true, true));

        var wrong = Voice("mock", "v-fr", "fr", VoiceType.Stock, false, null);
        var reasons = VoiceCompatibility.Check(wrong, project, true, true);
        Assert.Single(reasons);
        Assert.Contains("LANGUAGE_MISMATCH", reasons[0], StringComparison.Ordinal);
    }

    [Fact]
    public void SampleRate_And_Channel_Floor()
    {
        var project = Project("es");
        var lowRate = Voice("mock", "v-low", "es", VoiceType.Stock, false,
            """{"sampleRateHz":8000,"channels":1}""");
        Assert.Contains(
            VoiceCompatibility.Check(lowRate, project, true, true),
            r => r.Contains("SAMPLE_RATE_BELOW_FLOOR", StringComparison.Ordinal));

        var okRate = Voice("mock", "v-ok", "es", VoiceType.Stock, false,
            """{"sampleRateHz":48000,"channels":2}""");
        Assert.Empty(VoiceCompatibility.Check(okRate, project, true, true));

        var absent = Voice("mock", "v-absent", "es", VoiceType.Stock, false, null);
        Assert.Empty(VoiceCompatibility.Check(absent, project, true, true));
    }

    [Fact]
    public void Cloning_Requires_Consent_And_KillSwitch()
    {
        var project = Project("es");
        var cloned = Voice("mock", "cloned-x", "es", VoiceType.Cloned, true, null);

        var noConsent = VoiceCompatibility.Check(cloned, project, false, true);
        Assert.Contains(noConsent, r => r.Contains("CONSENT_REQUIRED", StringComparison.Ordinal));

        var killed = VoiceCompatibility.Check(cloned, project, true, false);
        Assert.Contains(killed, r => r.Contains("CLONING_DISABLED", StringComparison.Ordinal));

        Assert.Empty(VoiceCompatibility.Check(cloned, project, true, true));
    }

    [Fact]
    public void Style_Allowlist_And_Gender()
    {
        var project = Project("es", """{"allowedVoiceStyles":["warm"],"allowedGenders":["feminine"]}""");
        var allowed = Voice("mock", "v-a", "es", VoiceType.Stock, false,
            """{"styleTags":["warm"],"gender":"feminine"}""");
        Assert.Empty(VoiceCompatibility.Check(allowed, project, true, true));

        var wrongStyle = Voice("mock", "v-b", "es", VoiceType.Stock, false,
            """{"styleTags":["cold"],"gender":"feminine"}""");
        Assert.Contains(
            VoiceCompatibility.Check(wrongStyle, project, true, true),
            r => r.Contains("STYLE_NOT_ALLOWED", StringComparison.Ordinal));

        var wrongGender = Voice("mock", "v-c", "es", VoiceType.Stock, false,
            """{"styleTags":["warm"],"gender":"masculine"}""");
        Assert.Contains(
            VoiceCompatibility.Check(wrongGender, project, true, true),
            r => r.Contains("GENDER_NOT_ALLOWED", StringComparison.Ordinal));

        var untagged = Voice("mock", "v-d", "es", VoiceType.Stock, false, null);
        Assert.Empty(VoiceCompatibility.Check(untagged, project, true, true));
    }

    [Fact]
    public void Incompatible_Exception_Carries_Reasons()
    {
        var ex = new VoiceIncompatibleException("v-x", ["LANGUAGE_MISMATCH: nope"]);
        Assert.Equal(ErrorCodes.VoiceIncompatible, ex.ErrorCode);
        Assert.Equal(422, ex.StatusCode);
        var details = ex.GetErrorDetails();
        Assert.Equal("v-x", details["voiceId"]);
    }

    [Fact]
    public void Reason_Sanitized()
    {
        Assert.Null(SpeakerVoiceService.SanitizeReason(null));
        Assert.Null(SpeakerVoiceService.SanitizeReason("   "));
        Assert.Null(SpeakerVoiceService.SanitizeReason("<br/>"));
        Assert.Equal("hello", SpeakerVoiceService.SanitizeReason("<b>hello</b>"));
    }

    private static DubbingProject Project(string targetLanguage, string? settingsJson = null)
    {
        var now = DateTimeOffset.UtcNow;
        return new DubbingProject(
            Guid.NewGuid(), Guid.NewGuid(), "en", targetLanguage,
            ProjectStatus.Created, settingsJson ?? "{}", new string('a', 64),
            null, null, now, now);
    }

    private static VoiceProfile Voice(
        string provider, string voiceId, string language, VoiceType type, bool cloning, string? modelRef)
    {
        return new VoiceProfile(
            Guid.NewGuid(), Guid.NewGuid(), provider, voiceId, "1",
            language, type, cloning, modelRef, DateTimeOffset.UtcNow);
    }
}
