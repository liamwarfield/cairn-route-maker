using System.IO;
using System.Numerics;
using CairnRouteMaker.Routes;
using Xunit;

namespace CairnRouteMaker.Tests;

public class RouteTests
{
    private static Stroke StrokeAt(StrokeMode mode, Vector3 position, float radius)
    {
        var stroke = new Stroke(mode);
        stroke.Stamps.Add(new Stamp(position, Vector3.UnitZ, radius));
        return stroke;
    }

    [Fact]
    public void UnpaintedPointsAreOffRoute()
    {
        var route = new Route();
        route.Strokes.Add(StrokeAt(StrokeMode.Allow, Vector3.Zero, 1f));

        Assert.True(route.IsAllowed(new Vector3(0.5f, 0, 0)));
        Assert.False(route.IsAllowed(new Vector3(2f, 0, 0)));
    }

    [Fact]
    public void LaterStrokesOverrideEarlierOnes()
    {
        var route = new Route();
        route.Strokes.Add(StrokeAt(StrokeMode.Allow, Vector3.Zero, 2f));
        route.Strokes.Add(StrokeAt(StrokeMode.Deny, Vector3.Zero, 0.5f));
        route.Strokes.Add(StrokeAt(StrokeMode.Allow, new Vector3(0.2f, 0, 0), 0.1f));

        Assert.False(route.IsAllowed(new Vector3(-0.2f, 0, 0)));
        Assert.True(route.IsAllowed(new Vector3(0.2f, 0, 0)));
        Assert.True(route.IsAllowed(new Vector3(1.5f, 0, 0)));
    }

    [Fact]
    public void EraseClearsEarlierPaintOnly()
    {
        var route = new Route();
        route.Strokes.Add(StrokeAt(StrokeMode.Allow, Vector3.Zero, 2f));
        route.Strokes.Add(StrokeAt(StrokeMode.Erase, Vector3.Zero, 0.5f));
        route.Strokes.Add(StrokeAt(StrokeMode.Allow, new Vector3(0.3f, 0, 0), 0.1f));

        Assert.False(route.IsAllowed(Vector3.Zero));
        Assert.True(route.IsAllowed(new Vector3(0.3f, 0, 0)));
        Assert.True(route.IsAllowed(new Vector3(1.5f, 0, 0)));
    }

    [Fact]
    public void ReadsVersionOneFiles()
    {
        var route = RouteFile.Deserialize("{\"formatVersion\": 1, \"zoneKey\": \"Kami/A\", \"strokes\": [{\"mode\": \"allow\", \"stamps\": [{\"p\": [0,0,0], \"n\": [0,0,1], \"r\": 1}]}]}");

        Assert.Equal("Kami/A", route.ZoneKey);
        Assert.True(route.IsAllowed(Vector3.Zero));
    }

    [Fact]
    public void RoundTripsThroughJson()
    {
        var route = new Route
        {
            Name = "Slab warmup",
            ZoneKey = "Kami/Zone_A",
            GameVersion = "1.2.3",
            Start = new StartPosition { Position = new Vector3(1, 2, 3), YawDegrees = 90f },
            Finish = new FinishZone { Position = new Vector3(1, 12, 3), Radius = 1.5f },
        };
        route.Strokes.Add(StrokeAt(StrokeMode.Allow, new Vector3(4, 5, 6), 0.75f));
        route.Strokes.Add(StrokeAt(StrokeMode.Deny, new Vector3(7, 8, 9), 0.25f));
        route.Strokes.Add(StrokeAt(StrokeMode.Erase, new Vector3(7, 8, 9), 0.1f));

        var json = RouteFile.Serialize(route);
        var copy = RouteFile.Deserialize(json);

        Assert.Contains("\"mode\": \"deny\"", json);
        Assert.Equal(route.Name, copy.Name);
        Assert.Equal(route.ZoneKey, copy.ZoneKey);
        Assert.Equal(route.Start.Position, copy.Start.Position);
        Assert.Equal(90f, copy.Start.YawDegrees);
        Assert.Equal(route.Finish.Position, copy.Finish.Position);
        Assert.Equal(1.5f, copy.Finish.Radius);
        Assert.Equal(3, copy.Strokes.Count);
        Assert.Equal(StrokeMode.Deny, copy.Strokes[1].Mode);
        Assert.Equal(StrokeMode.Erase, copy.Strokes[2].Mode);
        Assert.Equal(route.Strokes[0].Stamps[0], copy.Strokes[0].Stamps[0]);
    }

    [Fact]
    public void RejectsNewerFormats()
    {
        Assert.Throws<InvalidDataException>(() => RouteFile.Deserialize("{\"formatVersion\": 99}"));
    }

