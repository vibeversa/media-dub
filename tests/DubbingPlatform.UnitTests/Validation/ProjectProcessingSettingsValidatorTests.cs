// Task 039C: settings-schema validation unit gap closure.

using DubbingPlatform.Application.Validation;

namespace DubbingPlatform.UnitTests.Validation;

/// <summary>
/// Task 001/007 coverage for <see cref="ProjectProcessingSettingsValidator"/>.
/// Every rule is exercised with a valid and an invalid case at its exact
/// boundary (0, 1, max, max+1), including the cross-field rules
/// (<c>schemaVersion</c> gates the shape rule). Unknown or unparsable values
/// resolve to an explicit safe outcome — a <c>null</c> document or a verbatim
/// free-form string — never a 500-style throw. Pure: no I/O, no clock, no
/// database.
/// </summary>
public sealed class ProjectProcessingSettingsValidatorTests
{
    private static string Repeat(char c, int count) => new(c, count);

    private static string Padded(int length) => Repeat('a', length);

    // ------------------------------------------------------------------
    // BeValidJson
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{oops")]
    [InlineData("{\"a\":1,}")]
    [InlineData("{'a':1}")]
    [InlineData("undefined")]
    public void BeValidJson_Rejects_Non_Json(string? json)
    {
        Assert.False(ProjectProcessingSettingsValidator.BeValidJson(json));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("0")]
    [InlineData("\"text\"")]
    [InlineData("null")]
    [InlineData("{\"a\":[1,2,{\"b\":null}]}")]
    public void BeValidJson_Accepts_Any_Json_Shape(string json)
    {
        Assert.True(ProjectProcessingSettingsValidator.BeValidJson(json));
    }

    // ------------------------------------------------------------------
    // HaveSupportedSchemaVersion
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{oops")]
    [InlineData("{}")]
    [InlineData("""{"schemaVersion":"1"}""")]
    [InlineData("""{"schemaVersion":null}""")]
    [InlineData("""{"schemaVersion":0}""")]
    [InlineData("""{"schemaVersion":2}""")]
    [InlineData("""{"schemaVersion":-1}""")]
    [InlineData("""{"schemaVersion":1.5}""")]
    [InlineData("""{"schemaVersion":9999999999}""")]
    public void HaveSupportedSchemaVersion_Rejects_Anything_But_Exactly_One(string? json)
    {
        Assert.False(ProjectProcessingSettingsValidator.HaveSupportedSchemaVersion(json));
    }

    [Fact]
    public void HaveSupportedSchemaVersion_Accepts_Only_The_Supported_Version()
    {
        Assert.Equal(1, ProjectProcessingSettingsValidator.SupportedSchemaVersion);
        Assert.True(ProjectProcessingSettingsValidator.HaveSupportedSchemaVersion("""{"schemaVersion":1}"""));
        Assert.True(ProjectProcessingSettingsValidator.HaveSupportedSchemaVersion(
            """{"other":0,"schemaVersion":1}"""));
    }

    // ------------------------------------------------------------------
    // HaveValidShape — root
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{oops")]
    [InlineData("[]")]
    [InlineData("42")]
    [InlineData("\"settings\"")]
    [InlineData("null")]
    public void HaveValidShape_Requires_A_Json_Object_Root(string? json)
    {
        Assert.False(ProjectProcessingSettingsValidator.HaveValidShape(json));
    }

    [Fact]
    public void HaveValidShape_Accepts_An_Empty_Object()
    {
        Assert.True(ProjectProcessingSettingsValidator.HaveValidShape("{}"));
    }

    // ------------------------------------------------------------------
    // HaveValidShape — the four bounded enum-ish string fields
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("sourceSeparationPolicy")]
    [InlineData("outputProfile")]
    [InlineData("timingStrictness")]
    [InlineData("voicePolicy")]
    public void Bounded_String_Field_Accepts_Up_To_64_Chars_And_Null(string field)
    {
        Assert.True(ProjectProcessingSettingsValidator.HaveValidShape(
            $$"""{"schemaVersion":1,"{{field}}":null}"""));
        Assert.True(ProjectProcessingSettingsValidator.HaveValidShape(
            $$"""{"schemaVersion":1,"{{field}}":"{{Padded(64)}}"}"""));
        Assert.True(ProjectProcessingSettingsValidator.HaveValidShape(
            $$"""{"schemaVersion":1,"{{field}}":"ok"}"""));
    }

    [Theory]
    [InlineData("sourceSeparationPolicy")]
    [InlineData("outputProfile")]
    [InlineData("timingStrictness")]
    [InlineData("voicePolicy")]
    public void Bounded_String_Field_Rejects_Over_64_Chars(string field)
    {
        Assert.False(ProjectProcessingSettingsValidator.HaveValidShape(
            $$"""{"schemaVersion":1,"{{field}}":"{{Padded(65)}}"}"""));
    }

    [Theory]
    [InlineData("sourceSeparationPolicy")]
    [InlineData("outputProfile")]
    [InlineData("timingStrictness")]
    [InlineData("voicePolicy")]
    public void Bounded_String_Field_Rejects_Empty_Whitespace_And_Non_String(string field)
    {
        Assert.False(ProjectProcessingSettingsValidator.HaveValidShape(
            $$"""{"schemaVersion":1,"{{field}}":""}"""));
        Assert.False(ProjectProcessingSettingsValidator.HaveValidShape(
            $$"""{"schemaVersion":1,"{{field}}":"   "}"""));
        Assert.False(ProjectProcessingSettingsValidator.HaveValidShape(
            $$"""{"schemaVersion":1,"{{field}}":7}"""));
        Assert.False(ProjectProcessingSettingsValidator.HaveValidShape(
            $$"""{"schemaVersion":1,"{{field}}":true}"""));
        Assert.False(ProjectProcessingSettingsValidator.HaveValidShape(
            $$"""{"schemaVersion":1,"{{field}}":["a"]}"""));
        Assert.False(ProjectProcessingSettingsValidator.HaveValidShape(
            "{\"schemaVersion\":1,\"" + field + "\":{\"a\":1}}"));
    }

    // ------------------------------------------------------------------
    // HaveValidShape — reviewThreshold
    // ------------------------------------------------------------------

    private static string ThresholdJson(double value)
    {
        return "{\"schemaVersion\":1,\"reviewThreshold\":"
            + value.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + "}";
    }

    [Theory]
    [InlineData(0)]
    [InlineData(0.0)]
    [InlineData(0.5)]
    [InlineData(1)]
    [InlineData(1.0)]
    public void ReviewThreshold_Accepts_Inclusive_Zero_To_One(double value)
    {
        Assert.True(ProjectProcessingSettingsValidator.HaveValidShape(ThresholdJson(value)));
    }

    [Fact]
    public void ReviewThreshold_Accepts_Null_And_Missing()
    {
        Assert.True(ProjectProcessingSettingsValidator.HaveValidShape("""{"schemaVersion":1,"reviewThreshold":null}"""));
        Assert.True(ProjectProcessingSettingsValidator.HaveValidShape("""{"schemaVersion":1}"""));
    }

    [Theory]
    [InlineData(-0.0000001)]
    [InlineData(-1)]
    [InlineData(1.0000001)]
    [InlineData(2)]
    [InlineData(100)]
    public void ReviewThreshold_Rejects_Out_Of_Range(double value)
    {
        Assert.False(ProjectProcessingSettingsValidator.HaveValidShape(ThresholdJson(value)));
    }

    [Theory]
    [InlineData("\"0.5\"")]
    [InlineData("true")]
    [InlineData("[]")]
    [InlineData("{\"a\":1}")]
    public void ReviewThreshold_Rejects_Non_Number(string raw)
    {
        Assert.False(ProjectProcessingSettingsValidator.HaveValidShape(
            $$"""{"schemaVersion":1,"reviewThreshold":{{raw}}}"""));
    }

    // ------------------------------------------------------------------
    // HaveValidShape — glossary
    // ------------------------------------------------------------------

    [Fact]
    public void Glossary_Accepts_Missing_Null_And_Empty_Array()
    {
        Assert.True(ProjectProcessingSettingsValidator.HaveValidShape("""{"schemaVersion":1}"""));
        Assert.True(ProjectProcessingSettingsValidator.HaveValidShape("""{"schemaVersion":1,"glossary":null}"""));
        Assert.True(ProjectProcessingSettingsValidator.HaveValidShape("""{"schemaVersion":1,"glossary":[]}"""));
    }

    [Fact]
    public void Glossary_Entry_At_Exact_Max_Count_Is_Accepted_And_One_Over_Is_Rejected()
    {
        Assert.Equal(1000, ProjectProcessingSettingsValidator.MaxGlossaryEntries);

        var atLimit = GlossaryJson(ProjectProcessingSettingsValidator.MaxGlossaryEntries);
        Assert.True(ProjectProcessingSettingsValidator.HaveValidShape(atLimit));

        var overLimit = GlossaryJson(ProjectProcessingSettingsValidator.MaxGlossaryEntries + 1);
        Assert.False(ProjectProcessingSettingsValidator.HaveValidShape(overLimit));
    }

    [Fact]
    public void Glossary_Must_Be_An_Array_Of_Objects()
    {
        Assert.False(ProjectProcessingSettingsValidator.HaveValidShape("""{"schemaVersion":1,"glossary":"a"}"""));
        Assert.False(ProjectProcessingSettingsValidator.HaveValidShape("""{"schemaVersion":1,"glossary":{}}"""));
        Assert.False(ProjectProcessingSettingsValidator.HaveValidShape("""{"schemaVersion":1,"glossary":["a"]}"""));
        Assert.False(ProjectProcessingSettingsValidator.HaveValidShape("""{"schemaVersion":1,"glossary":[[]]}"""));
    }

    [Theory]
    [InlineData("""{"targetTerm":"b"}""")]
    [InlineData("""{"sourceTerm":""}""")]
    [InlineData("""{"sourceTerm":"   "}""")]
    [InlineData("""{"sourceTerm":7}""")]
    [InlineData("""{"sourceTerm":null}""")]
    public void Glossary_Rejects_Bad_Source_Term(string entry)
    {
        Assert.False(ProjectProcessingSettingsValidator.HaveValidShape(
            $$"""{"schemaVersion":1,"glossary":[{{entry}}]}"""));
    }

    [Theory]
    [InlineData("""{"sourceTerm":"a"}""")]
    [InlineData("""{"sourceTerm":"a","targetTerm":""}""")]
    [InlineData("""{"sourceTerm":"a","targetTerm":"   "}""")]
    [InlineData("""{"sourceTerm":"a","targetTerm":null}""")]
    [InlineData("""{"sourceTerm":"a","targetTerm":[]}""")]
    public void Glossary_Rejects_Bad_Target_Term(string entry)
    {
        Assert.False(ProjectProcessingSettingsValidator.HaveValidShape(
            $$"""{"schemaVersion":1,"glossary":[{{entry}}]}"""));
    }

    [Fact]
    public void Glossary_Terms_Are_Bounded_At_256_Chars()
    {
        var sourceOk = $$"""{"sourceTerm":"{{Padded(256)}}","targetTerm":"b"}""";
        var sourceBad = $$"""{"sourceTerm":"{{Padded(257)}}","targetTerm":"b"}""";
        var targetOk = $$"""{"sourceTerm":"a","targetTerm":"{{Padded(256)}}"}""";
        var targetBad = $$"""{"sourceTerm":"a","targetTerm":"{{Padded(257)}}"}""";

        Assert.True(ProjectProcessingSettingsValidator.HaveValidShape(
            $$"""{"schemaVersion":1,"glossary":[{{sourceOk}}]}"""));
        Assert.False(ProjectProcessingSettingsValidator.HaveValidShape(
            $$"""{"schemaVersion":1,"glossary":[{{sourceBad}}]}"""));
        Assert.True(ProjectProcessingSettingsValidator.HaveValidShape(
            $$"""{"schemaVersion":1,"glossary":[{{targetOk}}]}"""));
        Assert.False(ProjectProcessingSettingsValidator.HaveValidShape(
            $$"""{"schemaVersion":1,"glossary":[{{targetBad}}]}"""));
    }

    [Fact]
    public void Glossary_Notes_Are_Optional_And_Bounded_At_1024_Chars()
    {
        var withNull = """{"sourceTerm":"a","targetTerm":"b","notes":null}""";
        var withEmpty = """{"sourceTerm":"a","targetTerm":"b","notes":""}""";
        var atLimit = $$"""{"sourceTerm":"a","targetTerm":"b","notes":"{{Padded(1024)}}"}""";
        var overLimit = $$"""{"sourceTerm":"a","targetTerm":"b","notes":"{{Padded(1025)}}"}""";
        var wrongType = """{"sourceTerm":"a","targetTerm":"b","notes":5}""";

        Assert.True(ProjectProcessingSettingsValidator.HaveValidShape(
            $$"""{"schemaVersion":1,"glossary":[{{withNull}}]}"""));
        Assert.True(ProjectProcessingSettingsValidator.HaveValidShape(
            $$"""{"schemaVersion":1,"glossary":[{{withEmpty}}]}"""));
        Assert.True(ProjectProcessingSettingsValidator.HaveValidShape(
            $$"""{"schemaVersion":1,"glossary":[{{atLimit}}]}"""));
        Assert.False(ProjectProcessingSettingsValidator.HaveValidShape(
            $$"""{"schemaVersion":1,"glossary":[{{overLimit}}]}"""));
        Assert.False(ProjectProcessingSettingsValidator.HaveValidShape(
            $$"""{"schemaVersion":1,"glossary":[{{wrongType}}]}"""));
    }

    // ------------------------------------------------------------------
    // HaveValidShape — styleInstructions
    // ------------------------------------------------------------------

    [Fact]
    public void StyleInstructions_Accepts_Missing_Null_And_4000_Chars()
    {
        Assert.True(ProjectProcessingSettingsValidator.HaveValidShape("""{"schemaVersion":1}"""));
        Assert.True(ProjectProcessingSettingsValidator.HaveValidShape("""{"schemaVersion":1,"styleInstructions":null}"""));
        Assert.True(ProjectProcessingSettingsValidator.HaveValidShape(
            $$"""{"schemaVersion":1,"styleInstructions":"{{Padded(4000)}}"}"""));
    }

    [Fact]
    public void StyleInstructions_Rejects_Over_4000_Chars_And_Non_String()
    {
        Assert.False(ProjectProcessingSettingsValidator.HaveValidShape(
            $$"""{"schemaVersion":1,"styleInstructions":"{{Padded(4001)}}"}"""));
        Assert.False(ProjectProcessingSettingsValidator.HaveValidShape("""{"schemaVersion":1,"styleInstructions":7}"""));
        Assert.False(ProjectProcessingSettingsValidator.HaveValidShape("""{"schemaVersion":1,"styleInstructions":[]}"""));
    }

    // ------------------------------------------------------------------
    // Cross-field rules
    // ------------------------------------------------------------------

    [Fact]
    public void Shape_Rule_Only_Applies_Once_The_Version_Is_Supported()
    {
        // Both version and shape are wrong: the version error is the one raised,
        // because the shape rule is gated on a supported version.
        Assert.False(ProjectProcessingSettingsValidator.HaveSupportedSchemaVersion("""{"schemaVersion":7,"reviewThreshold":9}"""));
        Assert.False(ProjectProcessingSettingsValidator.HaveValidShape("""{"schemaVersion":7,"reviewThreshold":9}"""));
    }

    [Fact]
    public void Unknown_Property_Names_Are_Ignored_For_Forward_Compatibility()
    {
        Assert.True(ProjectProcessingSettingsValidator.HaveValidShape(
            """{"schemaVersion":1,"futureField":{"nested":[1,2,3]},"anotherOne":"anything"}"""));
    }

    [Fact]
    public void Unknown_Enum_Values_Are_Kept_As_Free_Form_Strings_Not_Rejected()
    {
        // The four policy fields are bounded strings, not enums: an unknown
        // name is carried verbatim (explicit safe default) and never throws.
        var json = """{"schemaVersion":1,"sourceSeparationPolicy":"quantum-denoise","outputProfile":"streaming-v9","timingStrictness":"slack","voicePolicy":"human-review"}""";

        Assert.True(ProjectProcessingSettingsValidator.HaveValidShape(json));
        var document = ProjectProcessingSettingsValidator.Parse(json);
        Assert.NotNull(document);
        Assert.Equal("quantum-denoise", document.SourceSeparationPolicy);
        Assert.Equal("streaming-v9", document.OutputProfile);
        Assert.Equal("slack", document.TimingStrictness);
        Assert.Equal("human-review", document.VoicePolicy);
    }

    [Fact]
    public void Exact_Allowed_Value_Bounds_Are_Documented_By_Behaviour()
    {
        // schemaVersion: exactly 1.
        Assert.Equal(1, ProjectProcessingSettingsValidator.SupportedSchemaVersion);
        Assert.True(ProjectProcessingSettingsValidator.HaveSupportedSchemaVersion("""{"schemaVersion":1}"""));
        Assert.False(ProjectProcessingSettingsValidator.HaveSupportedSchemaVersion("""{"schemaVersion":0}"""));
        Assert.False(ProjectProcessingSettingsValidator.HaveSupportedSchemaVersion("""{"schemaVersion":2}"""));

        // Four bounded string fields: 1..64 chars (0 is rejected as empty).
        Assert.True(ProjectProcessingSettingsValidator.HaveValidShape("""{"schemaVersion":1,"voicePolicy":"a"}"""));
        Assert.False(ProjectProcessingSettingsValidator.HaveValidShape("""{"schemaVersion":1,"voicePolicy":""}"""));
        Assert.True(ProjectProcessingSettingsValidator.HaveValidShape(
            $$"""{"schemaVersion":1,"voicePolicy":"{{Padded(64)}}"}"""));
        Assert.False(ProjectProcessingSettingsValidator.HaveValidShape(
            $$"""{"schemaVersion":1,"voicePolicy":"{{Padded(65)}}"}"""));

        // reviewThreshold: 0..1 inclusive; glossary: 0..1000 entries;
        // terms: 1..256 chars; notes: 0..1024 chars; style: 0..4000 chars.
        Assert.True(ProjectProcessingSettingsValidator.HaveValidShape("""{"schemaVersion":1,"reviewThreshold":0}"""));
        Assert.False(ProjectProcessingSettingsValidator.HaveValidShape("""{"schemaVersion":1,"reviewThreshold":1.5}"""));
        Assert.True(ProjectProcessingSettingsValidator.HaveValidShape("""{"schemaVersion":1,"glossary":[]}"""));
        Assert.False(ProjectProcessingSettingsValidator.HaveValidShape(GlossaryJson(ProjectProcessingSettingsValidator.MaxGlossaryEntries + 1)));
        Assert.True(ProjectProcessingSettingsValidator.HaveValidShape("""{"schemaVersion":1,"styleInstructions":""}"""));
        Assert.False(ProjectProcessingSettingsValidator.HaveValidShape(
            $$"""{"schemaVersion":1,"styleInstructions":"{{Padded(4001)}}"}"""));
    }

    // ------------------------------------------------------------------
    // Parse
    // ------------------------------------------------------------------

    [Fact]
    public void Parse_Returns_Null_For_Every_Rejected_Input_And_Never_Throws()
    {
        string?[] rejected =
        [
            null,
            "",
            "{oops",
            "{}",
            """{"schemaVersion":2}""",
            """{"schemaVersion":1,"outputProfile":7}""",
            """{"schemaVersion":1,"reviewThreshold":2}""",
            """{"schemaVersion":1,"glossary":[{"sourceTerm":"a"}]}""",
        ];

        foreach (var json in rejected)
        {
            Assert.Null(ProjectProcessingSettingsValidator.Parse(json));
        }
    }

    [Fact]
    public void Parse_Throws_An_Uncoded_Exception_For_A_Valid_Json_Non_Object_Root_Known_Gap()
    {
        // KNOWN GAP (Task 039C): `[]`, `42` and the literal `null` are valid
        // JSON, so BeValidJson passes and HaveSupportedSchemaVersion reaches
        // JsonElement.TryGetProperty on a non-object element, which STJ turns
        // into an uncoded InvalidOperationException. Shape validation itself is
        // safe (see HaveValidShape_Requires_A_Json_Object_Root); the version
        // probe should degrade to false so the caller answers 400
        // VALIDATION_FAILED instead of 500. Pinned so the fix is visible.
        Assert.False(ProjectProcessingSettingsValidator.HaveValidShape("[]"));
        Assert.False(ProjectProcessingSettingsValidator.HaveValidShape("null"));
        Assert.Throws<InvalidOperationException>(() => ProjectProcessingSettingsValidator.Parse("[]"));
        Assert.Throws<InvalidOperationException>(() => ProjectProcessingSettingsValidator.Parse("42"));
        Assert.Throws<InvalidOperationException>(() => ProjectProcessingSettingsValidator.Parse("null"));
        Assert.Throws<InvalidOperationException>(
            () => ProjectProcessingSettingsValidator.HaveSupportedSchemaVersion("[]"));
    }

    [Fact]
    public void Parse_Maps_Optional_And_Absent_Fields_To_Null()
    {
        var document = ProjectProcessingSettingsValidator.Parse("""{"schemaVersion":1}""");

        Assert.NotNull(document);
        Assert.Equal(1, document.SchemaVersion);
        Assert.Null(document.SourceSeparationPolicy);
        Assert.Null(document.OutputProfile);
        Assert.Null(document.TimingStrictness);
        Assert.Null(document.VoicePolicy);
        Assert.Null(document.ReviewThreshold);
        Assert.Null(document.Glossary);
        Assert.Null(document.StyleInstructions);
    }

    [Fact]
    public void Parse_Maps_Null_Valued_Fields_To_Null_Not_Empty_String()
    {
        var document = ProjectProcessingSettingsValidator.Parse(
            """{"schemaVersion":1,"outputProfile":null,"reviewThreshold":null,"glossary":null,"styleInstructions":null}""");

        Assert.NotNull(document);
        Assert.Null(document.OutputProfile);
        Assert.Null(document.ReviewThreshold);
        Assert.Null(document.Glossary);
        Assert.Null(document.StyleInstructions);
    }

    [Fact]
    public void Parse_Maps_Glossary_And_Optional_Notes()
    {
        var document = ProjectProcessingSettingsValidator.Parse(
            """{"schemaVersion":1,"glossary":[{"sourceTerm":"hello","targetTerm":"hallo","notes":"greeting"},{"sourceTerm":"bye","targetTerm":"tschuess"}]}""");

        Assert.NotNull(document);
        Assert.NotNull(document.Glossary);
        Assert.Equal(2, document.Glossary.Count);
        Assert.Equal(new GlossaryEntry("hello", "hallo", "greeting"), document.Glossary[0]);
        Assert.Equal(new GlossaryEntry("bye", "tschuess", null), document.Glossary[1]);
    }

    [Fact]
    public void Parse_Reads_ReviewThreshold_At_The_Exact_Endpoints()
    {
        Assert.Equal(0d, ProjectProcessingSettingsValidator.Parse("""{"schemaVersion":1,"reviewThreshold":0}""")!.ReviewThreshold!.Value);
        Assert.Equal(1d, ProjectProcessingSettingsValidator.Parse("""{"schemaVersion":1,"reviewThreshold":1}""")!.ReviewThreshold!.Value);
        Assert.Equal(0.25d, ProjectProcessingSettingsValidator.Parse("""{"schemaVersion":1,"reviewThreshold":0.25}""")!.ReviewThreshold!.Value);
    }

    // ------------------------------------------------------------------
    // FluentValidation surface (code + message per rule)
    // ------------------------------------------------------------------

    [Fact]
    public void Validator_Accepts_A_Complete_Version_One_Document()
    {
        var result = new ProjectProcessingSettingsValidator().Validate(
            """{"schemaVersion":1,"sourceSeparationPolicy":"auto","outputProfile":"broadcast","timingStrictness":"strict","voicePolicy":"clone-allowed","reviewThreshold":0.5,"glossary":[{"sourceTerm":"hello","targetTerm":"hallo"}],"styleInstructions":"formal"}""");

        Assert.True(result.IsValid, string.Join("; ", result.Errors.Select(e => e.ErrorMessage)));
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Validator_Rejects_Blank_Input_With_Both_Messages()
    {
        var blank = new ProjectProcessingSettingsValidator().Validate("   ");

        Assert.False(blank.IsValid);
        Assert.Contains(blank.Errors, e => e.ErrorMessage == "Processing settings must not be empty.");
        Assert.Contains(blank.Errors, e => e.ErrorMessage == "Processing settings must be valid JSON.");
    }

    [Fact]
    public void Validator_Rejects_Malformed_Json_With_ValidationFailed_Code()
    {
        var result = new ProjectProcessingSettingsValidator().Validate("{oops");

        Assert.False(result.IsValid);
        var error = Assert.Single(result.Errors);
        Assert.Equal("Processing settings must be valid JSON.", error.ErrorMessage);
        Assert.Equal("VALIDATION_FAILED", error.ErrorCode);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"schemaVersion":0}""")]
    [InlineData("""{"schemaVersion":2}""")]
    [InlineData("""{"schemaVersion":"1"}""")]
    [InlineData("""{"schemaVersion":1.5}""")]
    public void Validator_Rejects_Unsupported_Version_With_Dedicated_Code(string json)
    {
        var result = new ProjectProcessingSettingsValidator().Validate(json);

        Assert.False(result.IsValid);
        var error = Assert.Single(result.Errors);
        Assert.Equal(
            $"Unsupported processing settings schema version. Supported: {ProjectProcessingSettingsValidator.SupportedSchemaVersion}.",
            error.ErrorMessage);
        Assert.Equal(ProjectProcessingSettingsValidator.UnsupportedVersionCode, error.ErrorCode);
    }

    [Theory]
    [InlineData("""{"schemaVersion":1,"outputProfile":7}""")]
    [InlineData("""{"schemaVersion":1,"outputProfile":""}""")]
    [InlineData("""{"schemaVersion":1,"reviewThreshold":-1}""")]
    [InlineData("""{"schemaVersion":1,"glossary":[{"targetTerm":"b"}]}""")]
    [InlineData("""{"schemaVersion":1,"styleInstructions":[]}""")]
    public void Validator_Rejects_Bad_Shape_With_ValidationFailed_Code(string json)
    {
        var result = new ProjectProcessingSettingsValidator().Validate(json);

        Assert.False(result.IsValid);
        var error = Assert.Single(result.Errors);
        Assert.Equal("Processing settings have an invalid shape.", error.ErrorMessage);
        Assert.Equal("VALIDATION_FAILED", error.ErrorCode);
    }

    [Fact]
    public void Validator_Only_Reports_The_Version_Error_For_A_Versioned_But_Malformed_Document()
    {
        // Cross-field gating: an unsupported version short-circuits the shape
        // rule, so a client fixing the version does not get a second error storm.
        var result = new ProjectProcessingSettingsValidator().Validate("""{"schemaVersion":99,"reviewThreshold":42}""");

        var error = Assert.Single(result.Errors);
        Assert.Equal(ProjectProcessingSettingsValidator.UnsupportedVersionCode, error.ErrorCode);
    }

    [Fact]
    public void Validator_Never_Throws_For_Hostile_Input()
    {
        // Non-object JSON roots (`0`, `[]`, `null`) are excluded: they hit the
        // uncoded-throw gap pinned in
        // Parse_Throws_An_Uncoded_Exception_For_A_Valid_Json_Non_Object_Root_Known_Gap.
        string[] hostile =
        [
            "", " ", "\t", "{", "}", "{\"schemaVersion\":{}}",
            """{"schemaVersion":1,"glossary":[{"sourceTerm":"a","targetTerm":"b","notes":{"x":1}}]}""",
            Repeat('"', 5000),
            Repeat('{', 200),
        ];

        foreach (var json in hostile)
        {
            var result = new ProjectProcessingSettingsValidator().Validate(json);
            Assert.NotNull(result);

            // No rule ever produces a 500-style outcome: every failure is a
            // catalogued 4xx code (NotEmptyValidator is FluentValidation's own
            // default for the required-ness rule).
            foreach (var error in result.Errors)
            {
                Assert.Contains(
                    error.ErrorCode,
                    new[]
                    {
                        "VALIDATION_FAILED",
                        ProjectProcessingSettingsValidator.UnsupportedVersionCode,
                        "NotEmptyValidator",
                    },
                    StringComparer.Ordinal);
            }
        }
    }

    private static string GlossaryJson(int count)
    {
        var entries = new string[count];
        for (var i = 0; i < count; i++)
        {
            entries[i] = $$"""{"sourceTerm":"s{{i}}","targetTerm":"t{{i}}"}""";
        }

        return $$"""{"schemaVersion":1,"glossary":[{{string.Join(",", entries)}}]}""";
    }
}
