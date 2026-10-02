// GAP-021: Plan B 9.1 requires a top-level `timezone` on `GET /api/v1/me`,
// sourced from the `timezone` preference with a default, so the frontend does
// not need a second round-trip.
using DubbingPlatform.Api.Controllers;
using DubbingPlatform.Api.Models;

namespace DubbingPlatform.UnitTests.Auth;

public sealed class MeTimezoneTests
{
    [Fact]
    public void MeResponse_Exposes_A_Top_Level_Timezone()
    {
        var property = typeof(MeResponse).GetProperty(nameof(MeResponse.Timezone));
        Assert.NotNull(property);
        Assert.Equal(typeof(string), property!.PropertyType);

        // Alongside `locale`, not nested inside a preferences slice.
        var names = typeof(MeResponse).GetProperties().Select(p => p.Name).ToList();
        Assert.Contains(nameof(MeResponse.Locale), names);
        Assert.DoesNotContain("Preferences", names);
    }

    [Fact]
    public void Default_Timezone_Is_Utc()
    {
        Assert.Equal("UTC", MeController.DefaultTimezone);
    }

    [Theory]
    [InlineData("\"Europe/Paris\"", "Europe/Paris")]
    [InlineData("\"America/New_York\"", "America/New_York")]
    [InlineData("\"UTC\"", "UTC")]
    [InlineData("\"  Asia/Tokyo  \"", "Asia/Tokyo")]
    public void Stored_Preference_Is_Used(string valueJson, string expected)
    {
        Assert.Equal(expected, MeController.ParseTimezonePreference(valueJson));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\"\"")]
    [InlineData("\"   \"")]
    [InlineData("42")]
    [InlineData("null")]
    [InlineData("{ \"zone\": \"Europe/Paris\" }")]
    [InlineData("not-json")]
    public void Missing_Or_Unusable_Preference_Falls_Back_To_The_Default(string? valueJson)
    {
        Assert.Equal(MeController.DefaultTimezone, MeController.ParseTimezonePreference(valueJson));
    }
}