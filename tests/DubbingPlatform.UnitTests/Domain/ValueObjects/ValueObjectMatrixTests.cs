// Task 039C: domain value object unit gap closure.
using System;
using System.Globalization;
using System.Threading;
using DubbingPlatform.Domain.ValueObjects;

namespace DubbingPlatform.UnitTests.Domain.ValueObjects;

/// <summary>
/// Matrix for the SHA-256 hex value objects (<see cref="ContentHash"/>,
/// <see cref="ConfigurationHash"/>, <see cref="PromptHash"/>,
/// <see cref="ProviderRouteHash"/>, <see cref="ExecutionSnapshotHash"/>).
/// All five share the same 64-lowercase-hex contract but are distinct types.
/// </summary>
public sealed class HashValueObjectMatrixTests
{
    private const string ValidHex =
        "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    private static string Repeat(char c, int count) => new(c, count);

    public static TheoryData<string> ValidHexes() => new()
    {
        ValidHex,
        new string('0', 64),
        new string('9', 64),
        new string('f', 64),
        new string('a', 64),
    };

    public static TheoryData<string> InvalidHexes() => new()
    {
        null!,
        string.Empty,
        "not-hex",
        Repeat('a', 63),
        Repeat('a', 65),
        Repeat('A', 64),
        Repeat('g', 64),
        Repeat('0', 63) + "G",
        " " + ValidHex,
        ValidHex + " ",
    };

    [Theory]
    [MemberData(nameof(ValidHexes))]
    public void ContentHash_Accepts_64_Lowercase_Hex(string hex)
    {
        var sut = new ContentHash(hex);

        Assert.Equal(hex, sut.Sha256Hex);
        Assert.Equal(hex, sut.ToString());
    }

    [Theory]
    [MemberData(nameof(InvalidHexes))]
    public void ContentHash_Rejects_Anything_Else(string hex)
    {
        var ex = Assert.Throws<ArgumentException>(() => new ContentHash(hex));
        Assert.Equal("sha256Hex", ex.ParamName);
    }

    [Fact]
    public void ContentHash_Rejects_UPPERCASE_Hex()
    {
        Assert.Throws<ArgumentException>(() => new ContentHash(ValidHex.ToUpperInvariant()));
    }

    [Theory]
    [MemberData(nameof(ValidHexes))]
    public void ConfigurationHash_Accepts_64_Lowercase_Hex(string hex)
    {
        var sut = new ConfigurationHash(hex);

        Assert.Equal(hex, sut.Sha256Hex);
        Assert.Equal(hex, sut.ToString());
    }

    [Theory]
    [MemberData(nameof(InvalidHexes))]
    public void ConfigurationHash_Rejects_Anything_Else(string hex)
    {
        var ex = Assert.Throws<ArgumentException>(() => new ConfigurationHash(hex));
        Assert.Equal("sha256Hex", ex.ParamName);
    }

    [Theory]
    [MemberData(nameof(ValidHexes))]
    public void PromptHash_Accepts_64_Lowercase_Hex(string hex)
    {
        var sut = new PromptHash(hex);

        Assert.Equal(hex, sut.Sha256Hex);
        Assert.Equal(hex, sut.ToString());
    }

    [Theory]
    [MemberData(nameof(InvalidHexes))]
    public void PromptHash_Rejects_Anything_Else(string hex)
    {
        var ex = Assert.Throws<ArgumentException>(() => new PromptHash(hex));
        Assert.Equal("sha256Hex", ex.ParamName);
    }

    [Theory]
    [MemberData(nameof(ValidHexes))]
    public void ProviderRouteHash_Accepts_64_Lowercase_Hex(string hex)
    {
        var sut = new ProviderRouteHash(hex);

        Assert.Equal(hex, sut.Sha256Hex);
        Assert.Equal(hex, sut.ToString());
    }

    [Theory]
    [MemberData(nameof(InvalidHexes))]
    public void ProviderRouteHash_Rejects_Anything_Else(string hex)
    {
        var ex = Assert.Throws<ArgumentException>(() => new ProviderRouteHash(hex));
        Assert.Equal("sha256Hex", ex.ParamName);
    }

