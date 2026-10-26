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
    public void RoundTripsTheRouteLine()
    {
        var route = new Route { ZoneKey = "Kami/A" };
        route.Line.Add(new LinePoint(new Vector3(1, 2, 3), Vector3.UnitZ));
        route.Line.Add(new LinePoint(new Vector3(1, 7, 3.5f), -Vector3.UnitX));

        var json = RouteFile.Serialize(route);
        var copy = RouteFile.Deserialize(json);

        Assert.Contains("\"formatVersion\": 4", json);
        Assert.Equal(route.Line, copy.Line);
        Assert.False(copy.IsEmpty);
    }

    [Fact]
    public void LeavesTheLineOutWhenThereIsNone()
    {
        var json = RouteFile.Serialize(new Route { ZoneKey = "Kami/A" });

        Assert.DoesNotContain("\"line\"", json);
        Assert.Empty(RouteFile.Deserialize(json).Line);
    }

    [Fact]
    public void ReadsVersionThreeFilesWithoutALine()
    {
        var route = RouteFile.Deserialize(
            "{\"formatVersion\": 3, \"zoneKey\": \"Kami/A\", \"finish\": {\"position\": [0,5,0], \"radius\": 1}, \"strokes\": []}");

        Assert.Empty(route.Line);
        Assert.Equal(1f, route.Finish.Radius);
    }

    [Fact]
    public void RejectsNewerFormats()
    {
        Assert.Throws<InvalidDataException>(() => RouteFile.Deserialize("{\"formatVersion\": 99}"));
    }

    [Fact]
    public void StorageSanitisesZoneKeysIntoFileNames()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var storage = new RouteStorage(dir);
        var route = new Route { ZoneKey = "Kami/Zone:A" };

        var path = storage.Save(route);

        Assert.Equal(Path.Combine(dir, "Kami_Zone_A.json"), path);
        Assert.True(storage.Exists("Kami/Zone:A"));
        Assert.Equal("Kami/Zone:A", storage.Load("Kami/Zone:A").ZoneKey);
        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public void LoadAllSkipsBrokenFilesAndSortsByZone()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var storage = new RouteStorage(dir);
        storage.Save(new Route { ZoneKey = "Tenzing/B" });
        storage.Save(new Route { ZoneKey = "Kami/A" });
        File.WriteAllText(Path.Combine(dir, "broken.json"), "{ not json");

        var routes = storage.LoadAll();

        Assert.Equal(new[] { "Kami/A", "Tenzing/B" }, routes.ConvertAll(r => r.ZoneKey));
        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public void DeleteMovesTheRouteIntoBackups()
    {
        var root = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var storage = new RouteStorage(Path.Combine(root, "Routes"));
        storage.Save(new Route { ZoneKey = "Kami/A" });

        var backup = storage.Delete("Kami/A", new System.DateTime(2026, 10, 17, 16, 5, 9));

        Assert.False(storage.Exists("Kami/A"));
        Assert.Equal(Path.Combine(root, "Backups", "Kami_A.20261017-160509.json"), backup);
        Assert.Equal("Kami/A", RouteFile.Deserialize(File.ReadAllText(backup)).ZoneKey);
        Assert.Null(storage.Delete("Kami/A", System.DateTime.Now));
        Directory.Delete(root, recursive: true);
    }
}
