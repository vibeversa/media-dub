using System.Globalization;
using DubbingPlatform.Application.Configuration;

namespace DubbingPlatform.UnitTests.Configuration;

public sealed class HashCalculatorTests
{
    [Fact]
    public void Same_Input_Different_Key_Order_Same_Hash()
    {
        var first = new Dictionary<string, object?> { ["b"] = 2, ["a"] = 1, ["c"] = "x" };
        var second = new Dictionary<string, object?> { ["c"] = "x", ["a"] = 1, ["b"] = 2 };

        Assert.Equal(ConfigurationHashCalculator.Compute(first), ConfigurationHashCalculator.Compute(second));
    }

    [Fact]
    public void Nested_Key_Order_Does_Not_Change_Hash()
    {
        var first = new Dictionary<string, object?>
        {
            ["outer"] = new Dictionary<string, object?> { ["y"] = true, ["x"] = new[] { 1, 2 } },
        };
        var second = new Dictionary<string, object?>
        {
            ["outer"] = new Dictionary<string, object?> { ["x"] = new[] { 1, 2 }, ["y"] = true },
        };

        Assert.Equal(ConfigurationHashCalculator.Compute(first), ConfigurationHashCalculator.Compute(second));
    }

    [Theory]
    [InlineData("password")]
    [InlineData("apiKey")]
    [InlineData("clientSecret")]
    [InlineData("authToken")]
    [InlineData("credentials")]
    public void Changing_Secret_Does_Not_Change_Hash(string secretKey)
    {
        var first = new Dictionary<string, object?> { ["name"] = "worker", [secretKey] = "value-a" };
        var second = new Dictionary<string, object?> { ["name"] = "worker", [secretKey] = "value-b" };

        Assert.Equal(ConfigurationHashCalculator.Compute(first), ConfigurationHashCalculator.Compute(second));
    }

    [Fact]
    public void Changing_Non_Secret_Changes_Hash()
    {
        var first = new Dictionary<string, object?> { ["name"] = "worker-a" };
        var second = new Dictionary<string, object?> { ["name"] = "worker-b" };

        Assert.NotEqual(ConfigurationHashCalculator.Compute(first), ConfigurationHashCalculator.Compute(second));
    }

    [Fact]
    public void Null_Settings_Treated_As_Empty_Object()
    {
        var fromNull = ConfigurationHashCalculator.Compute(null);
        var fromEmpty = ConfigurationHashCalculator.Compute(new Dictionary<string, object?>());
        var fromEmptyJson = ConfigurationHashCalculator.Compute("{}");

        Assert.Equal(fromEmpty, fromNull);
        Assert.Equal(fromEmpty, fromEmptyJson);
    }

    [Fact]
    public void Hash_Is_Culture_Invariant()
    {
        var settings = new Dictionary<string, object?>
        {
            ["ratio"] = 1234.56,
            ["at"] = new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc),
            ["name"] = "münchen",
        };

        var invariant = ConfigurationHashCalculator.Compute(settings);
        var previousCulture = CultureInfo.CurrentCulture;
        var previousUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            CultureInfo.CurrentUICulture = new CultureInfo("de-DE");
            var german = ConfigurationHashCalculator.Compute(settings);

            CultureInfo.CurrentCulture = new CultureInfo("tr-TR");
            CultureInfo.CurrentUICulture = new CultureInfo("tr-TR");
            var turkish = ConfigurationHashCalculator.Compute(settings);

            Assert.Equal(invariant, german);
            Assert.Equal(invariant, turkish);
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
            CultureInfo.CurrentUICulture = previousUiCulture;
        }
    }

    [Fact]
    public void Hash_Is_Lowercase_Hex_Sha256()
    {
        var hash = ConfigurationHashCalculator.Compute(new Dictionary<string, object?> { ["a"] = 1 });

        Assert.Equal(64, hash.Length);
        Assert.Matches("^[0-9a-f]{64}$", hash);
    }

    [Fact]
    public void Execution_Snapshot_Is_Deterministic_And_Order_Independent()
    {
        var runConfig = new Dictionary<string, object?> { ["pipeline"] = "v1", ["secret"] = "s1" };
        var first = ExecutionSnapshotCalculator.Compute(runConfig, "route", ["b", "a"], ["p2", "p1"], "policy");
        var second = ExecutionSnapshotCalculator.Compute(runConfig, "route", ["a", "b"], ["p1", "p2"], "policy");

        Assert.Equal(first, second);
    }

    [Fact]
    public void Execution_Snapshot_Tolerates_Nulls()
    {
        var hash = ExecutionSnapshotCalculator.Compute(null, null, null, null, null);

        Assert.Equal(64, hash.Length);
        Assert.Matches("^[0-9a-f]{64}$", hash);
    }

    [Fact]
    public void Execution_Snapshot_Changes_When_Inputs_Change()
    {
        var baseHash = ExecutionSnapshotCalculator.Compute(new { Pipeline = "v1" }, "route", ["a"], ["p"], "policy");
        var changedRoute = ExecutionSnapshotCalculator.Compute(new { Pipeline = "v1" }, "other", ["a"], ["p"], "policy");
        var changedInput = ExecutionSnapshotCalculator.Compute(new { Pipeline = "v1" }, "route", ["b"], ["p"], "policy");

        Assert.NotEqual(baseHash, changedRoute);
        Assert.NotEqual(baseHash, changedInput);
    }
}
