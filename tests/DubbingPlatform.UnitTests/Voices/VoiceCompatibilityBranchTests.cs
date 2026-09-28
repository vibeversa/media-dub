// Task 039C: remaining branch closure for the voice-compatibility rule set
// and the selection-version conflict envelope.
//
// Complements (never edits) `Voices/VoiceCompatibilityTests.cs` and
// `Segments/SegmentApiExceptionsTests.cs`: those pin the rule outcomes and the
// public messages, this file pins the JSON-coercion legs of
// `VoiceCompatibility` (alias keys, string-encoded numbers, non-object roots,
// malformed JSON, blank strings, wrong value kinds, reason truncation) and the
// partially-populated `currentVersionIds` payload a client receives when only
// one of the two version ids is known.
//
// Pure: no HTTP, no database, no clock. No secrets in fixtures.
using DubbingPlatform.Application.Errors;
using DubbingPlatform.Application.Segments;
using DubbingPlatform.Application.Voices;
using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Domain.Enums;

namespace DubbingPlatform.UnitTests.Voices;

/// <summary>
/// Branch-completion matrix for <c>VoiceCompatibility</c>'s defensive JSON
/// readers. Every rule in the production file fails *closed* to "no constraint"
/// for malformed input, so these cases pin that a malformed model ref or
/// settings blob never makes a voice incompatible on its own — the documented
/// backward-compatibility contract for rows written before instrumentation.
/// </summary>
public sealed class VoiceCompatibilityBranchTests
{
    private const string Spanish = "es";

    [Theory]
    // `sampleRateHz` / `sampleRate` / `sample_rate` are accepted aliases.
    [InlineData("""{"sampleRateHz":8000}""", "SAMPLE_RATE_BELOW_FLOOR")]
    [InlineData("""{"sampleRate":8000}""", "SAMPLE_RATE_BELOW_FLOOR")]
    [InlineData("""{"sample_rate":8000}""", "SAMPLE_RATE_BELOW_FLOOR")]
    // A JSON *string* holding a number is coerced, not ignored.
    [InlineData("""{"sampleRateHz":"8000"}""", "SAMPLE_RATE_BELOW_FLOOR")]
    [InlineData("""{"sampleRateHz":" 8000 "}""", "SAMPLE_RATE_BELOW_FLOOR")]
    // `channels` / `channelCount` / `channel_count` are accepted aliases.
    [InlineData("""{"channels":0}""", "CHANNELS_BELOW_FLOOR")]
    [InlineData("""{"channelCount":0}""", "CHANNELS_BELOW_FLOOR")]
    [InlineData("""{"channel_count":0}""", "CHANNELS_BELOW_FLOOR")]
    [InlineData("""{"channels":"0"}""", "CHANNELS_BELOW_FLOOR")]
    public void Model_Ref_Number_Aliases_And_String_Encodings_Are_Honoured(string modelRef, string expectedReason)
    {
        var reasons = VoiceCompatibility.Check(
            Voice(modelRef, VoiceType.Stock, cloningEnabled: false),
            Project(Spanish),
            hasCoveringConsent: true,
            cloningGloballyEnabled: true);

        Assert.Contains(reasons, r => r.Contains(expectedReason, StringComparison.Ordinal));
    }