    [Theory]
    [MemberData(nameof(ValidHexes))]
    public void ExecutionSnapshotHash_Accepts_64_Lowercase_Hex(string hex)
    {
        var sut = new ExecutionSnapshotHash(hex);

        Assert.Equal(hex, sut.Sha256Hex);
        Assert.Equal(hex, sut.ToString());
    }

    [Theory]
    [MemberData(nameof(InvalidHexes))]
    public void ExecutionSnapshotHash_Rejects_Anything_Else(string hex)
    {
        var ex = Assert.Throws<ArgumentException>(() => new ExecutionSnapshotHash(hex));
        Assert.Equal("sha256Hex", ex.ParamName);
    }

    [Fact]
    public void Hash_Types_Are_Distinct_Not_Interchangeable()
    {
        // Same underlying hex, different nominal types.
        var content = new ContentHash(ValidHex);
        var configuration = new ConfigurationHash(ValidHex);
        var prompt = new PromptHash(ValidHex);
        var route = new ProviderRouteHash(ValidHex);
        var snapshot = new ExecutionSnapshotHash(ValidHex);

        Assert.Equal(ValidHex, content.Sha256Hex);
        Assert.Equal(ValidHex, configuration.Sha256Hex);
        Assert.Equal(ValidHex, prompt.Sha256Hex);
        Assert.Equal(ValidHex, route.Sha256Hex);
        Assert.Equal(ValidHex, snapshot.Sha256Hex);

        Assert.NotEqual<Type>(typeof(ContentHash), typeof(ConfigurationHash));
        Assert.NotEqual<Type>(typeof(ContentHash), typeof(PromptHash));
        Assert.NotEqual<Type>(typeof(ContentHash), typeof(ProviderRouteHash));
        Assert.NotEqual<Type>(typeof(ContentHash), typeof(ExecutionSnapshotHash));
    }

    [Fact]
    public void ContentHash_Is_A_Record_With_Value_Equality()
    {
        Assert.Equal(new ContentHash(ValidHex), new ContentHash(ValidHex));

        // Distinct nominal types are never equal, even with identical hex.
        Assert.False(new ContentHash(ValidHex).Equals(new ConfigurationHash(ValidHex)));
    }

    [Fact]
    public void ContentHash_Hex_Validation_Accepts_Only_Digits_And_Lowercase_A_F()
    {
        for (var c = '0'; c <= '9'; c++)
        {
            Assert.Throws<ArgumentException>(() => new ContentHash(Repeat(c, 63) + "g"));
        }

        for (var c = 'a'; c <= 'f'; c++)
        {
            var hex = Repeat(c, 64);
            Assert.Equal(hex, new ContentHash(hex).Sha256Hex);
        }
    }
}

/// <summary>Matrix for <see cref="Money"/>. Non-negative amount, 3-letter ISO 4217 code.</summary>
public sealed class MoneyValueObjectTests
{
    [Theory]
    [InlineData("USD")]
    [InlineData("usd")]
    [InlineData("uSd")]
    [InlineData("EUR")]
    [InlineData("GBP")]
    [InlineData("JPY")]
    [InlineData("AED")]
    public void Money_Normalizes_Currency_To_Uppercase(string currency)
    {
        var sut = new Money(1.00m, currency);

        Assert.Equal(currency.ToUpperInvariant(), sut.Currency);
    }

