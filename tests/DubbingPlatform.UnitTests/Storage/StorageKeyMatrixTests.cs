// Task 039C: exports unit gap closure (storage / signed-URL area).
using System.Security.Cryptography;
using System.Text;
using DubbingPlatform.Api.Services;
using DubbingPlatform.Application.Errors;
using DubbingPlatform.Application.Exceptions;
using DubbingPlatform.Application.Storage;
using DubbingPlatform.Domain.Exceptions;

namespace DubbingPlatform.UnitTests.Storage;

/// <summary>
/// Complement to <see cref="StorageKeyBuilderTests"/>: the remaining
/// <c>BuildKey</c> validation branches, extension normalization, key
/// validation limits, and the tenant-isolation property.
/// </summary>
public sealed class StorageKeyMatrixTests
{
    private static readonly Guid TenantA = Guid.Parse("aaaaaaaa-1111-1111-1111-111111111111");
    private static readonly Guid TenantB = Guid.Parse("bbbbbbbb-2222-2222-2222-222222222222");
    private static readonly Guid Project = Guid.Parse("cccccccc-3333-3333-3333-333333333333");
    private static readonly Guid Run = Guid.Parse("dddddddd-4444-4444-4444-444444444444");

    private const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private static string Key(
        Guid tenant,
        string? extension = ".mp4",
        string stage = "Render",
        string artifact = "RenderedOutput")
        => StorageKeyBuilder.BuildKey(tenant, Project, Run, stage, artifact, Hash, extension);

