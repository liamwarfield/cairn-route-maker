using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace CairnRouteMaker.Routes;

/// <summary>
/// Reads and writes route files in one directory. A zone can have any number of routes, one file
/// each, named after the zone: "Kami_09_Well.json", then "Kami_09_Well-2.json" and so on. The
/// zone a route belongs to is read from the file itself, not its name.
/// </summary>
public sealed class RouteStorage
{
    private const string InvalidChars = "<>:\"/\\|?*";

    private readonly string _directory;

    public RouteStorage(string directory) => _directory = directory;

    /// <summary>
    /// Every readable saved route, by zone and then in the order they were made; unreadable files
    /// are skipped.
    /// </summary>
    public List<Route> LoadAll()
    {
        var routes = new List<Route>();
        if (!Directory.Exists(_directory))
        {
            return routes;
        }

        foreach (var file in Directory.EnumerateFiles(_directory, "*.json"))
        {
            try
            {
                var route = RouteFile.Deserialize(File.ReadAllText(file));
                route.Id = Path.GetFileNameWithoutExtension(file);
                routes.Add(route);
            }
            catch (Exception e) when (e is IOException or InvalidDataException or System.Text.Json.JsonException)
            {
                // A broken or newer file shouldn't hide the others.
            }
        }

        routes.Sort(Compare);
        return routes;
    }

    /// <summary>A new, unsaved route in a zone, called "Route N" where N counts it among the zone's routes.</summary>
    public Route Create(string zoneKey)
    {
        var existing = LoadAll().FindAll(route => route.ZoneKey == zoneKey);
        var number = existing.Count + 1;
        while (existing.Exists(route => route.Name == $"Route {number}"))
        {
            number++;
        }

        return new Route { Name = $"Route {number}", ZoneKey = zoneKey };
    }

    /// <summary>
    /// Remove a route by moving its file to a Backups folder next to the routes folder, stamped
    /// with the time, so a deletion can always be undone by moving the file back.
    /// </summary>
    /// <returns>Where the file went, or null if the route wasn't saved.</returns>
    public string Delete(Route route, DateTime now)
    {
        if (route.Id == null || !File.Exists(PathFor(route.Id)))
        {
            return null;
        }

        var backups = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(_directory)) ?? _directory, "Backups");
        Directory.CreateDirectory(backups);
        var backup = Path.Combine(backups, $"{route.Id}.{now:yyyyMMdd-HHmmss}.json");
        File.Move(PathFor(route.Id), backup, overwrite: true);
        return backup;
    }

    /// <summary>Write a route to its file. A route saved for the first time gets the zone's next free file name.</summary>
    /// <returns>The file's path.</returns>
    public string Save(Route route)
    {
        Directory.CreateDirectory(_directory);
        route.Id ??= FreeId(route.ZoneKey);
        var path = PathFor(route.Id);
        var temp = path + ".tmp";
        File.WriteAllText(temp, RouteFile.Serialize(route));
        File.Move(temp, path, overwrite: true);
        return path;
    }

    private string PathFor(string id) => Path.Combine(_directory, id + ".json");

    private string FreeId(string zoneKey)
    {
        // Use Windows' rules on every platform so shared route files keep the same names.
        var stem = new string(zoneKey.Select(c => c < 32 || InvalidChars.Contains(c) ? '_' : c).ToArray());
        if (stem.Length == 0)
        {
            stem = "unknown";
        }

        // After the highest number so far, not in a gap a deletion left, so file order stays the
        // order routes were made in.
        var next = 1;
        foreach (var file in Directory.EnumerateFiles(_directory, stem + "*.json"))
        {
            var id = Path.GetFileNameWithoutExtension(file);
            var (fileStem, number) = SplitId(id);
            if (id == stem || fileStem == stem)
            {
                next = Math.Max(next, (id == stem ? 1 : number) + 1);
            }
        }

        var free = next == 1 ? stem : $"{stem}-{next}";
        while (File.Exists(PathFor(free)))
        {
            free = $"{stem}-{++next}";
        }

        return free;
    }

    /// <summary>By zone, then by file, counting the number at the end: "Well", "Well-2", "Well-10".</summary>
    private static int Compare(Route a, Route b)
    {
        var byZone = string.CompareOrdinal(a.ZoneKey, b.ZoneKey);
        if (byZone != 0)
        {
            return byZone;
        }

        var (stemA, numberA) = SplitId(a.Id);
        var (stemB, numberB) = SplitId(b.Id);
        var byStem = string.CompareOrdinal(stemA, stemB);
        return byStem != 0 ? byStem : numberA.CompareTo(numberB);
    }

    /// <summary>"Kami_09_Well-2" is ("Kami_09_Well", 2); an id without a number is the first.</summary>
    private static (string Stem, int Number) SplitId(string id)
    {
        var dash = id.LastIndexOf('-');
        return dash > 0 && int.TryParse(id[(dash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            ? (id[..dash], number)
            : (id, 1);
    }
}
