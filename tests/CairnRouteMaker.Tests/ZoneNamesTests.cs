using CairnRouteMaker.Routes;
using Xunit;

namespace CairnRouteMaker.Tests;

public class ZoneNamesTests
{
    [Theory]
    [InlineData("04_SplitRock", "Split Rock")]
    [InlineData("07_Lunchbreak", "Lunch Break")]
    [InlineData("99_SomethingNew", "99_SomethingNew")]
    [InlineData(null, null)]
    public void PrettyFallsBackToTheRealName(string zone, string expected) => Assert.Equal(expected, ZoneNames.Pretty(zone));

    [Theory]
    [InlineData("Kami/04_SplitRock", "Split Rock", "Kami")]
    [InlineData("Kami/99_SomethingNew", "99_SomethingNew", "Kami")]
    [InlineData("NoSlash", "NoSlash", null)]
    public void KeysSplitIntoWorldAndReadableZone(string key, string zone, string world)
    {
        Assert.Equal(zone, ZoneNames.PrettyKey(key));
        Assert.Equal(world, ZoneNames.World(key));
    }
}