    [Fact]
    public void Money_Defaults_To_USD_When_Currency_Is_Null()
    {
        Assert.Equal("USD", new Money(1.00m, null).Currency);
        Assert.Equal("USD", new Money(1.00m).Currency);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.01)]
    [InlineData(1.0)]
    [InlineData(99.0)]
    [InlineData(100.0)]
    [InlineData(12345678901234.5678)]
    public void Money_Accepts_NonNegative_Amounts(double amount)
    {
        var sut = new Money((decimal)amount, "USD");
        Assert.Equal((decimal)amount, sut.Amount);
    }

    [Fact]
    public void Money_Accepts_Decimal_Maximum()
    {
        var sut = new Money(decimal.MaxValue, "USD");
        Assert.Equal(decimal.MaxValue, sut.Amount);
    }

    [Theory]
    [InlineData(-0.01)]
    [InlineData(-1.0)]
    [InlineData(-12345678901234.5678)]
    public void Money_Rejects_Negative_Amounts(double amount)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => new Money((decimal)amount, "USD"));
        Assert.Equal("amount", ex.ParamName);
    }

    [Theory]
    [InlineData("-0.0000000000000000000000000001")]
    [InlineData("-79228162514264337593543950335")]
    public void Money_Rejects_Negative_Decimal_Extremes(string amount)
    {
        // The smallest representable negative, and decimal.MinValue itself.
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => new Money(decimal.Parse(amount), "USD"));
        Assert.Equal("amount", ex.ParamName);
    }

    [Fact]
    public void Money_Rejects_Decimal_Minimum()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Money(decimal.MinValue, "USD"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("US")]
    [InlineData("USDX")]
    [InlineData("US1")]
    [InlineData("12")]
    [InlineData("1234")]
    public void Money_Rejects_Invalid_Currency_Codes(string currency)
    {
        Assert.Throws<ArgumentException>(() => new Money(1.00m, currency));
    }

    [Fact]
    public void Money_Rejects_Negative_Before_Validating_Currency()
    {
        // Ordering: the amount guard runs first, so a negative amount with an
        // invalid currency surfaces as the amount failure, not a generic throw.
        Assert.Throws<ArgumentOutOfRangeException>(() => new Money(-1.0m, "TOOLONG"));
    }

    [Fact]
    public void Money_ToString_Is_Culture_Invariant()
    {
        var sut = new Money(1234.56m, "usd");

        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Assert.Equal("1234.56 USD", sut.ToString());

            CultureInfo.CurrentCulture = new CultureInfo("fr-FR");
            Assert.Equal("1234.56 USD", sut.ToString());
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Money_Is_A_Record_With_Value_Equality()
    {
        Assert.Equal(new Money(1.00m, "usd"), new Money(1.00m, "USD"));
        Assert.NotEqual(new Money(1.00m, "USD"), new Money(1.00m, "EUR"));
        Assert.NotEqual(new Money(0.00m, "USD"), new Money(0.01m, "USD"));
    }

    [Fact]
    public void Money_Zero_Amount_Round_Trips()
    {
        var sut = new Money(0m, "USD");
        Assert.Equal(0m, sut.Amount);
        Assert.Equal("USD", sut.Currency);
    }
}

/// <summary>Matrix for <see cref="TimeRange"/> (integer millisecond timeline range).</summary>
public sealed class TimeRangeValueObjectTests
{
    [Theory]
    [InlineData(0, 1, 1)]
    [InlineData(0, 99, 99)]
    [InlineData(0, 100, 100)]
    [InlineData(1, 100, 99)]
    [InlineData(99, 100, 1)]
    [InlineData(0, int.MaxValue, int.MaxValue)]
    public void TimeRange_Computes_Duration(int startMs, int endMs, int expectedDuration)
    {
        var sut = new TimeRange(startMs, endMs);

        Assert.Equal(startMs, sut.StartMs);
        Assert.Equal(endMs, sut.EndMs);
        Assert.Equal(expectedDuration, sut.DurationMs);
        Assert.Equal(expectedDuration, sut.GetDurationMs());
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    [InlineData(-1000)]
    public void TimeRange_Rejects_Negative_Start(int startMs)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => new TimeRange(startMs, 100));
        Assert.Equal("startMs", ex.ParamName);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(100, 100)]
    [InlineData(200, 100)]
    [InlineData(1, 0)]
    [InlineData(int.MaxValue, int.MaxValue - 1)]
    public void TimeRange_Rejects_NonPositive_Duration(int startMs, int endMs)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => new TimeRange(startMs, endMs));
        Assert.Equal("endMs", ex.ParamName);
    }

    [Fact]
    public void TimeRange_ToString_Is_Invariant()
    {
        var sut = new TimeRange(10, 20);

        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Assert.Equal("[10..20)", sut.ToString());
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void TimeRange_Default_Is_Zero_Zero()
    {
        var sut = default(TimeRange);

        Assert.Equal(0, sut.StartMs);
        Assert.Equal(0, sut.EndMs);
        Assert.Equal(0, sut.DurationMs);
    }

    [Fact]
    public void TimeRange_Is_A_Record_Struct_With_Value_Equality()
    {
        Assert.Equal(new TimeRange(0, 10), new TimeRange(0, 10));
        Assert.NotEqual(new TimeRange(0, 10), new TimeRange(1, 10));
    }
}