    [Fact]
    public void StorageSanitisesZoneKeysIntoFileNames()
    {
        var dir = TempDirectory();
        var storage = new RouteStorage(dir);
        var route = new Route { ZoneKey = "Kami/Zone:A" };

        var path = storage.Save(route);

        Assert.Equal(Path.Combine(dir, "Kami_Zone_A.json"), path);
        Assert.Equal("Kami_Zone_A", route.Id);
        Assert.Equal("Kami/Zone:A", Assert.Single(storage.LoadAll()).ZoneKey);
        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public void AZoneCanHoldSeveralRoutes()
    {
        var dir = TempDirectory();
        var storage = new RouteStorage(dir);
        var first = new Route { Name = "Slab", ZoneKey = "Kami/A" };
        var second = new Route { Name = "Roof", ZoneKey = "Kami/A" };

        storage.Save(first);
        storage.Save(second);
        first.Name = "Slab, harder";
        storage.Save(first);

        Assert.Equal("Kami_A", first.Id);
        Assert.Equal("Kami_A-2", second.Id);
        var routes = storage.LoadAll();
        Assert.Equal(new[] { "Slab, harder", "Roof" }, routes.ConvertAll(r => r.Name));
        Assert.Equal(new[] { "Kami_A", "Kami_A-2" }, routes.ConvertAll(r => r.Id));
        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public void NewRoutesAreNumberedWithinTheirZone()
    {
        var dir = TempDirectory();
        var storage = new RouteStorage(dir);
        storage.Save(new Route { ZoneKey = "Kami/A" });
        storage.Save(new Route { Name = "Route 3", ZoneKey = "Kami/A" });
        storage.Save(new Route { ZoneKey = "Kami/B" });

        var route = storage.Create("Kami/A");

        Assert.Equal("Route 4", route.Name);
        Assert.Equal("Kami/A", route.ZoneKey);
        Assert.Null(route.Id);
        Assert.Equal("Route 1", storage.Create("Kami/C").Name);
        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public void LoadAllSkipsBrokenFilesAndSortsByZoneThenAge()
    {
        var dir = TempDirectory();
        var storage = new RouteStorage(dir);
        storage.Save(new Route { ZoneKey = "Tenzing/B" });
        for (var i = 0; i < 10; i++)
        {
            storage.Save(new Route { Name = $"A{i + 1}", ZoneKey = "Kami/A" });
        }

        File.WriteAllText(Path.Combine(dir, "broken.json"), "{ not json");

        var routes = storage.LoadAll();

        Assert.Equal(11, routes.Count);
        Assert.Equal(new[] { "A1", "A2" }, routes.GetRange(0, 2).ConvertAll(r => r.Name));
        Assert.Equal("Kami_A-10", routes[9].Id);
        Assert.Equal("Tenzing/B", routes[10].ZoneKey);
        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public void ReadsRoutesSavedBeforeZonesHadSeveral()
    {
        var dir = TempDirectory();
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Kami_09_Well.json"), "{\"formatVersion\": 3, \"name\": \"Untitled route\", \"zoneKey\": \"Kami/09_Well\"}");
        var storage = new RouteStorage(dir);

        var old = Assert.Single(storage.LoadAll());
        var added = storage.Create("Kami/09_Well");
        storage.Save(added);

        Assert.Equal("Kami_09_Well", old.Id);
        Assert.Equal("Route 2", added.Name);
        Assert.Equal("Kami_09_Well-2", added.Id);
        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public void DeleteMovesTheRouteIntoBackups()
    {
        var root = TempDirectory();
        var storage = new RouteStorage(Path.Combine(root, "Routes"));
        var kept = new Route { Name = "Kept", ZoneKey = "Kami/A" };
        var deleted = new Route { Name = "Deleted", ZoneKey = "Kami/A" };
        storage.Save(kept);
        storage.Save(deleted);

        var backup = storage.Delete(deleted, new System.DateTime(2026, 10, 17, 16, 5, 9));

        Assert.Equal("Kept", Assert.Single(storage.LoadAll()).Name);
        Assert.Equal(Path.Combine(root, "Backups", "Kami_A-2.20261017-160509.json"), backup);
        Assert.Equal("Deleted", RouteFile.Deserialize(File.ReadAllText(backup)).Name);
        Assert.Null(storage.Delete(deleted, System.DateTime.Now));
        Assert.Null(storage.Delete(new Route { ZoneKey = "Kami/A" }, System.DateTime.Now));
        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public void NewFilesGoAfterTheNewestEvenWhenAnOlderOneWasDeleted()
    {
        var root = TempDirectory();
        var storage = new RouteStorage(Path.Combine(root, "Routes"));
        var first = new Route { Name = "First", ZoneKey = "Kami/A" };
        storage.Save(first);
        storage.Save(new Route { Name = "Second", ZoneKey = "Kami/A" });
        storage.Save(new Route { Name = "Third", ZoneKey = "Kami/A" });
        storage.Delete(first, System.DateTime.Now);

        var fourth = new Route { Name = "Fourth", ZoneKey = "Kami/A" };
        storage.Save(fourth);

        Assert.Equal("Kami_A-4", fourth.Id);
        Assert.Equal(new[] { "Second", "Third", "Fourth" }, storage.LoadAll().ConvertAll(r => r.Name));
        Directory.Delete(root, recursive: true);
    }

    private static string TempDirectory() => Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
}
