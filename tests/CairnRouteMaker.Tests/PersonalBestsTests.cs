using System;
using System.IO;
using CairnRouteMaker.Routes;
using Xunit;

namespace CairnRouteMaker.Tests;

public class PersonalBestsTests
{
    private static readonly DateTime When = new(2026, 10, 28, 20, 15, 0);

    private static string TempDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "crm-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void KeepsOnlyFasterTimes()
    {
        var root = TempDirectory();
        var bests = new PersonalBests(Path.Combine(root, "PersonalBests.json"));

        Assert.Null(bests.Get("Kami_09_Well"));
        Assert.True(bests.Record("Kami_09_Well", 90.5, When));
        Assert.False(bests.Record("Kami_09_Well", 95, When));
        Assert.False(bests.Record("Kami_09_Well", 90.5, When));
        Assert.True(bests.Record("Kami_09_Well", 83.25, When));

        Assert.Equal(83.25, bests.Get("Kami_09_Well"));
        Assert.Null(bests.Get("Kami_09_Well-2"));
        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public void ReadsBackWhatWasSaved()
    {
        var root = TempDirectory();
        var path = Path.Combine(root, "PersonalBests.json");
        var bests = new PersonalBests(path);
        bests.Record("Kami_09_Well", 83.25, When);
        bests.Record("Kami_09_Well-2", 61, When);
        bests.Clear("Kami_09_Well-2");

        var reread = new PersonalBests(path);

        Assert.Equal(83.25, reread.Get("Kami_09_Well"));
        Assert.Null(reread.Get("Kami_09_Well-2"));
        Assert.False(reread.Clear("Kami_09_Well-2"));
        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public void UnsavedRoutesHaveNoBest()
    {
        var root = TempDirectory();
        var path = Path.Combine(root, "PersonalBests.json");
        var bests = new PersonalBests(path);

        Assert.False(bests.Record(null, 10, When));
        Assert.Null(bests.Get(null));
        Assert.False(File.Exists(path));
        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public void AnUnreadableFileStartsEmpty()
    {
        var root = TempDirectory();
        var path = Path.Combine(root, "PersonalBests.json");
        File.WriteAllText(path, "{ not json");
        var bests = new PersonalBests(path);

        Assert.Null(bests.Get("Kami_09_Well"));
        Assert.True(bests.Record("Kami_09_Well", 83.25, When));
        Assert.Equal(83.25, new PersonalBests(path).Get("Kami_09_Well"));
        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public void DeletingARouteForgetsItsBest()
    {
        var root = TempDirectory();
        var storage = new RouteStorage(Path.Combine(root, "Routes"));
        var kept = new Route { Name = "Kept", ZoneKey = "Kami/A" };
        var deleted = new Route { Name = "Deleted", ZoneKey = "Kami/A" };
        storage.Save(kept);
        storage.Save(deleted);
        storage.Bests.Record(kept.Id, 50, When);
        storage.Bests.Record(deleted.Id, 40, When);

        storage.Delete(deleted, When);
        // The next new route in the zone gets the deleted one's file name back.
        var replacement = new Route { Name = "Replacement", ZoneKey = "Kami/A" };
        storage.Save(replacement);

        Assert.Equal(deleted.Id, replacement.Id);
        Assert.Null(storage.Bests.Get(replacement.Id));
        Assert.Equal(50, new PersonalBests(Path.Combine(root, "PersonalBests.json")).Get(kept.Id));
        Directory.Delete(root, recursive: true);
    }
}
