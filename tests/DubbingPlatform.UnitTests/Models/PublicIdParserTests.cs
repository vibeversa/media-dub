// Task 039C: exports unit gap closure (public-id parsing area).
using DubbingPlatform.Api.Models;
using DubbingPlatform.Domain.Exceptions;
using DubbingPlatform.Domain.Identity;

namespace DubbingPlatform.UnitTests.Models;

/// <summary>
/// Public-id round-trip and fail-closed parsing. Prefixed ids are
/// <c>{prefix_}{32 lowercase hex}</c> — there is no base32/base64 variant;
/// malformed bodies, wrong lengths, unknown prefixes and empty input all
/// throw <see cref="DomainException"/>.
/// </summary>
public sealed class PublicIdParserTests
{
    private static readonly Guid Id = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid Other = Guid.Parse("99999999-8888-7777-6666-555555555555");

    private static readonly (string Prefix, Func<Guid, string> To, Func<string, Guid> Parse)[] Pairs =
    [
        ("prj_", PublicIdParser.ToProjectId, PublicIdParser.ParseProjectId),
        ("upl_", PublicIdParser.ToUploadId, PublicIdParser.ParseUploadId),
        ("run_", PublicIdParser.ToRunId, PublicIdParser.ParseRunId),
        ("rev_", PublicIdParser.ToReviewId, PublicIdParser.ParseReviewId),
        ("exp_", PublicIdParser.ToExportId, PublicIdParser.ParseExportId),
        ("seg_", PublicIdParser.ToSegmentId, PublicIdParser.ParseSegmentId),
        ("spk_", PublicIdParser.ToSpeakerId, PublicIdParser.ParseSpeakerId),
        ("voice_", PublicIdParser.ToVoiceId, PublicIdParser.ParseVoiceId),
        ("vpv_", PublicIdParser.ToPreviewId, PublicIdParser.ParsePreviewId),
        ("ntf_", PublicIdParser.ToNotificationId, PublicIdParser.ParseNotificationId),
    ];

    [Fact]
    public void Every_Prefix_Is_Covered()
    {
        Assert.Equal(10, Pairs.Length);
    }

    [Fact]
    public void Encode_Produces_Prefix_Plus_N_Hex()
    {
        foreach (var (prefix, to, _) in Pairs)
        {
            var encoded = to(Id);
            Assert.StartsWith(prefix, encoded, StringComparison.Ordinal);
            Assert.Equal(prefix + Id.ToString("N"), encoded);
            Assert.Equal(prefix.Length + 32, encoded.Length);
        }
    }

    [Fact]
    public void Round_Trip_Prefix_And_Raw_Guid()
    {
        foreach (var (prefix, to, parse) in Pairs)
        {
            var encoded = to(Id);
            Assert.Equal(Id, parse(encoded));

            // D format (with dashes).
            Assert.Equal(Id, parse(Id.ToString("D")));
            // N format (no dashes).
            Assert.Equal(Id, parse(Id.ToString("N")));
            // Surrounding whitespace is trimmed.
            Assert.Equal(Id, parse("  " + encoded + "\t"));
        }
    }

    [Fact]
    public void Round_Trip_Distinguishes_Different_Ids()
    {
        foreach (var (prefix, to, parse) in Pairs)
        {
            Assert.NotEqual(to(Id), to(Other));
            Assert.Equal(Other, parse(to(Other)));
            Assert.Equal(Id, parse(to(Id)));
        }
    }

    [Fact]
    public void Round_Trip_Is_Deterministic()
    {
        foreach (var (_, to, parse) in Pairs)
        {
            Assert.Equal(to(Id), to(Id));
            Assert.Equal(parse(to(Id)), parse(to(Id)));
        }
    }

    [Fact]
    public void Encode_Rejects_Empty_Guid()
    {
        foreach (var (_, to, _) in Pairs)
        {
            var ex = Assert.Throws<DomainException>(() => to(Guid.Empty));
            Assert.Equal("Id must not be empty.", ex.Message);
        }
    }

    [Theory]
    [InlineData(null, "project")]
    [InlineData("", "project")]
    [InlineData("   ", "project")]
    [InlineData("\t\n", "project")]
    [InlineData(null, "run")]
    [InlineData("", "segment")]
    [InlineData("  ", "voice")]
    [InlineData(null, "notification")]
    public void Parse_Rejects_Empty_Input(string? raw, string kind)
    {
        var ex = Assert.Throws<DomainException>(() => ParseFor(kind, raw));
        Assert.Equal($"{kind} id must not be empty.", ex.Message);
    }

