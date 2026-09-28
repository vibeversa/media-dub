// Task 039C: exports/duration unit gap closure (application services area).
using DubbingPlatform.Application.Services;
using DubbingPlatform.Domain.Exceptions;

namespace DubbingPlatform.UnitTests.Services;

/// <summary>
/// Deterministic duration math: per-language character costs, the 300..30000ms
/// clamp, prosody ratio clamping, SSML escaping and SHA-256 hashing. All pure —
/// no clock, no network, no providers.
/// </summary>
public sealed class DurationEstimatorMatrixTests
{
    [Fact]
    public void Constants_Are_Frozen()
    {
        Assert.Equal(300, DurationEstimator.MinMs);
        Assert.Equal(30000, DurationEstimator.MaxMs);
        Assert.Equal(0.85, DurationEstimator.MinRate);
        Assert.Equal(1.15, DurationEstimator.MaxRate);
        Assert.Equal("tts-ssml-v1", DurationEstimator.SsmlTemplateId);
    }

    [Theory]
    [InlineData("en", 70)]
    [InlineData("EN", 70)]
    [InlineData("  en  ", 70)]
    [InlineData("es", 75)]
    [InlineData("ES-mx", 75)]
    [InlineData("de", 80)]
    [InlineData("DE", 80)]
    [InlineData("fr", 75)]
    [InlineData("Fr", 75)]
    [InlineData("zz", 75)]
    [InlineData("", 75)]
    [InlineData("   ", 75)]
    [InlineData(null, 75)]
    public void PerCharMsFor_Table(string? language, int expected)
    {
        Assert.Equal(expected, DurationEstimator.PerCharMsFor(language));
    }

