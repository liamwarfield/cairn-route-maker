using CairnRouteMaker.Routes;
using Xunit;

namespace CairnRouteMaker.Tests;

public class ZoneNamesTests
{
    [Theory]
    [InlineData("04_SplitRock", "Split Rock")]
    [InlineData("07_Lunchbreak", "Lunch Break")]
    [InlineData("Tenzing_04_MountainMid", "Mid-Mountain")]
    [InlineData("Crux_02_DeepWaterPiton", "Deep Water")]
    [InlineData("Crux_05_Nunataq", "Nunataq")]
    [InlineData("Crux_09_SomeNewCrag", "Some New Crag")]
    [InlineData("99_SomethingNew", "99_SomethingNew")]
    [InlineData(null, null)]
    public void PrettyFallsBackToTheRealName(string zone, string expected) => Assert.Equal(expected, ZoneNames.Pretty(zone));

    [Theory]
    [InlineData("Kami/04_SplitRock", "Split Rock", "Kami")]
    [InlineData("Kami/99_SomethingNew", "99_SomethingNew", "Kami")]
    [InlineData("Crux_01_TowerGym/Crux_01_TowerGym", "Tower Gym", "Crux_01_TowerGym")]
    [InlineData("NoSlash", "NoSlash", null)]
    public void KeysSplitIntoWorldAndReadableZone(string key, string zone, string world)
    {
        Assert.Equal(zone, ZoneNames.PrettyKey(key));
        Assert.Equal(world, ZoneNames.World(key));
    }

    [Theory]
    [InlineData("Tenzing", "Tenzen")]
    [InlineData("Kami", "Kami")]
    [InlineData("Crux_01_TowerGym", "Lamu")]
    [InlineData("Crux_02_DeepWaterPiton", "Haken")]
    [InlineData("Crux_03_Mallorca", "Ruka")]
    [InlineData("Crux_06_Atlas", "Atlas")]
    [InlineData(null, null)]
    public void WorldsUseTheirInGameNames(string world, string expected) => Assert.Equal(expected, ZoneNames.PrettyWorld(world));

    [Theory]
    [InlineData("Crux_01_TowerGym_SpawnerMC", "Tower Gym")]
    [InlineData("Crux_01_TowerGym_SpawnerMC_Left", "Tower Gym Left")]
    [InlineData("Crux_02_Needle_SpawnerMC (1)", "Needle 2")]
    [InlineData("Crux_03_LaBaule_SpawnerMC", "La Baule")]
    [InlineData("SpawnerMC_Kami_20_SpecialDome (4)", "Special Dome 5")]
    [InlineData("SpawnerMC", "SpawnerMC")]
    [InlineData(null, null)]
    public void SpawnersReadAsPlaces(string spawner, string expected) => Assert.Equal(expected, ZoneNames.PrettySpawner(spawner));
}
