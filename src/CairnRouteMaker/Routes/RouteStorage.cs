using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CairnRouteMaker.Routes;

/// <summary>Reads and writes one route file per zone under the given directory.</summary>
public sealed class RouteStorage
{
    private const string InvalidChars = "<>:\"/\\|?*";

    private readonly string _directory;

    public RouteStorage(string directory) => _directory = directory;

    public string PathFor(string zoneKey)
    {
        // Use Windows' rules on every platform so shared route files keep the same names.
        var safe = new string(zoneKey.Select(c => c < 32 || InvalidChars.Contains(c) ? '_' : c).ToArray());
        return Path.Combine(_directory, (safe.Length == 0 ? "unknown" : safe) + ".json");
    }

    public bool Exists(string zoneKey) => File.Exists(PathFor(zoneKey));

    /// <summary>Every readable saved route, ordered by zone; unreadable files are skipped.</summary>
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
                routes.Add(RouteFile.Deserialize(File.ReadAllText(file)));
            }
            catch (Exception e) when (e is IOException or InvalidDataException or System.Text.Json.JsonException)
            {
                // A broken or newer file shouldn't hide the others.
            }
        }

        routes.Sort((a, b) => string.CompareOrdinal(a.ZoneKey, b.ZoneKey));
        return routes;
    }

    public Route Load(string zoneKey) => RouteFile.Deserialize(File.ReadAllText(PathFor(zoneKey)));

    public string Save(Route route)
    {
        Directory.CreateDirectory(_directory);
        var path = PathFor(route.ZoneKey);
        var temp = path + ".tmp";
        File.WriteAllText(temp, RouteFile.Serialize(route));
        File.Move(temp, path, overwrite: true);
        return path;
    }
}
