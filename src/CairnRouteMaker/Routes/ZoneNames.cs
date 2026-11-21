using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace CairnRouteMaker.Routes;

/// <summary>
/// Readable names for the game's zone scenes ("04_SplitRock" → "Split Rock"), for menus only:
/// route files, logs and travel keep the real names. Zones not listed show their real name.
/// </summary>
internal static class ZoneNames
{
    // Kami's zones, from the Addressables catalog. Leftover duplicates (29_Summit, 24_SnowWall,
    // 14_FirstGuardian) are left out so they can't be mistaken for the real ones.
    private static readonly Dictionary<string, string> Names = new()
    {
        ["01_FirstRidge"] = "First Ridge",
        ["02_Crag"] = "Crag",
        ["03_Chimney"] = "Chimney",
        ["04_Bivouac"] = "Bivouac",
        ["04_SplitRock"] = "Split Rock",
        ["05_CableCar"] = "Cable Car",
        ["06_Waterfall"] = "Waterfall",
        ["07_Lunchbreak"] = "Lunch Break",
        ["08_Village"] = "Village",
        ["09_Well"] = "Well",
        ["10_ShepherdOutdoor"] = "Shepherd, Outside",
        ["11_ShepherdIndoor"] = "Shepherd, Inside",
        ["12_FirstGuardian"] = "First Guardian",
        ["13_LakeApproach"] = "Lake Approach",
        ["14_Lake"] = "Lake",
        ["15_MonkeyBridge"] = "Monkey Bridge",
        ["16_WindPalaceOutdoor"] = "Wind Palace, Outside",
        ["17_WindPalaceIndoor"] = "Wind Palace, Inside",
        ["18_SecondGuardian"] = "Second Guardian",
        ["19_GenepiApproach"] = "Génépi Approach",
        ["20_GenepiCliff"] = "Génépi Cliff",
        ["20_SpecialDome"] = "Special Dome",
        ["21_SpecialRidge"] = "Special Ridge",
        ["22_SnowWallLeftExterior"] = "Snow Wall Left, Outside",
        ["23_SnowWall"] = "Snow Wall",
        ["23_SnowWallRightExterior"] = "Snow Wall Right, Outside",
        ["24_SnowWallLeft"] = "Snow Wall Left",
        ["24_ThirdGuardian"] = "Third Guardian",
        ["25_SnowWallRight"] = "Snow Wall Right",
        ["26_LastBridge"] = "Last Bridge",
        ["27_SpikeWall"] = "Spike Wall",
        ["28_Iceland"] = "Iceland",
        ["29_Icefall"] = "Icefall",
        ["30_SnowWalk"] = "Snow Walk",
        ["31_Summit"] = "Summit",

        // Tenzen's zones all share the in-game name "Tenzen", so these come from the scene names.
        ["Tenzing_01_ClimbingGym"] = "Climbing Gym",
        ["Tenzing_02_GymToMountain"] = "Gym to Mountain",
        ["Tenzing_03_MountainBase"] = "Mountain Base",
        ["Tenzing_04_MountainMid"] = "Mid-Mountain",
        ["Tenzing_05_MountainTop"] = "Mountain Top",

        // The On the Trail crags are one zone each, named in game after the crag; these come from
        // the scene names. Later crags fall back to the same (see Pretty).
        ["Crux_01_TowerGym"] = "Tower Gym",
        ["Crux_02_DeepWaterPiton"] = "Deep Water",
        ["Crux_03_Mallorca"] = "Mallorca",
    };

    /// <summary>Mountains whose in-game name differs from the code's (from each world's WorldMeta).</summary>
    private static readonly Dictionary<string, string> Worlds = new()
    {
        ["Tenzing"] = "Tenzen",
        ["Crux_01_TowerGym"] = "Lamu",
        ["Crux_02_DeepWaterPiton"] = "Haken",
        ["Crux_03_Mallorca"] = "Ruka",
        ["Crux_04_Pika"] = "Pika",
    };

    private static readonly Regex CruxName = new(@"^Crux_\d+_(?<name>[A-Za-z]+)$");

    /// <summary>The readable name of a zone scene, or the name itself if there isn't one.</summary>
    public static string Pretty(string zone) => zone != null && Names.TryGetValue(zone, out var name) ? name : FromCrux(zone) ?? zone;

    /// <summary>A mountain as the game names it ("Tenzing" is "Tenzen"), or the name itself.</summary>
    public static string PrettyWorld(string world) => world != null && Worlds.TryGetValue(world, out var name) ? name : FromCrux(world) ?? world;

    private static readonly HashSet<string> SpawnerNoise = new() { "SpawnerMC", "Crux", "Kami", "Lakpa", "Tenzing", "Apa" };

    /// <summary>
    /// A spawn point's object name made readable: "Tower Gym Left" for "Crux_01_TowerGym_SpawnerMC_Left",
    /// "Needle 2" for "Crux_02_Needle_SpawnerMC (1)", "Bivouac" for "SpawnerMC_Kami_04_Bivouac". The
    /// spawner marker, mountain names and zone numbers go; Unity's " (1)" copy suffix becomes a number.
    /// </summary>
    public static string PrettySpawner(string spawner)
    {
        if (string.IsNullOrWhiteSpace(spawner))
        {
            return spawner;
        }

        var name = spawner;
        var suffix = "";
        var copy = Regex.Match(name, @"\s*\((\d+)\)$");
        if (copy.Success)
        {
            suffix = $" {int.Parse(copy.Groups[1].Value) + 1}";
            name = name[..copy.Index];
        }

        var words = name.Split('_', StringSplitOptions.RemoveEmptyEntries)
            .Where(token => !SpawnerNoise.Contains(token) && !token.All(char.IsDigit))
            .Select(token => Regex.Replace(token, "(?<=[a-z])(?=[A-Z])", " "));
        var pretty = string.Join(" ", words);
        return pretty.Length == 0 ? spawner : pretty + suffix;
    }

    /// <summary>"Nunataq" for "Crux_05_Nunataq", "Deep Water Piton" for "Crux_02_DeepWaterPiton"; null for other names.</summary>
    private static string FromCrux(string name)
    {
        var match = name == null ? null : CruxName.Match(name);
        return match is { Success: true } ? Regex.Replace(match.Groups["name"].Value, "(?<=[a-z])(?=[A-Z])", " ") : null;
    }

    /// <summary>The readable zone name of a "World/Zone" key, or the key itself if it isn't one.</summary>
    public static string PrettyKey(string zoneKey)
    {
        var slash = zoneKey?.IndexOf('/') ?? -1;
        return slash < 0 ? zoneKey : Pretty(zoneKey[(slash + 1)..]);
    }

    /// <summary>The world part of a "World/Zone" key, or null.</summary>
    public static string World(string zoneKey)
    {
        var slash = zoneKey?.IndexOf('/') ?? -1;
        return slash < 0 ? null : zoneKey[..slash];
    }
}