/// <summary>Matrix for <see cref="TimingWindow"/> (timing placement tolerances).</summary>
public sealed class TimingWindowValueObjectTests
{
    [Fact]
    public void TimingWindow_Defaults_Are_Production_Tolerances()
    {
        var sut = new TimingWindow();

        Assert.Equal(0, sut.TargetOnsetMs);
        Assert.Equal(0, sut.TargetDurationMs);
        Assert.Equal(50, sut.AllowableLeadMs);
        Assert.Equal(50, sut.AllowableLagMs);
        Assert.Equal(15.0, sut.MaxRateChangePercent);
        Assert.Equal(1.15, sut.MaxStretchFactor);
        Assert.Equal(50, sut.PreferredToleranceMs);
        Assert.Equal(100, sut.MaxToleranceMs);
    }

    [Fact]
    public void TimingWindow_Accepts_All_Explicit_Values()
    {
        var sut = new TimingWindow(
            targetOnsetMs: 1000,
            targetDurationMs: 2500,
            allowableLeadMs: 0,
            allowableLagMs: 100,
            maxRateChangePercent: 0.01,
            maxStretchFactor: 1.0,
            preferredToleranceMs: 0,
            maxToleranceMs: 0);

        Assert.Equal(1000, sut.TargetOnsetMs);
        Assert.Equal(2500, sut.TargetDurationMs);
        Assert.Equal(0, sut.AllowableLeadMs);
        Assert.Equal(100, sut.AllowableLagMs);
        Assert.Equal(0.01, sut.MaxRateChangePercent);
        Assert.Equal(1.0, sut.MaxStretchFactor);
        Assert.Equal(0, sut.PreferredToleranceMs);
        Assert.Equal(0, sut.MaxToleranceMs);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void TimingWindow_Rejects_Negative_TargetOnset(int value)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => new TimingWindow(targetOnsetMs: value));
        Assert.Equal("targetOnsetMs", ex.ParamName);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void TimingWindow_Rejects_Negative_TargetDuration(int value)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => new TimingWindow(targetDurationMs: value));
        Assert.Equal("targetDurationMs", ex.ParamName);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void TimingWindow_Rejects_Negative_Lead(int value)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => new TimingWindow(allowableLeadMs: value));
        Assert.Equal("allowableLeadMs", ex.ParamName);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void TimingWindow_Rejects_Negative_Lag(int value)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => new TimingWindow(allowableLagMs: value));
        Assert.Equal("allowableLagMs", ex.ParamName);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-0.1)]
    [InlineData(-15.0)]
    public void TimingWindow_Rejects_NonPositive_RateChange(double value)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => new TimingWindow(maxRateChangePercent: value));
        Assert.Equal("maxRateChangePercent", ex.ParamName);
    }

    [Theory]
    [InlineData(0.99)]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    public void TimingWindow_Rejects_StretchFactor_Below_One(double value)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => new TimingWindow(maxStretchFactor: value));
        Assert.Equal("maxStretchFactor", ex.ParamName);
    }

    [Fact]
    public void TimingWindow_Accepts_StretchFactor_At_Exactly_One()
    {
        Assert.Equal(1.0, new TimingWindow(maxStretchFactor: 1.0).MaxStretchFactor);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void TimingWindow_Rejects_Negative_PreferredTolerance(int value)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => new TimingWindow(preferredToleranceMs: value));
        Assert.Equal("preferredToleranceMs", ex.ParamName);
    }

    [Theory]
    [InlineData(50, 49)]
    [InlineData(1, 0)]
    [InlineData(100, 99)]
    [InlineData(0, -1)]
    public void TimingWindow_Rejects_MaxTolerance_Below_Preferred(int preferred, int max)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => new TimingWindow(preferredToleranceMs: preferred, maxToleranceMs: max));
        Assert.Equal("maxToleranceMs", ex.ParamName);
    }

    [Fact]
    public void TimingWindow_Accepts_MaxTolerance_Equal_To_Preferred()
    {
        var sut = new TimingWindow(preferredToleranceMs: 100, maxToleranceMs: 100);
        Assert.Equal(100, sut.MaxToleranceMs);
    }

    [Fact]
    public void TimingWindow_ToString_Is_Invariant()
    {
        var sut = new TimingWindow(targetOnsetMs: 10, targetDurationMs: 20, allowableLeadMs: 5, allowableLagMs: 6);

        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Assert.Equal("Onset=10 Duration=20 Lead=5 Lag=6", sut.ToString());
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void TimingWindow_Is_A_Record_With_Value_Equality()
    {
        Assert.Equal(new TimingWindow(), new TimingWindow());
        Assert.NotEqual(new TimingWindow(), new TimingWindow(targetOnsetMs: 1));
    }
}

