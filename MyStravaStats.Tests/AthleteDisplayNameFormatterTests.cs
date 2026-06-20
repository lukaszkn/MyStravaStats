using MyStravaStatsWebApp.Services;
using Xunit;

namespace MyStravaStats.Tests;

public sealed class AthleteDisplayNameFormatterTests
{
    [Theory]
    [InlineData("Marek Miara", "Marek")]
    [InlineData("Lukasz K", "Lukasz")]
    [InlineData(" Ada  Lovelace ", "Ada")]
    [InlineData("Ada", "Ada")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData(null, "")]
    public void GetFirstNameReturnsFirstSpaceSeparatedName(string? athleteName, string expected)
    {
        Assert.Equal(expected, AthleteDisplayNameFormatter.GetFirstName(athleteName));
    }
}
