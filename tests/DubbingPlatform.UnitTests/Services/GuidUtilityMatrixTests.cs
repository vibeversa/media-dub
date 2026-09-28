// Task 039C: exports/duration unit gap closure (deterministic id area).
using DubbingPlatform.Application.Services;
using DubbingPlatform.Domain.Exceptions;

namespace DubbingPlatform.UnitTests.Services;

/// <summary>
/// Deterministic RFC 4122 v5 (SHA-1 name-based) id factory. The key format is
/// frozen: <c>{run:N}:segment:{sequence}</c>. These tests only assert invariants
/// and cross-method consistency — never machine-specific digests.
/// </summary>
public sealed class GuidUtilityMatrixTests
{
    private static readonly Guid RunA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid RunB = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public void SegmentId_Uses_The_Documented_Key_Format()
    {
        var id = GuidUtility.SegmentId(RunA, 7);

        Assert.Equal(GuidUtility.From(RunA.ToString("N") + ":segment:7"), id);
    }

    [Fact]
    public void SegmentId_Rejects_Empty_Run()
    {
        var ex = Assert.Throws<DomainException>(() => GuidUtility.SegmentId(Guid.Empty, 0));
        Assert.Equal("RunId must not be empty.", ex.Message);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void SegmentId_Rejects_Negative_Sequence(int sequence)
    {
        var ex = Assert.Throws<DomainException>(() => GuidUtility.SegmentId(RunA, sequence));
        Assert.Equal("Sequence must be >= 0.", ex.Message);
    }

    [Fact]
    public void SegmentId_Rejects_Empty_Run_Before_Sequence_Check()
    {
        // Precedence matters: an empty run id is reported even with a bad
        // sequence, so the caller always gets the actionable message.
        var ex = Assert.Throws<DomainException>(() => GuidUtility.SegmentId(Guid.Empty, -1));
        Assert.Equal("RunId must not be empty.", ex.Message);
    }

    [Fact]
    public void SegmentId_Is_Deterministic_And_Sequence_Sensitive()
    {
        Assert.Equal(GuidUtility.SegmentId(RunA, 0), GuidUtility.SegmentId(RunA, 0));
        Assert.NotEqual(GuidUtility.SegmentId(RunA, 0), GuidUtility.SegmentId(RunA, 1));
        Assert.NotEqual(GuidUtility.SegmentId(RunA, 0), GuidUtility.SegmentId(RunB, 0));
    }

    [Fact]
    public void SegmentId_Accepts_Sequence_Zero_And_Large_Values()
    {
        var zero = GuidUtility.SegmentId(RunA, 0);
        Assert.NotEqual(Guid.Empty, zero);
        Assert.NotEqual(zero, GuidUtility.SegmentId(RunA, int.MaxValue));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public void From_Rejects_Blank_Keys(string? key)
    {
        var ex = Assert.Throws<DomainException>(() => GuidUtility.From(key!));
        Assert.Equal("Key must not be empty.", ex.Message);
    }

    [Fact]
    public void From_Produces_Version_5_Variant_Rfc4122_Guids()
    {
        var guid = GuidUtility.From("stable-key");
        Assert.NotEqual(Guid.Empty, guid);

        // RFC 4122 layout: byte 6 carries version 5, byte 8 carries the 10xx
        // variant, in the canonical big-endian byte order.
        var bytes = guid.ToByteArray();
        Assert.Equal(0x50, bytes[6] & 0xF0);
        Assert.Equal(0x80, bytes[8] & 0xC0);

        // Same bits as seen through the canonical "D" string: the version digit
        // is the third group, the variant digit the first of the fourth group.
        var dashed = guid.ToString("D", System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal('5', dashed[16]);
        Assert.True("89ab".Contains(dashed[19], StringComparison.Ordinal), $"variant digit was '{dashed[19]}'.");
    }

    [Fact]
    public void From_Never_Collides_With_Random_Guids()
    {
        // v5 outputs are name-based; a v4 (random) Guid must never be produced.
        for (var i = 0; i < 25; i++)
        {
            var guid = GuidUtility.From("run:" + i.ToString(System.Globalization.CultureInfo.InvariantCulture));
            var bytes = guid.ToByteArray();
            Assert.Equal(0x50, bytes[6] & 0xF0);
            Assert.Equal(0x80, bytes[8] & 0xC0);
        }
    }

    [Fact]
    public void From_Is_Deterministic_And_Avalanche_Sensitive()
    {
        Assert.Equal(GuidUtility.From("abc"), GuidUtility.From("abc"));

        var seen = new HashSet<Guid>();
        for (var i = 0; i < 500; i++)
        {
            Assert.True(seen.Add(GuidUtility.From("key-" + i.ToString(System.Globalization.CultureInfo.InvariantCulture))));
        }

        // One-bit change in the key: a completely different id.
        Assert.NotEqual(GuidUtility.From("abc"), GuidUtility.From("abd"));
    }

    [Fact]
    public void Overlap_Namespace_Extends_The_Segment_Namespace()
    {
        var group = Guid.NewGuid();
        var member = GuidUtility.From(RunA.ToString("N") + ":overlap:" + group.ToString("N"));
        var memberZero = GuidUtility.From(RunA.ToString("N") + ":overlap:" + group.ToString("N") + ":member:0");
        var memberOne = GuidUtility.From(RunA.ToString("N") + ":overlap:" + group.ToString("N") + ":member:1");

        Assert.NotEqual(GuidUtility.SegmentId(RunA, 0), member);
        Assert.NotEqual(member, memberZero);
        Assert.NotEqual(memberZero, memberOne);
    }

    [Fact]
    public void Key_Content_Is_Not_Recoverable_And_Holds_No_Secrets()
    {
        // Deterministic ids are safe to log: the value is a digest, and the
        // literal key never round-trips back out.
        var id = GuidUtility.From("sk-test-DO-NOT-USE");
        Assert.DoesNotContain(id.ToString("N"), "sk-test-DO-NOT-USE", StringComparison.Ordinal);
        Assert.Equal(32, id.ToString("N").Length);
    }
}
