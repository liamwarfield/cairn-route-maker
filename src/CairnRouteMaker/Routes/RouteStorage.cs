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