    [Theory]
    [InlineData("prj_", "upload")]
    [InlineData("upl_", "project")]
    [InlineData("run_", "project")]
    [InlineData("rev_", "export")]
    [InlineData("exp_", "review")]
    [InlineData("seg_", "project")]
    [InlineData("spk_", "project")]
    [InlineData("voice_", "speaker")]
    [InlineData("vpv_", "voice")]
    [InlineData("ntf_", "project")]
    public void Parse_Rejects_An_Unexpected_Prefix(string usedPrefix, string kind)
    {
        var body = Id.ToString("N");
        var ex = Assert.Throws<DomainException>(() => ParseFor(kind, usedPrefix + body));

        Assert.Contains("unexpected prefix", ex.Message, StringComparison.Ordinal);
        Assert.Contains(usedPrefix, ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("prj_", "project")]
    [InlineData("run_", "run")]
    [InlineData("seg_", "segment")]
    [InlineData("voice_", "voice")]
    public void Parse_Rejects_A_Short_Body(string prefix, string kind)
    {
        Assert.Throws<DomainException>(() => ParseFor(kind, prefix + Id.ToString("N")[..31]));
    }

    [Theory]
    [InlineData("prj_", "project")]
    [InlineData("run_", "run")]
    [InlineData("seg_", "segment")]
    public void Parse_Rejects_A_Long_Body(string prefix, string kind)
    {
        Assert.Throws<DomainException>(() => ParseFor(kind, prefix + Id.ToString("N") + "00"));
    }

    [Theory]
    [InlineData("prj_", "project")]
    [InlineData("run_", "run")]
    [InlineData("spk_", "speaker")]
    public void Parse_Rejects_A_Non_Hex_Body(string prefix, string kind)
    {
        // Base32/base64 style bodies are not accepted: the body must be 32 hex.
        Assert.Throws<DomainException>(() => ParseFor(kind, prefix + new string('z', 32)));
        Assert.Throws<DomainException>(() => ParseFor(kind, prefix + "not-base32-AAAA-AAAA-AAAA-AAAAAAAAA"));
        Assert.Throws<DomainException>(() => ParseFor(kind, prefix + "YWJjZGVmZ2hpamtsbW5vcHFyc3R1dnd4eXo="));
    }

    [Theory]
    [InlineData("prj_", "project")]
    [InlineData("run_", "run")]
    [InlineData("exp_", "export")]
    public void Parse_Rejects_An_All_Zero_Body_Which_Is_The_Empty_Guid(string prefix, string kind)
    {
        var ex = Assert.Throws<DomainException>(() => ParseFor(kind, prefix + new string('0', 32)));
        Assert.Contains("invalid identifier body", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("zzz_0123456789abcdef0123456789abcdef", "project")]
    [InlineData("PRJ_0123456789abcdef0123456789abcdef", "project")]
    [InlineData("Prj_0123456789abcdef0123456789abcdef", "project")]
    public void Parse_Rejects_An_Unknown_Prefix(string raw, string kind)
    {
        var ex = Assert.Throws<DomainException>(() => ParseFor(kind, raw));

        Assert.Equal($"{kind} id '{raw}' is not a valid identifier.", ex.Message);
    }

    [Fact]
    public void Parse_Rejects_A_Bare_All_Zero_Guid()
    {
        var zeros = "00000000-0000-0000-0000-000000000000";

        var ex = Assert.Throws<DomainException>(() => PublicIdParser.ParseProjectId(zeros));
        Assert.Equal($"project id '{zeros}' is not a valid identifier.", ex.Message);
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("12345")]
    [InlineData("prj")]
    [InlineData("prj_")]
    [InlineData("_")]
    [InlineData("....")]
    [InlineData("' OR 1=1 --")]
    public void Parse_Rejects_Garbage(string raw)
    {
        Assert.Throws<DomainException>(() => PublicIdParser.ParseProjectId(raw));
    }

    [Fact]
    public void Parse_Accepts_Other_Known_Prefixes_As_Raw_Input_Only_For_Its_Own_Kind()
    {
        // tenant_ is a known prefix but not one the project parser accepts.
        var tenant = Guid.NewGuid();
        var raw = PublicIdMapper.ToPublic(tenant, PublicIdMapper.TenantPrefix);

        var ex = Assert.Throws<DomainException>(() => PublicIdParser.ParseProjectId(raw));
        Assert.Contains("unexpected prefix 'tenant_'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Malformed_Body_Message_Is_Bounded_And_Names_The_Prefix()
    {
        var ex = Assert.Throws<DomainException>(() => PublicIdParser.ParseProjectId("prj_" + new string('g', 32)));
        Assert.Contains("prj_", ex.Message, StringComparison.Ordinal);
        Assert.Contains("invalid identifier body", ex.Message, StringComparison.Ordinal);
        Assert.True(ex.Message.Length < 200, $"message was {ex.Message.Length} chars");
    }

    [Fact]
    public void Prefixes_Are_All_Registered_With_The_Domain_Mapper()
    {
        foreach (var (prefix, _, _) in Pairs)
        {
            Assert.Contains(prefix, PublicIdMapper.AllPrefixes);
        }
    }

    private static Guid ParseFor(string kind, string? raw)
    {
        return kind switch
        {
            "project" => PublicIdParser.ParseProjectId(raw),
            "upload" => PublicIdParser.ParseUploadId(raw),
            "run" => PublicIdParser.ParseRunId(raw),
            "review" => PublicIdParser.ParseReviewId(raw),
            "export" => PublicIdParser.ParseExportId(raw),
            "segment" => PublicIdParser.ParseSegmentId(raw),
            "speaker" => PublicIdParser.ParseSpeakerId(raw),
            "voice" => PublicIdParser.ParseVoiceId(raw),
            "preview" => PublicIdParser.ParsePreviewId(raw),
            "notification" => PublicIdParser.ParseNotificationId(raw),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "unknown kind"),
        };
    }
}