    [Fact]
    public void BuildKey_Trims_Stage_And_Artifact_But_Keeps_Their_Case()
    {
        var key = StorageKeyBuilder.BuildKey(
            TenantA, Project, Run, "  Render  ", "  RenderedOutput  ", Hash, ".mp4");

        Assert.Equal(Key(TenantA), key);
        Assert.Contains("/Render/RenderedOutput/", key, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildKey_Rejects_Empty_Project()
    {
        var ex = Assert.Throws<DomainException>(
            () => StorageKeyBuilder.BuildKey(TenantA, Guid.Empty, Run, "Render", "Audio", Hash, ".mp3"));
        Assert.Equal("ProjectId must not be empty.", ex.Message);
    }

    [Fact]
    public void BuildKey_Rejects_Empty_Run()
    {
        var ex = Assert.Throws<DomainException>(
            () => StorageKeyBuilder.BuildKey(TenantA, Project, Guid.Empty, "Render", "Audio", Hash, ".mp3"));
        Assert.Equal("ProcessingRunId must not be empty.", ex.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BuildKey_Rejects_Blank_Stage(string? stage)
    {
        var ex = Assert.Throws<DomainException>(
            () => StorageKeyBuilder.BuildKey(TenantA, Project, Run, stage!, "Audio", Hash, ".mp3"));
        Assert.Equal("StageType must not be empty.", ex.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BuildKey_Rejects_Blank_Artifact(string? artifact)
    {
        var ex = Assert.Throws<DomainException>(
            () => StorageKeyBuilder.BuildKey(TenantA, Project, Run, "Render", artifact!, Hash, ".mp3"));
        Assert.Equal("ArtifactType must not be empty.", ex.Message);
    }

    [Theory]
    [InlineData("Render/../../escape")]
    [InlineData("Ren/der")]
    public void BuildKey_Rejects_Slash_In_Stage(string stage)
    {
        var ex = Assert.Throws<DomainException>(
            () => StorageKeyBuilder.BuildKey(TenantA, Project, Run, stage, "Audio", Hash, ".mp3"));
        Assert.Equal("StageType and ArtifactType must not contain '/'.", ex.Message);
    }

    [Theory]
    [InlineData("Audio/../escape")]
    [InlineData("Au/dio")]
    public void BuildKey_Rejects_Slash_In_Artifact(string artifact)
    {
        var ex = Assert.Throws<DomainException>(
            () => StorageKeyBuilder.BuildKey(TenantA, Project, Run, "Render", artifact, Hash, ".mp3"));
        Assert.Equal("StageType and ArtifactType must not contain '/'.", ex.Message);
    }

    [Fact]
    public void BuildKey_Allows_A_Null_Or_Empty_Extension()
    {
        var withNull = Key(TenantA, extension: null);
        var withEmpty = Key(TenantA, extension: string.Empty);

        Assert.Equal(withNull, withEmpty);
        Assert.EndsWith(Hash, withNull, StringComparison.Ordinal);
        // No trailing separator is added when there is no extension.
        Assert.Equal(6, withNull.Split('/').Length);
    }

    [Fact]
    public void BuildKey_Rejects_Hash_That_Is_Not_Lower_Hex_64()
    {
        Assert.Throws<DomainException>(() => StorageKeyBuilder.BuildKey(
            TenantA, Project, Run, "Render", "Audio", new string('A', 64), ".mp3"));
        Assert.Throws<DomainException>(() => StorageKeyBuilder.BuildKey(
            TenantA, Project, Run, "Render", "Audio", new string('a', 63), ".mp3"));
        Assert.Throws<DomainException>(() => StorageKeyBuilder.BuildKey(
            TenantA, Project, Run, "Render", "Audio", null!, ".mp3"));
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData(".json", ".json")]
    [InlineData(".mp4", ".mp4")]
    [InlineData(".a", ".a")]
    [InlineData(".", ".")]
    [InlineData("." + "abcdefghijklmno", "." + "abcdefghijklmno")]
    public void NormalizeExtension_Accepts(string? input, string expected)
    {
        Assert.Equal(expected, StorageKeyBuilder.NormalizeExtension(input));
    }

    [Theory]
    [InlineData("json")]
    [InlineData("mp4")]
    [InlineData("tar.gz")]
    [InlineData(".JSON")]
    [InlineData(".mp-4")]
    [InlineData("..txt")]
    [InlineData(".abc/def")]
    [InlineData("." + "abcdefghijklmnop")]
    public void NormalizeExtension_Rejects(string input)
    {
        Assert.Throws<DomainException>(() => StorageKeyBuilder.NormalizeExtension(input));
    }

    [Fact]
    public void NormalizeExtension_Length_Boundaries()
    {
        // 16 chars total is the cap; 17 is one over.
        Assert.Equal(".abcdefghijklmno", StorageKeyBuilder.NormalizeExtension(".abcdefghijklmno"));
        Assert.Throws<DomainException>(() => StorageKeyBuilder.NormalizeExtension(".abcdefghijklmnop"));
    }

    [Fact]
    public void IsLowerHex64_Accepts_Only_64_Lower_Hex_Chars()
    {
        Assert.True(StorageKeyBuilder.IsLowerHex64(Hash));
        Assert.True(StorageKeyBuilder.IsLowerHex64(new string('0', 64)));
        Assert.True(StorageKeyBuilder.IsLowerHex64(new string('f', 64)));

        Assert.False(StorageKeyBuilder.IsLowerHex64(null));
        Assert.False(StorageKeyBuilder.IsLowerHex64(string.Empty));
        Assert.False(StorageKeyBuilder.IsLowerHex64(new string('a', 63)));
        Assert.False(StorageKeyBuilder.IsLowerHex64(new string('a', 65)));
        Assert.False(StorageKeyBuilder.IsLowerHex64(new string('A', 64)));
        Assert.False(StorageKeyBuilder.IsLowerHex64("g" + new string('0', 63)));
        Assert.False(StorageKeyBuilder.IsLowerHex64(Hash[..63] + "-"));
        Assert.False(StorageKeyBuilder.IsLowerHex64(Hash[..63] + " "));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ValidateKey_Rejects_Blank(string? key)
    {
        var ex = Assert.Throws<DomainException>(() => StorageKeyBuilder.ValidateKey(key!));
        Assert.Equal("StorageKey must not be empty.", ex.Message);
    }

    [Fact]
    public void ValidateKey_Rejects_Traversal_And_Backslash()
    {
        Assert.Throws<DomainException>(() => StorageKeyBuilder.ValidateKey("a/../b"));
        Assert.Throws<DomainException>(() => StorageKeyBuilder.ValidateKey("a\\b"));
    }

    [Fact]
    public void ValidateKey_Accepts_Tenant_Prefixed_Keys()
    {
        StorageKeyBuilder.ValidateKey(Key(TenantA));
        StorageKeyBuilder.ValidateKey("a/b");
    }

    [Fact]
    public void ValidateKey_Rejects_Keys_Longer_Than_1024()
    {
        var atLimit = "a/" + new string('b', 1022);
        Assert.Equal(1024, atLimit.Length);
        StorageKeyBuilder.ValidateKey(atLimit);

        var overLimit = "a/" + new string('b', 1023);
        var ex = Assert.Throws<DomainException>(() => StorageKeyBuilder.ValidateKey(overLimit));
        Assert.Equal("StorageKey must be at most 1024 chars.", ex.Message);
    }

    [Fact]
    public void Tenant_Isolation_Keys_Differ_Only_In_The_Tenant_Segment()
    {
        var keyA = Key(TenantA);
        var keyB = Key(TenantB);

        Assert.NotEqual(keyA, keyB);
        Assert.StartsWith(TenantA.ToString("N") + "/", keyA, StringComparison.Ordinal);
        Assert.StartsWith(TenantB.ToString("N") + "/", keyB, StringComparison.Ordinal);
        Assert.False(keyA.StartsWith(TenantB.ToString("N") + "/", StringComparison.Ordinal));
        Assert.False(keyB.StartsWith(TenantA.ToString("N") + "/", StringComparison.Ordinal));
        Assert.Equal(
            keyA.Substring((TenantA.ToString("N") + "/").Length),
            keyB.Substring((TenantB.ToString("N") + "/").Length));
    }

    [Fact]
    public void Tenant_Isolation_Leading_Segment_Is_Exactly_The_Tenant()
    {
        var key = Key(TenantA);
        var firstSegment = key.Split('/', 2)[0];

        Assert.Equal(TenantA.ToString("N"), firstSegment);
        Assert.Equal(32, firstSegment.Length);
    }

    [Fact]
    public void Tenant_Isolation_Key_Count_Is_Exactly_Six_Segments()
    {
        Assert.Equal(6, Key(TenantA).Split('/').Length);
    }

    [Fact]
    public void Built_Keys_Always_Validate()
    {
        StorageKeyBuilder.ValidateKey(Key(TenantA));
        StorageKeyBuilder.ValidateKey(Key(TenantB, extension: null));
    }
}

/// <summary>
/// Signed-URL expiry policy: the 1 minute .. 1 hour window, both as a
/// validating record and as a resolving clamp.
/// </summary>
public sealed class SignedUrlPolicyMatrixTests
{
    [Fact]
    public void Constants_Are_Frozen()
    {
        Assert.Equal(TimeSpan.FromMinutes(15), SignedUrlPolicy.DefaultExpiry);
        Assert.Equal(TimeSpan.FromHours(1), SignedUrlPolicy.MaxExpiry);
        Assert.Equal(TimeSpan.FromMinutes(15), SignedUrlService.DefaultExpiry);
        Assert.Equal(TimeSpan.FromMinutes(15), StoragePresignedUrls.DefaultExpiry);
    }

    [Fact]
    public void Default_Policy_Uses_The_Default_Expiry()
    {
        Assert.Equal(SignedUrlPolicy.DefaultExpiry, SignedUrlPolicy.Default.Expiry);
        Assert.Equal(SignedUrlPolicy.Default, SignedUrlPolicy.Default);
    }

    [Theory]
    [InlineData(60)]
    [InlineData(61)]
    [InlineData(900)]
    [InlineData(3599)]
    [InlineData(3600)]
    public void Validate_Accepts_The_Allowed_Window(int seconds)
    {
        new SignedUrlPolicy(TimeSpan.FromSeconds(seconds)).Validate();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(59)]
    [InlineData(3601)]
    [InlineData(7200)]
    [InlineData(-60)]
    public void Validate_Rejects_Outside_The_Window(int seconds)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => new SignedUrlPolicy(TimeSpan.FromSeconds(seconds)).Validate());
        Assert.Equal("Expiry", ex.ParamName);
    }

    [Fact]
    public void Resolve_Defaults_When_Null()
    {
        Assert.Equal(TimeSpan.FromMinutes(15), SignedUrlPolicy.Resolve(null));
    }

    [Theory]
    [InlineData(0, 60)]
    [InlineData(-1, 60)]
    [InlineData(1, 60)]
    [InlineData(59, 60)]
    [InlineData(60, 60)]
    [InlineData(61, 61)]
    [InlineData(900, 900)]
    [InlineData(3599, 3599)]
    [InlineData(3600, 3600)]
    [InlineData(3601, 3600)]
    [InlineData(86400, 3600)]
    [InlineData(-86400, 60)]
    public void Resolve_Clamps_To_The_Window(int requestedSeconds, int expectedSeconds)
    {
        var resolved = SignedUrlPolicy.Resolve(TimeSpan.FromSeconds(requestedSeconds));

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), resolved);
        // A resolved expiry is always a valid policy.
        new SignedUrlPolicy(resolved).Validate();
    }

    [Fact]
    public void Policy_Is_A_Value_Record()
    {
        Assert.Equal(new SignedUrlPolicy(TimeSpan.FromMinutes(15)), SignedUrlPolicy.Default);
        Assert.NotEqual(new SignedUrlPolicy(TimeSpan.FromMinutes(1)), SignedUrlPolicy.Default);
    }
}

/// <summary>
/// <see cref="SignedUrlService"/> takes a raw HMAC key (no storage client), so
/// the whole issue/validate envelope is exercised hermetically. Every call
/// passes an explicit <c>now</c> — no clock reads, no waits, no network.
/// </summary>
public sealed class SignedUrlServiceMatrixTests
{
    private static readonly Guid TenantA = Guid.Parse("aaaaaaaa-1111-1111-1111-111111111111");
    private static readonly Guid TenantB = Guid.Parse("bbbbbbbb-2222-2222-2222-222222222222");
    private static readonly Guid ProjectA = Guid.Parse("cccccccc-3333-3333-3333-333333333333");
    private static readonly Guid ProjectB = Guid.Parse("dddddddd-4444-4444-4444-444444444444");
    private static readonly Guid Artifact = Guid.Parse("eeeeeeee-5555-5555-5555-555555555555");

    /// <summary>Fixed test-only key. Not a real secret and never logged.</summary>
    private static readonly byte[] Key = Encoding.UTF8.GetBytes("unit-test-signing-key-DO-NOT-USE-0001");

    private static readonly DateTimeOffset Now = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

    private static SignedUrlService Service() => new(Key);

    [Fact]
    public void Signing_Key_Is_At_Least_32_Bytes()
    {
        Assert.True(Key.Length >= 32);
    }

    [Fact]
    public void Constructor_Rejects_Null_Key()
    {
        Assert.Throws<ArgumentNullException>(() => new SignedUrlService(null!));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(31)]
    public void Constructor_Rejects_Short_Key(int length)
    {
        var ex = Assert.Throws<ArgumentException>(() => new SignedUrlService(new byte[length]));
        Assert.Contains("at least 32 bytes", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Constructor_Clones_The_Key_So_Later_Mutation_Cannot_Change_It()
    {
        var mutable = new byte[32];
        Array.Fill(mutable, (byte)1);
        var service = new SignedUrlService(mutable);

        // Mutating the caller's array must not change the effective key, so a
        // token issued *after* the mutation still validates.
        Array.Fill(mutable, (byte)99);
        var issued = service.Issue(TenantA, ProjectA, Artifact, Now);
        var validated = service.Validate(issued.Token, TenantA, ProjectA, Now);

        Assert.Equal(Artifact, validated.ArtifactId);
        // And the mutated key does not work for a second service.
        Assert.Throws<ForbiddenException>(
            () => new SignedUrlService(mutable).Validate(issued.Token, TenantA, ProjectA, Now));
    }

    [Fact]
    public void Issue_Uses_The_Default_Expiry_When_None_Requested()
    {
        var issued = Service().Issue(TenantA, ProjectA, Artifact, Now);

        Assert.Equal(Now.AddMinutes(15), issued.ExpiresAt);
        Assert.Equal(TenantA, issued.TenantId);
        Assert.Equal(ProjectA, issued.ProjectId);
        Assert.Equal(Artifact, issued.ArtifactId);
        var payload = issued.Token.Split('.')[0];
        Assert.EndsWith("." + SignFor(payload), issued.Token, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(59, 1)]
    [InlineData(60, 1)]
    [InlineData(900, 15)]
    [InlineData(3600, 60)]
    [InlineData(3601, 60)]
    [InlineData(86400, 60)]
    public void Issue_Clamps_The_Requested_Expiry(int requestedSeconds, int expectedMinutes)
    {
        var issued = Service().Issue(
            TenantA, ProjectA, Artifact, Now, TimeSpan.FromSeconds(requestedSeconds));

        Assert.Equal(Now.AddMinutes(expectedMinutes), issued.ExpiresAt);
    }

    [Fact]
    public void Issue_Rejects_Empty_Ids()
    {
        var service = Service();

        Assert.Equal(
            "TenantId must not be empty.",
            Assert.Throws<DomainException>(() => service.Issue(Guid.Empty, ProjectA, Artifact, Now)).Message);
        Assert.Equal(
            "ProjectId must not be empty.",
            Assert.Throws<DomainException>(() => service.Issue(TenantA, Guid.Empty, Artifact, Now)).Message);
        Assert.Equal(
            "ArtifactId must not be empty.",
            Assert.Throws<DomainException>(() => service.Issue(TenantA, ProjectA, Guid.Empty, Now)).Message);
    }

    [Fact]
    public void Issue_Then_Validate_Round_Trips()
    {
        var service = Service();
        var issued = service.Issue(TenantA, ProjectA, Artifact, Now);

        var validated = service.Validate(issued.Token, TenantA, ProjectA, Now);

        Assert.Equal(issued.Token, validated.Token);
        Assert.Equal(TenantA, validated.TenantId);
        Assert.Equal(ProjectA, validated.ProjectId);
        Assert.Equal(Artifact, validated.ArtifactId);
        Assert.Equal(issued.ExpiresAt, validated.ExpiresAt);
    }

    [Fact]
    public void Issue_Is_Deterministic_For_The_Same_Inputs()
    {
        var service = Service();

        Assert.Equal(
            service.Issue(TenantA, ProjectA, Artifact, Now).Token,
            service.Issue(TenantA, ProjectA, Artifact, Now).Token);
    }

    [Fact]
    public void Issue_Tokens_Embed_The_Tenant_Project_And_Artifact()
    {
        var issued = Service().Issue(TenantA, ProjectA, Artifact, Now);
        var parts = issued.Token.Split('.');
        var raw = Encoding.UTF8.GetString(Convert.FromBase64String(parts[0])).Split('|');

        Assert.Equal(4, raw.Length);
        Assert.Equal(TenantA.ToString("N"), raw[0]);
        Assert.Equal(ProjectA.ToString("N"), raw[1]);
        Assert.Equal(Artifact.ToString("N"), raw[2]);
        Assert.Equal(Now.AddMinutes(15).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture), raw[3]);
    }

    [Fact]
    public void Validate_Accepts_Just_Before_Expiry_And_Rejects_At_Expiry()
    {
        var service = Service();
        var issued = service.Issue(TenantA, ProjectA, Artifact, Now);
        var expiresAt = issued.ExpiresAt;

        // One second before expiry: still valid.
        var justBefore = service.Validate(issued.Token, TenantA, ProjectA, expiresAt.AddSeconds(-1));
        Assert.Equal(Artifact, justBefore.ArtifactId);

        // Exactly at expiry: rejected (now >= expiresAt).
        var atExpiry = Assert.Throws<ErrorCodeException>(
            () => service.Validate(issued.Token, TenantA, ProjectA, expiresAt));
        Assert.Equal(ErrorCodes.UrlExpired, atExpiry.ErrorCode);
        Assert.Equal(410, atExpiry.StatusCode);
    }

    [Fact]
    public void Validate_Rejects_Long_Expired_Tokens()
    {
        var service = Service();
        var issued = service.Issue(TenantA, ProjectA, Artifact, Now);

        foreach (var offset in new[] { TimeSpan.FromHours(1), TimeSpan.FromDays(1), TimeSpan.FromDays(365) })
        {
            var ex = Assert.Throws<ErrorCodeException>(
                () => service.Validate(issued.Token, TenantA, ProjectA, Now.Add(offset)));
            Assert.Equal(ErrorCodes.UrlExpired, ex.ErrorCode);
        }
    }

    [Fact]
    public void Validate_Rejects_A_Clock_Behind_The_Issuer_Skew()
    {
        // Token issued "in the future" relative to the validating clock: the
        // token is still inside its own window, so acceptance depends purely on
        // now < expiresAt (no tolerance band is added).
        var service = Service();
        var issued = service.Issue(TenantA, ProjectA, Artifact, Now);
        var beforeIssuance = Now.AddSeconds(-30);

        var validated = service.Validate(issued.Token, TenantA, ProjectA, beforeIssuance);
        Assert.Equal(Artifact, validated.ArtifactId);
    }

    [Fact]
    public void Validate_Rejects_A_Token_Issued_By_A_Different_Signing_Key()
    {
        var issuer = new SignedUrlService(Encoding.UTF8.GetBytes("another-unit-test-signing-key-DO-NOT!"));
        var issued = issuer.Issue(TenantA, ProjectA, Artifact, Now);

        var ex = Assert.Throws<ForbiddenException>(
            () => Service().Validate(issued.Token, TenantA, ProjectA, Now));
        Assert.Contains("signature mismatch", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_Rejects_Tampered_Signature()
    {
        var service = Service();
        var issued = service.Issue(TenantA, ProjectA, Artifact, Now);
        var parts = issued.Token.Split('.');
        var tampered = parts[0] + "." + parts[1][..^1] + (parts[1][^1] == 'A' ? 'B' : 'A');

        var ex = Assert.Throws<ForbiddenException>(() => service.Validate(tampered, TenantA, ProjectA, Now));
        Assert.Contains("signature mismatch", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_Rejects_A_Signature_Of_The_Wrong_Length()
    {
        var service = Service();
        var issued = service.Issue(TenantA, ProjectA, Artifact, Now);
        var parts = issued.Token.Split('.');
        var extended = parts[0] + "." + parts[1] + "AAAA";

        Assert.Throws<ForbiddenException>(() => service.Validate(extended, TenantA, ProjectA, Now));
    }

    [Fact]
    public void Validate_Rejects_A_Tampered_Payload()
    {
        var service = Service();
        var issued = service.Issue(TenantA, ProjectA, Artifact, Now);
        var parts = issued.Token.Split('.');

        // Swap the tenant in the payload but keep the original signature.
        var forgedRaw = string.Concat(TenantB.ToString("N"), "|", ProjectA.ToString("N"), "|", Artifact.ToString("N"), "|", Now.ToUnixTimeSeconds());
        var forged = Convert.ToBase64String(Encoding.UTF8.GetBytes(forgedRaw)) + "." + parts[1];

        var ex = Assert.Throws<ForbiddenException>(() => service.Validate(forged, TenantB, ProjectA, Now));
        Assert.Contains("signature mismatch", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_Rejects_Unknown_Tenant_With_401()
    {
        var service = Service();
        var issued = service.Issue(TenantA, ProjectA, Artifact, Now);

        Assert.Throws<UnauthorizedAccessException>(() => service.Validate(issued.Token, Guid.Empty, ProjectA, Now));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("no-dot-at-all")]
    [InlineData("a.b.c")]
    public void Validate_Rejects_Malformed_Tokens(string? token)
    {
        var ex = Assert.Throws<ForbiddenException>(() => Service().Validate(token, TenantA, ProjectA, Now));
        Assert.Equal("Signed URL is invalid.", ex.Message);
    }

    [Fact]
    public void Validate_Rejects_A_Bare_Separator_Token_As_A_Signature_Mismatch()
    {
        // "." splits into two empty parts, so it fails the signature check
        // rather than the shape check; both paths are forbidden.
        var ex = Assert.Throws<ForbiddenException>(() => Service().Validate(".", TenantA, ProjectA, Now));
        Assert.Contains("signature mismatch", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_Trims_Surrounding_Whitespace()
    {
        var service = Service();
        var issued = service.Issue(TenantA, ProjectA, Artifact, Now);

        var validated = service.Validate("  " + issued.Token + "\n", TenantA, ProjectA, Now);
        Assert.Equal(issued.Token, validated.Token);
    }

    [Theory]
    [InlineData("a|b|c")]
    [InlineData("a|b|c|d|e")]
    [InlineData("|")]
    public void Validate_Rejects_Correctly_Signed_But_Malformed_Payloads(string raw)
    {
        var service = Service();
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(raw));
        var token = payload + "." + SignFor(payload);

        var ex = Assert.Throws<ForbiddenException>(() => service.Validate(token, TenantA, ProjectA, Now));
        Assert.Contains("malformed", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_Rejects_Correctly_Signed_Payload_With_Bad_Guids()
    {
        var service = Service();
        var payload = Convert.ToBase64String(
            Encoding.UTF8.GetBytes("not-a-guid|" + ProjectA.ToString("N") + "|" + Artifact.ToString("N") + "|1000"));
        var token = payload + "." + SignFor(payload);

        var ex = Assert.Throws<ForbiddenException>(() => service.Validate(token, TenantA, ProjectA, Now));
        Assert.Contains("malformed", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Tenant_Isolation_A_Token_Issued_For_A_Never_Validates_For_B()
    {
        var service = Service();
        var issuedForA = service.Issue(TenantA, ProjectA, Artifact, Now);

        var ex = Assert.Throws<ForbiddenException>(
            () => service.Validate(issuedForA.Token, TenantB, ProjectA, Now));
        Assert.Equal("Signed URL does not belong to the current tenant/project.", ex.Message);

        var ex2 = Assert.Throws<ForbiddenException>(
            () => service.Validate(issuedForA.Token, TenantA, ProjectB, Now));
        Assert.Equal("Signed URL does not belong to the current tenant/project.", ex2.Message);

        // The owning tenant still succeeds.
        Assert.Equal(Artifact, service.Validate(issuedForA.Token, TenantA, ProjectA, Now).ArtifactId);
    }

    [Fact]
    public void Tenant_Isolation_Expiry_Is_Checked_Before_Tenancy_But_Claims_Are_Private()
    {
        // Even for the wrong tenant, an expired token reveals nothing but "gone".
        var service = Service();
        var issued = service.Issue(TenantA, ProjectA, Artifact, Now, TimeSpan.FromMinutes(1));

        var ex = Assert.Throws<ErrorCodeException>(
            () => service.Validate(issued.Token, TenantB, ProjectA, Now.AddMinutes(30)));
        Assert.Equal(ErrorCodes.UrlExpired, ex.ErrorCode);
        Assert.DoesNotContain(TenantA.ToString("N", System.Globalization.CultureInfo.InvariantCulture), ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Project_Archived_Blocks_Use_Of_A_Valid_Token()
    {
        var service = Service();
        var issued = service.Issue(TenantA, ProjectA, Artifact, Now);

        var ex = Assert.Throws<ErrorCodeException>(
            () => service.Validate(issued.Token, TenantA, ProjectA, Now, projectArchived: true));
        Assert.Equal(ErrorCodes.ProjectArchived, ex.ErrorCode);
        Assert.Equal(409, ex.StatusCode);
    }

    [Fact]
    public void Project_Archived_Is_Checked_After_Tenancy()
    {
        var service = Service();
        var issued = service.Issue(TenantA, ProjectA, Artifact, Now);

        Assert.Throws<ForbiddenException>(
            () => service.Validate(issued.Token, TenantB, ProjectA, Now, projectArchived: true));
    }

    [Fact]
    public void Issuance_Cache_Control_Is_No_Store()
    {
        Assert.Equal("private, no-store", SignedUrlService.IssuanceCacheControl);
    }

    [Fact]
    public void Token_Carries_No_Plaintext_Secret_Material()
    {
        var issued = Service().Issue(TenantA, ProjectA, Artifact, Now);
        var payload = issued.Token.Split('.')[0];

        // The payload is base64 over ids only; the signing key never appears.
        var raw = Encoding.UTF8.GetString(Convert.FromBase64String(payload));
        Assert.DoesNotContain("unit-test-signing-key", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("unit-test-signing-key", issued.Token, StringComparison.Ordinal);
    }

    private static string SignFor(string payload)
    {
        using var hmac = new HMACSHA256(Key);
        return Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload)));
    }
}
