using System.Collections.Generic;

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
    };

    /// <summary>The readable name of a zone scene, or the name itself if there isn't one.</summary>
    public static string Pretty(string zone) => zone != null && Names.TryGetValue(zone, out var name) ? name : zone;

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