    [Theory]
    // Values that cannot be read as a number fail open: the floor is simply not
    // enforced, so the voice is NOT excluded. This is the backward-compat leg.
    [InlineData("""{"sampleRateHz":"not-a-number"}""")]
    [InlineData("""{"sampleRateHz":null}""")]
    [InlineData("""{"sampleRateHz":true}""")]
    [InlineData("""{"sampleRateHz":{}}""")]
    [InlineData("""{"sampleRateHz":[48000]}""")]
    [InlineData("""{"sampleRateHz":48000.5}""")]
    [InlineData("""{"channels":"nope"}""")]
    [InlineData("""{"channelCount":[1]}""")]
    public void Unreadable_Numbers_Fail_Open_Rather_Than_Excluding_The_Voice(string modelRef)
    {
        var reasons = VoiceCompatibility.Check(
            Voice(modelRef, VoiceType.Stock, cloningEnabled: false),
            Project(Spanish),
            hasCoveringConsent: true,
            cloningGloballyEnabled: true);

        Assert.DoesNotContain(reasons, r => r.Contains("BELOW_FLOOR", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    // A JSON root that is not an object carries no model attributes.
    [InlineData("[]")]
    [InlineData("42")]
    [InlineData("\"a string\"")]
    [InlineData("null")]
    [InlineData("true")]
    // Malformed JSON must not throw; it degrades to "no constraints".
    [InlineData("{not json")]
    [InlineData("{\"sampleRateHz\":")]
    public void Unparseable_Model_Ref_Never_Excludes_And_Never_Throws(string? modelRef)
    {
        var reasons = VoiceCompatibility.Check(
            Voice(modelRef, VoiceType.Stock, cloningEnabled: false),
            Project(Spanish),
            hasCoveringConsent: true,
            cloningGloballyEnabled: true);

        Assert.Empty(reasons);
    }

    [Theory]
    // `DubbingProject.Validate` rejects a blank `SettingsJson` outright, so the
    // only unreadable settings a real row can carry are non-blank values that
    // are not a JSON object, or malformed JSON. Both must degrade to "no
    // project constraints" rather than throwing.
    [InlineData("[]")]
    [InlineData("7")]
    [InlineData("\"a string\"")]
    [InlineData("true")]
    [InlineData("oops")]
    [InlineData("{\"voiceSampleRateFloor\":")]
    public void Unparseable_Project_Settings_Fail_Closed_To_No_Constraints(string settingsJson)
    {
        // The model ref still enforces the default floor when the project
        // settings cannot be read, because the floor is a server constant.
        var lowRate = Voice("""{"sampleRateHz":8000}""", VoiceType.Stock, cloningEnabled: false);
        var reasons = VoiceCompatibility.Check(lowRate, Project(Spanish, settingsJson), true, true);

        Assert.Contains(reasons, r => r.Contains("SAMPLE_RATE_BELOW_FLOOR", StringComparison.Ordinal));
        // ...and a compliant voice stays selectable.
        Assert.Empty(VoiceCompatibility.Check(
            Voice("""{"sampleRateHz":48000}""", VoiceType.Stock, cloningEnabled: false),
            Project(Spanish, settingsJson),
            true,
            true));
    }

    [Theory]
    [InlineData("""{"voiceSampleRateFloor":24000}""", """{"sampleRateHz":16000}""", "SAMPLE_RATE_BELOW_FLOOR")]
    [InlineData("""{"voiceChannelsFloor":2}""", """{"channels":1}""", "CHANNELS_BELOW_FLOOR")]
    // A raised floor must not exclude a voice that meets the *default* floor.
    [InlineData("""{"voiceSampleRateFloor":24000}""", """{"sampleRateHz":48000}""", "")]
    [InlineData("""{"voiceChannelsFloor":2}""", """{"channels":2}""", "")]
    public void Project_Settings_Raise_The_Floors(string settingsJson, string modelRef, string expectedReason)
    {
        var reasons = VoiceCompatibility.Check(
            Voice(modelRef, VoiceType.Stock, cloningEnabled: false),
            Project(Spanish, settingsJson),
            true,
            true);

        if (expectedReason.Length == 0)
        {
            Assert.Empty(reasons);
        }
        else
        {
            Assert.Contains(reasons, r => r.Contains(expectedReason, StringComparison.Ordinal));
        }
    }

    [Theory]
    // `allowedVoiceStyles` / `voiceStyles` / `styleAllowlist` are aliases; the
    // voice side reads `styleTags` / `styles` / `tags`.
    [InlineData("""{"allowedVoiceStyles":["warm"]}""", """{"styleTags":["warm"]}""", "")]
    [InlineData("""{"voiceStyles":["warm"]}""", """{"styles":["warm"]}""", "")]
    [InlineData("""{"styleAllowlist":["warm"]}""", """{"tags":["warm"]}""", "")]
    [InlineData("""{"allowedVoiceStyles":["warm"]}""", """{"styleTags":["cold"]}""", "STYLE_NOT_ALLOWED")]
    // A single string is treated as a one-element allowlist / tag list.
    [InlineData("""{"allowedVoiceStyles":"warm"}""", """{"styleTags":"warm"}""", "")]
    // A voice with no tags is universal and always passes an allowlist.
    [InlineData("""{"allowedVoiceStyles":["warm"]}""", """{"sampleRateHz":48000}""", "")]
    // An empty allowlist imposes no constraint.
    [InlineData("""{"allowedVoiceStyles":[]}""", """{"styleTags":["cold"]}""", "")]
    // A non-string / non-array allowlist is unreadable and imposes nothing.
    [InlineData("""{"allowedVoiceStyles":7}""", """{"styleTags":["cold"]}""", "")]
    [InlineData("""{"allowedVoiceStyles":null}""", """{"styleTags":["cold"]}""", "")]
    [InlineData("""{"allowedVoiceStyles":{}}""", """{"styleTags":["cold"]}""", "")]
    public void Style_Tag_Allowlist_Matrix(string settingsJson, string modelRef, string expectedReason)
    {
        var reasons = VoiceCompatibility.Check(
            Voice(modelRef, VoiceType.Stock, cloningEnabled: false),
            Project(Spanish, settingsJson),
            true,
            true);

        if (expectedReason.Length == 0)
        {
            Assert.DoesNotContain(reasons, r => r.Contains("STYLE_NOT_ALLOWED", StringComparison.Ordinal));
        }
        else
        {
            Assert.Contains(reasons, r => r.Contains(expectedReason, StringComparison.Ordinal));
        }
    }

    [Theory]
    [InlineData("""{"allowedGenders":["female"]}""", """{"gender":"female"}""", "")]
    [InlineData("""{"allowedGenders":["female"]}""", """{"gender":" male "}""", "GENDER_NOT_ALLOWED")]
    // A voice with no (or a blank) gender passes any allowlist.
    [InlineData("""{"allowedGenders":["female"]}""", """{"sampleRateHz":48000}""", "")]
    [InlineData("""{"allowedGenders":["female"]}""", """{"gender":"   "}""", "")]
    [InlineData("""{"allowedGenders":["female"]}""", """{"gender":null}""", "")]
    [InlineData("""{"allowedGenders":["female"]}""", """{"gender":42}""", "")]
    // An empty allowlist imposes no constraint.
    [InlineData("""{"allowedGenders":[]}""", """{"gender":"male"}""", "")]
    [InlineData("""{"allowedGenders":null}""", """{"gender":"male"}""", "")]
    [InlineData("""{"allowedGenders":7}""", """{"gender":"male"}""", "")]
    // A single string is coerced to a one-element allowlist, like the style list.
    [InlineData("""{"allowedGenders":"female"}""", """{"gender":"male"}""", "GENDER_NOT_ALLOWED")]
    [InlineData("""{"allowedGenders":"female"}""", """{"gender":"female"}""", "")]
    public void Gender_Constraint_Matrix(string settingsJson, string modelRef, string expectedReason)
    {
        var reasons = VoiceCompatibility.Check(
            Voice(modelRef, VoiceType.Stock, cloningEnabled: false),
            Project(Spanish, settingsJson),
            true,
            true);

        if (expectedReason.Length == 0)
        {
            Assert.DoesNotContain(reasons, r => r.Contains("GENDER_NOT_ALLOWED", StringComparison.Ordinal));
        }
        else
        {
            Assert.Contains(reasons, r => r.Contains(expectedReason, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Every_Reason_Is_Truncated_To_The_Documented_Limit()
    {
        // `VoiceProfile.Language` is a 2-3 letter code, so a long reason cannot
        // come from the language tag. A long *style tag list* can: the tag array
        // is free-form JSON and the reason embeds it verbatim.
        var manyTags = string.Join(
            ",",
            Enumerable.Range(0, 200)
                .Select(i => "style-" + i.ToString("D3", System.Globalization.CultureInfo.InvariantCulture)));
        var modelRef = $$"""{"styleTags":["{{manyTags}}"]}""";
        var reasons = VoiceCompatibility.Check(
            Voice(modelRef, VoiceType.Stock, cloningEnabled: false),
            Project(Spanish, """{"allowedVoiceStyles":["warm"]}"""),
            hasCoveringConsent: true,
            cloningGloballyEnabled: true);

        var reason = Assert.Single(reasons);
        Assert.Contains("STYLE_NOT_ALLOWED", reason, StringComparison.Ordinal);
        Assert.Equal(VoiceCompatibility.MaxReasonLength, reason.Length);
    }

    [Fact]
    public void Reasons_Under_The_Limit_Are_Not_Truncated()
    {
        var reasons = VoiceCompatibility.Check(
            Voice("""{"sampleRateHz":8000}""", VoiceType.Stock, cloningEnabled: false),
            Project(Spanish),
            true,
            true);

        var reason = Assert.Single(reasons);
        Assert.True(reason.Length < VoiceCompatibility.MaxReasonLength);
    }

    [Fact]
    public void Cloning_Gate_Reports_Both_Denial_Reasons()
    {
        var stock = Project(Spanish);

        // Cloning switched off globally wins over the consent check.
        var disabled = VoiceCompatibility.Check(
            Voice(null, VoiceType.Cloned, cloningEnabled: false), stock, hasCoveringConsent: false, cloningGloballyEnabled: false);
        Assert.Contains(disabled, r => r.Contains("CLONING_DISABLED", StringComparison.Ordinal));

        // Enabled but no covering consent: the consent reason, never a bypass.
        var noConsent = VoiceCompatibility.Check(
            Voice(null, VoiceType.Cloned, cloningEnabled: false), stock, hasCoveringConsent: false, cloningGloballyEnabled: true);
        Assert.Contains(noConsent, r => r.Contains("CONSENT_REQUIRED", StringComparison.Ordinal));

        // Enabled with consent: clean.
        var allowed = VoiceCompatibility.Check(
            Voice(null, VoiceType.Cloned, cloningEnabled: false), stock, hasCoveringConsent: true, cloningGloballyEnabled: true);
        Assert.Empty(allowed);

        // The `CloningEnabled` flag gates a Stock-typed voice too.
        var flagged = VoiceCompatibility.Check(
            Voice(null, VoiceType.Stock, cloningEnabled: true), stock, hasCoveringConsent: false, cloningGloballyEnabled: true);
        Assert.Contains(flagged, r => r.Contains("CONSENT_REQUIRED", StringComparison.Ordinal));
    }

    [Fact]
    public void Requires_Consent_Mirrors_The_Structural_Rule()
    {
        Assert.False(VoiceCompatibility.RequiresConsent(Voice(null, VoiceType.Stock, cloningEnabled: false)));
        Assert.True(VoiceCompatibility.RequiresConsent(Voice(null, VoiceType.Cloned, cloningEnabled: false)));
        Assert.True(VoiceCompatibility.RequiresConsent(Voice(null, VoiceType.Stock, cloningEnabled: true)));
    }

    [Fact]
    public void Null_Arguments_Are_Rejected_Not_Silently_Allowed()
    {
        Assert.Throws<ArgumentNullException>(() =>
            VoiceCompatibility.Check(null!, Project(Spanish), true, true));
        Assert.Throws<ArgumentNullException>(() =>
            VoiceCompatibility.Check(Voice(null, VoiceType.Stock, false), null!, true, true));
        Assert.Throws<ArgumentNullException>(() => VoiceCompatibility.RequiresConsent(null!));
    }

    [Theory]
    [InlineData("es", "ES", "")]
    [InlineData("es", "es", "")]
    [InlineData("es", "fr", "LANGUAGE_MISMATCH")]
    [InlineData("es", "de", "LANGUAGE_MISMATCH")]
    [InlineData("en", "es", "LANGUAGE_MISMATCH")]
    public void Language_Comparison_Trims_And_Ignores_Case(string voiceLanguage, string targetLanguage, string expectedReason)
    {
        var reasons = VoiceCompatibility.Check(
            Voice(null, VoiceType.Stock, cloningEnabled: false, language: voiceLanguage),
            Project(targetLanguage),
            true,
            true);

        if (expectedReason.Length == 0)
        {
            Assert.Empty(reasons);
        }
        else
        {
            Assert.Contains(reasons, r => r.Contains(expectedReason, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Documented_Floors_Are_Stable_Constants()
    {
        Assert.Equal(16_000, VoiceCompatibility.MinSampleRateHz);
        Assert.Equal(1, VoiceCompatibility.MinChannels);
        Assert.Equal(500, VoiceCompatibility.MaxReasonLength);
    }

    private static readonly DateTimeOffset FrozenNow =
        DateTimeOffset.Parse("2026-01-15T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// A minimal valid project. `DubbingProject.Validate` requires a non-blank
    /// `SettingsJson`, a 2-3 letter source/target language, and the two
    /// languages to differ, so a blank or equal target is rejected before the
    /// compatibility rule set ever runs.
    /// </summary>
    private static DubbingProject Project(string targetLanguage, string? settingsJson = null) =>
        new(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            Guid.Parse("22222222-2222-2222-2222-222222222222"),
            targetLanguage.Equals("en", StringComparison.OrdinalIgnoreCase) ? "fr" : "en",
            targetLanguage,
            ProjectStatus.Created,
            settingsJson ?? "{}",
            new string('a', 64),
            null,
            null,
            FrozenNow,
            FrozenNow);

    private static VoiceProfile Voice(
        string? modelRefJson,
        VoiceType type,
        bool cloningEnabled,
        string language = "es") =>
        new(
            Guid.Parse("33333333-3333-3333-3333-333333333333"),
            Guid.Parse("22222222-2222-2222-2222-222222222222"),
            "mock",
            "mock-voice",
            "1",
            language,
            type,
            cloningEnabled,
            modelRefJson,
            FrozenNow);
}

/// <summary>
/// Branch completion for the selection-conflict refresh payload. When only one
/// of the two version ids is known the envelope must still carry a
/// <c>currentVersionIds</c> object with the missing entry explicitly null, so a
/// client can tell "unknown" from "absent".
/// </summary>
public sealed class SegmentConflictEnvelopeBranchTests
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void Current_Version_Ids_Report_Each_Side_Independently(bool withTranscript, bool withTranslation)
    {
        Guid? transcript = withTranscript ? Guid.Parse("44444444-4444-4444-4444-444444444444") : null;
        Guid? translation = withTranslation ? Guid.Parse("55555555-5555-5555-5555-555555555555") : null;
        var ex = new SelectionConflictApiException(11, transcript, translation);

        var ids = Assert.IsType<Dictionary<string, object?>>(ex.GetErrorDetails()["currentVersionIds"]);

        Assert.Equal(transcript?.ToString("D"), ids["transcriptVersionId"]);
        Assert.Equal(translation?.ToString("D"), ids["translationVersionId"]);
        // The keys are always present, so a client never has to probe for them.
        Assert.True(ids.ContainsKey("transcriptVersionId"));
        Assert.True(ids.ContainsKey("translationVersionId"));
    }

    [Fact]
    public void Selection_Version_Is_Carried_Even_When_No_Versions_Exist()
    {
        var ex = new SelectionConflictApiException(0, null, null);
        var details = ex.GetErrorDetails();

        Assert.Equal(0, Assert.IsType<int>(details["currentSelectionVersion"]));
        var ids = Assert.IsType<Dictionary<string, object?>>(details["currentVersionIds"]);
        Assert.Null(ids["transcriptVersionId"]);
        Assert.Null(ids["translationVersionId"]);
    }
}
