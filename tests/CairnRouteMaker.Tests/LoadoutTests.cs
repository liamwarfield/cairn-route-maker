using CairnRouteMaker.Routes;
using Xunit;

namespace CairnRouteMaker.Tests;

public class LoadoutTests
{
    [Fact]
    public void DefaultLoadoutIsLeftOutOfTheFile()
    {
        var json = RouteFile.Serialize(new Route { ZoneKey = "Kami/A" });

        Assert.DoesNotContain("\"loadout\"", json);
        Assert.True(RouteFile.Deserialize(json).Loadout.IsDefault);
    }

    [Fact]
    public void RoundTripsALoadout()
    {
        var route = new Route { ZoneKey = "Kami/A" };
        route.Loadout.Health = 40;
        route.Loadout.LeftHand = Grip.Bare;
        route.Loadout.RightHand = Grip.Hurt;
        route.Loadout.EndGame = true;
        route.Loadout.SurvivalNeeds = true;
        route.Loadout.AddItem("ITEM_ENERGY_BAR");
        route.Loadout.AddItem("ITEM_ENERGY_BAR");
        route.Loadout.AddItem("ITEM_PITON");

        var json = RouteFile.Serialize(route);
        var copy = RouteFile.Deserialize(json).Loadout;

        Assert.Contains("\"rightHand\": \"hurt\"", json);
        Assert.Equal(40, copy.Health);
        Assert.Equal(Grip.Bare, copy.LeftHand);
        Assert.Equal(Grip.Hurt, copy.RightHand);
        Assert.True(copy.EndGame);
        Assert.True(copy.SurvivalNeeds);
        Assert.Collection(copy.Items,
            item => Assert.Equal(("ITEM_ENERGY_BAR", 2), (item.Id, item.Count)),
            item => Assert.Equal(("ITEM_PITON", 1), (item.Id, item.Count)));
        Assert.False(route.IsEmpty);
    }

    [Fact]
    public void AnEmptyItemListMeansCarryingNothing()
    {
        var route = new Route { ZoneKey = "Kami/A" };
        route.Loadout.Items = new();

        var copy = RouteFile.Deserialize(RouteFile.Serialize(route)).Loadout;

        Assert.NotNull(copy.Items);
        Assert.Empty(copy.Items);
        Assert.False(copy.IsDefault);
    }

    [Fact]
    public void ReadsPartialAndOutOfRangeLoadouts()
    {
        var loadout = RouteFile.Deserialize(
            "{\"formatVersion\": 8, \"zoneKey\": \"Kami/A\", \"loadout\": {\"health\": 3, \"items\": [{\"id\": \"ITEM_NUTS\", \"count\": 500}, {\"id\": \"\", \"count\": 2}, {\"id\": \"ITEM_TEA_BAG\", \"count\": 0}]}}").Loadout;

        Assert.Equal(Loadout.MinHealth, loadout.Health);
        Assert.Equal(Grip.Taped, loadout.LeftHand);
        Assert.False(loadout.EndGame);
        var item = Assert.Single(loadout.Items);
        Assert.Equal(LoadoutItem.MaxCount, item.Count);
    }

    [Theory]
    [InlineData(100, 1, 5)]
    [InlineData(5, -1, 100)]
    [InlineData(50, -2, 40)]
    public void HealthStepsWrapAround(int start, int steps, int expected)
    {
        var loadout = new Loadout { Health = start };

        loadout.StepHealth(steps);

        Assert.Equal(expected, loadout.Health);
    }

    [Fact]
    public void GripAndCountsWrapAround()
    {
        var item = new LoadoutItem("ITEM_NUTS", LoadoutItem.MaxCount);

        Loadout.StepCount(item, 1);

        Assert.Equal(Grip.Hurt, Loadout.StepGrip(Grip.Taped, -1));
        Assert.Equal(Grip.Taped, Loadout.StepGrip(Grip.Hurt, 1));
        Assert.Equal(0, item.Count);
    }

    [Fact]
    public void EndGameCapsHealthAtHalf()
    {
        var loadout = new Loadout { Health = 80, EndGame = true };

        Assert.Equal(50, loadout.EffectiveHealth);
    }

    [Fact]
    public void AddingAListedItemCountsItUp()
    {
        var loadout = new Loadout();

        loadout.AddItem("ITEM_NUTS");
        loadout.AddItem("ITEM_NUTS");
        loadout.Items[0].Count = 0;
        loadout.RemoveEmptyItems();

        Assert.NotNull(loadout.Items);
        Assert.Empty(loadout.Items);
    }
}
