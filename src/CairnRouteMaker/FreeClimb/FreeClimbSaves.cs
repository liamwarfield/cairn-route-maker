using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MelonLoader;

namespace CairnRouteMaker.FreeClimb;

/// <summary>
/// The mountains whose summit save slot holds a free climb of ours. Free climb launches as a Summit
/// game, and since On the Trail the game keeps one Summit save per mountain, so free climb shares
/// that slot with the player's On the Trail runs. A mountain is ours from a free-climb launch until
/// the player's own run is seen there. Kept in UserData/CairnRouteMaker/FreeClimbSaves.txt, one
/// "World ticks" line per mountain: the last time we knew the slot was ours.
/// </summary>
internal static class FreeClimbSaves
{
    /// <summary>A slot written this long after we last had it was written by something else.</summary>
    private static readonly TimeSpan Slack = TimeSpan.FromMinutes(5);

    private static readonly Dictionary<string, DateTime> Ours = new();
    private static string _path;

    public static void Initialize(string path)
    {
        _path = path;
        Ours.Clear();
        try
        {
            if (!File.Exists(path))
            {
                return;
            }

            foreach (var line in File.ReadAllLines(path))
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2 && long.TryParse(parts[1], out var ticks))
                {
                    Ours[parts[0]] = new DateTime(ticks, DateTimeKind.Utc);
                }
            }
        }
        catch (Exception e)
        {
            MelonLogger.Warning($"Couldn't read {path}: {e.Message}");
        }
    }

    /// <summary>
    /// Whether the mountain's slot holds our free climb: we launched one there, the player hasn't
    /// played their own run there since, and the save file (if we could find it) hasn't been written
    /// long after our last free climb, by the game running without the mod, say.
    /// </summary>
    public static bool IsOurs(Il2Cpp.StreamingTweakables.World world, DateTime? savedAt)
    {
        if (!Ours.TryGetValue(world.ToString(), out var lastOurs))
        {
            return false;
        }

        return savedAt is not { } written || written <= lastOurs + Slack;
    }

    /// <summary>A free climb was launched there, or is being played there now.</summary>
    public static void Claim(Il2Cpp.StreamingTweakables.World world)
    {
        Ours[world.ToString()] = DateTime.UtcNow;
        Write();
    }

    /// <summary>The player's own run is being played there: the slot is theirs.</summary>
    public static void Release(Il2Cpp.StreamingTweakables.World world)
    {
        if (Ours.Remove(world.ToString()))
        {
            MelonLogger.Msg($"{world}'s summit save is the player's run now; free climb won't replace it without asking.");
            Write();
        }
    }

    /// <summary>
    /// When the game last wrote a summit slot's file, or null if it can't be found. The game saves
    /// under the Windows user's Saved Games folder (Proton's prefix on Linux): SUMMIT/SLOT_A.CPK is slot 0.
    /// </summary>
    public static DateTime? SlotWriteTime(byte slot)
    {
        if (slot >= 26)
        {
            return null;
        }

        try
        {
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var file = Path.Combine(profile, "Saved Games", "TheGameBakers", "Cairn_RETAIL", "SAVEGAMES", "RETAIL", "SUMMIT", $"SLOT_{(char)('A' + slot)}.CPK");
            return File.Exists(file) ? File.GetLastWriteTimeUtc(file) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void Write()
    {
        if (_path == null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllLines(_path, Ours.Select(pair => $"{pair.Key} {pair.Value.Ticks}"));
        }
        catch (Exception e)
        {
            MelonLogger.Warning($"Couldn't write {_path}: {e.Message}");
        }
    }
}