/// <summary>Matrix for <see cref="ProviderModelReference"/>.</summary>
public sealed class ProviderModelReferenceValueObjectTests
{
    [Fact]
    public void ProviderModelReference_Requires_Provider_And_Model()
    {
        Assert.Equal("provider", Assert.Throws<ArgumentException>(() => new ProviderModelReference(null!, "m")).ParamName);
        Assert.Equal("provider", Assert.Throws<ArgumentException>(() => new ProviderModelReference(" ", "m")).ParamName);
        Assert.Equal("model", Assert.Throws<ArgumentException>(() => new ProviderModelReference("p", null!)).ParamName);
        Assert.Equal("model", Assert.Throws<ArgumentException>(() => new ProviderModelReference("p", "\t")).ParamName);
    }

    [Fact]
    public void ProviderModelReference_Optional_Fields_Default_To_Null()
    {
        var sut = new ProviderModelReference("azure", "gpt-4o");

        Assert.Equal("azure", sut.Provider);
        Assert.Equal("gpt-4o", sut.Model);
        Assert.Null(sut.ModelVersion);
        Assert.Null(sut.Deployment);
        Assert.Null(sut.Region);
        Assert.Null(sut.ApiVersion);
    }

    [Fact]
    public void ProviderModelReference_Keeps_All_Supplied_Fields()
    {
        var sut = new ProviderModelReference(
            "azure",
            "gpt-4o",
            modelVersion: "2024-11-20",
            deployment: "prod",
            region: "westeurope",
            apiVersion: "2024-10-21");

        Assert.Equal("2024-11-20", sut.ModelVersion);
        Assert.Equal("prod", sut.Deployment);
        Assert.Equal("westeurope", sut.Region);
        Assert.Equal("2024-10-21", sut.ApiVersion);
    }

    [Fact]
    public void ProviderModelReference_ToString_Falls_Back_To_Latest()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");

            Assert.Equal("azure/gpt-4o:latest", new ProviderModelReference("azure", "gpt-4o").ToString());
            Assert.Equal(
                "azure/gpt-4o:2024-11-20",
                new ProviderModelReference("azure", "gpt-4o", modelVersion: "2024-11-20").ToString());
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void ProviderModelReference_Is_A_Record_With_Value_Equality()
    {
        Assert.Equal(new ProviderModelReference("p", "m"), new ProviderModelReference("p", "m"));
        Assert.NotEqual(new ProviderModelReference("p", "m"), new ProviderModelReference("p", "m2"));
    }
}