    [Fact]
    public void Estimate_Null_Text_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => DurationEstimator.EstimateMs(null!, "en"));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void Estimate_Non_Finite_Or_Non_Positive_Rate_Throws(double rate)
    {
        var ex = Assert.Throws<DomainException>(() => DurationEstimator.EstimateMs("hello", "en", rate));
        Assert.Equal("Rate must be finite and > 0.", ex.Message);
    }

    [Fact]
    public void Estimate_Negative_Zero_Rate_Is_Rejected()
    {
        // -0.0 <= 0.0 is true, so the negative-zero rate is refused as well.
        Assert.Throws<DomainException>(() => DurationEstimator.EstimateMs("hello", "en", -0.0));
    }

    [Fact]
    public void Estimate_Empty_Text_Hits_The_Minimum_Floor()
    {
        Assert.Equal(DurationEstimator.MinMs, DurationEstimator.EstimateMs(string.Empty, "en"));
        Assert.Equal(DurationEstimator.MinMs, DurationEstimator.EstimateMs(string.Empty, "de", 1.15));
    }

    [Theory]
    [InlineData("a", 300)]
    [InlineData("abcd", 300)]
    [InlineData("abcde", 350)]
    [InlineData("abcdefghij", 700)]
    public void Estimate_Language_Table_And_Floor(string text, int expected)
    {
        Assert.Equal(expected, DurationEstimator.EstimateMs(text, "en"));
    }

    [Fact]
    public void Estimate_German_Is_Slower_Than_English_For_The_Same_Text()
    {
        const string Text = "Guten Tag, wie geht es Ihnen heute?";
        Assert.Equal(
            Text.Length * DurationEstimator.PerCharMsFor("de"),
            DurationEstimator.EstimateMs(Text, "de"));
        Assert.True(
            DurationEstimator.EstimateMs(Text, "de") > DurationEstimator.EstimateMs(Text, "en"));
        Assert.Equal(DurationEstimator.EstimateMs(Text, "es"), DurationEstimator.EstimateMs(Text, "fr"));
    }

    [Fact]
    public void Estimate_Rate_Divides_And_Rounds_Away_From_Zero()
    {
        Assert.Equal(350, DurationEstimator.EstimateMs("abcde", "en", 1.0));
        Assert.Equal(700, DurationEstimator.EstimateMs("abcde", "en", 0.5));
        // 350 / 1.15 = 304.34 -> 304 ; 350 / 0.85 = 411.76 -> 412
        Assert.Equal(304, DurationEstimator.EstimateMs("abcde", "en", 1.15));
        Assert.Equal(412, DurationEstimator.EstimateMs("abcde", "en", 0.85));
    }

    [Fact]
    public void Estimate_Speeding_Up_Falls_Below_The_Floor_For_Short_Text()
    {
        // 5 chars * 70 / 2.0 = 175ms, below MinMs, so the floor applies.
        Assert.Equal(350, DurationEstimator.EstimateMs("abcdefghij", "en", 2.0));
        Assert.Equal(DurationEstimator.MinMs, DurationEstimator.EstimateMs("abcde", "en", 2.0));
        Assert.Equal(DurationEstimator.MinMs, DurationEstimator.EstimateMs("abcdefghij", "en", 5.0));
    }

    [Fact]
    public void Estimate_Clamps_To_Maximum()
    {
        // 429 * 70 = 30030 -> clamped; 428 * 70 = 29960 -> untouched.
        Assert.Equal(29_960, DurationEstimator.EstimateMs(new string('x', 428), "en"));
        Assert.Equal(DurationEstimator.MaxMs, DurationEstimator.EstimateMs(new string('x', 429), "en"));
        Assert.Equal(DurationEstimator.MaxMs, DurationEstimator.EstimateMs(new string('x', 10_000), "de"));
    }

    [Fact]
    public void Estimate_Overflowing_Ratio_Falls_Back_To_Minimum()
    {
        // rate = double.Epsilon makes the intermediate ratio +Infinity, which is
        // handled explicitly (never cast to int) and yields the floor.
        Assert.Equal(
            DurationEstimator.MinMs,
            DurationEstimator.EstimateMs("abcde", "en", double.Epsilon));
        Assert.Equal(
            DurationEstimator.MinMs,
            DurationEstimator.EstimateMs(string.Empty, "en", double.Epsilon));
    }

    [Fact]
    public void Estimate_Tiny_Rate_Clamps_Instead_Of_Overflowing()
    {
        // 350 / 0.001 = 350000 -> far above the ceiling, still clamped.
        Assert.Equal(
            DurationEstimator.MaxMs,
            DurationEstimator.EstimateMs("abcde", "en", 0.001));
        Assert.Equal(
            DurationEstimator.MaxMs,
            DurationEstimator.EstimateMs(new string('x', 500), "en", 0.0001));
    }

    [Fact]
    public void Estimate_Ratio_Beyond_Int_Range_Stays_Inside_The_Documented_Window()
    {
        // 1e-12 makes the finite-but-astronomical ratio 3.5e14, which cannot be
        // represented as an int. Whatever the narrowing does, the contract is
        // that the caller only ever sees a value inside MinMs..MaxMs.
        foreach (var rate in new[] { 1e-12, 1e-300, 1e-308 })
        {
            var estimate = DurationEstimator.EstimateMs("abcde", "en", rate);
            Assert.InRange(estimate, DurationEstimator.MinMs, DurationEstimator.MaxMs);
        }
    }

    [Fact]
    public void Estimate_Is_Deterministic()
    {
        const string Text = "Deterministic duration estimates for dubbing.";
        var first = DurationEstimator.EstimateMs(Text, "es", 1.05);
        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(first, DurationEstimator.EstimateMs(Text, "es", 1.05));
        }
    }

    [Theory]
    [InlineData(0, 1000, 1.0)]
    [InlineData(-1, 1000, 1.0)]
    [InlineData(-500, 500, 1.0)]
    [InlineData(1000, 0, 0.85)]
    [InlineData(1000, 1, 0.85)]
    [InlineData(1000, 849, 0.85)]
    [InlineData(1000, 850, 0.85)]
    [InlineData(1000, 999, 0.999)]
    [InlineData(1000, 1000, 1.0)]
    [InlineData(1000, 1001, 1.001)]
    [InlineData(1000, 1150, 1.15)]
    [InlineData(1000, 1151, 1.15)]
    [InlineData(1000, 60_000, 1.15)]
    [InlineData(1, 60_000, 1.15)]
    [InlineData(1, 0, 0.85)]
    public void ComputeRate_Clamps_Ratio(int estimateMs, int targetMs, double expected)
    {
        Assert.Equal(expected, DurationEstimator.ComputeRate(estimateMs, targetMs), precision: 10);
    }

    [Fact]
    public void ComputeRate_Non_Positive_Target_Floors_Rather_Than_Going_Faster()
    {
        Assert.Equal(DurationEstimator.MinRate, DurationEstimator.ComputeRate(1000, -5000), precision: 10);
        Assert.Equal(DurationEstimator.MinRate, DurationEstimator.ComputeRate(1000, 0), precision: 10);
        Assert.Equal(DurationEstimator.MaxRate, DurationEstimator.ComputeRate(1000, int.MaxValue), precision: 10);
    }

    [Fact]
    public void ComputeRate_Is_Invertible_For_Aligned_Budgets()
    {
        // estimate == target means no prosody change.
        foreach (var ms in new[] { 0, 1, 999, 1000, 60_000, 30_000 })
        {
            var rate = DurationEstimator.ComputeRate(ms, ms);
            Assert.Equal(1.0, rate, precision: 10);
        }
    }

    [Fact]
    public void BuildSsml_Null_Text_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => DurationEstimator.BuildSsml(null!, 1.0));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-2.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void BuildSsml_Non_Finite_Or_Non_Positive_Rate_Throws(double rate)
    {
        var ex = Assert.Throws<DomainException>(() => DurationEstimator.BuildSsml("hello", rate));
        Assert.Equal("Rate must be finite and > 0.", ex.Message);
    }

    [Fact]
    public void BuildSsml_Negative_Zero_Rate_Is_Rejected()
    {
        Assert.Throws<DomainException>(() => DurationEstimator.BuildSsml("hello", -0.0));
    }

    [Theory]
    [InlineData(1.0, "100%")]
    [InlineData(0.85, "85%")]
    [InlineData(1.15, "115%")]
    [InlineData(0.5, "85%")]
    [InlineData(2.0, "115%")]
    [InlineData(0.855, "86%")]
    [InlineData(0.854, "85%")]
    [InlineData(100.0, "115%")]
    public void BuildSsml_Clamps_And_Rounds_Percent(double rate, string percent)
    {
        Assert.Equal(
            "<speak><prosody rate=\"" + percent + "\">hello</prosody></speak>",
            DurationEstimator.BuildSsml("hello", rate));
    }

    [Fact]
    public void BuildSsml_Escapes_Xml_Metacharacters()
    {
        var ssml = DurationEstimator.BuildSsml("a & b < c > d \" e ' f", 1.0);

        Assert.Equal(
            "<speak><prosody rate=\"100%\">a &amp; b &lt; c &gt; d &quot; e &apos; f</prosody></speak>",
            ssml);
        Assert.DoesNotContain("< c", ssml, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildSsml_Is_Deterministic_And_Adds_No_Secrets()
    {
        var first = DurationEstimator.BuildSsml("Hallo Welt", 1.0);
        Assert.Equal(first, DurationEstimator.BuildSsml("Hallo Welt", 1.0));
        Assert.DoesNotContain("secret", first, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ComputeHash_Null_Text_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => DurationEstimator.ComputeHash(null!));
    }

    [Theory]
    [InlineData("", "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855")]
    [InlineData("abc", "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad")]
    [InlineData(
        "The quick brown fox jumps over the lazy dog",
        "d7a8fbb307d7809469ca9abcb0082e4f8d5651e46d3cdb762d02d0bf37c9e592")]
    public void ComputeHash_Matches_Known_Sha256(string text, string expected)
    {
        Assert.Equal(expected, DurationEstimator.ComputeHash(text));
    }

    [Fact]
    public void ComputeHash_Is_Lowercase_Hex_64_And_Sensitive()
    {
        var a = DurationEstimator.ComputeHash("sk-test-DO-NOT-USE one");
        var b = DurationEstimator.ComputeHash("sk-test-DO-NOT-USE two");

        Assert.Equal(64, a.Length);
        Assert.Equal(a.ToLowerInvariant(), a);
        Assert.NotEqual(a, b);
        Assert.Equal(a, DurationEstimator.ComputeHash("sk-test-DO-NOT-USE one"));
    }
}
