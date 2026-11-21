using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Il2Cpp;
using Il2CppTheGameBakers.Cairn.Save;
using MelonLoader;

namespace CairnRouteMaker.Game;

/// <summary>
/// Free climb with the original climbing: health and stamina, not On the Trail's energy. Since 1.1
/// <c>GameSetup.summitMode</c> means On the Trail: <c>UseEnergyInsteadOfHealth</c> and
/// <c>RequiresGearUp</c> just return it, and some 280 places read it live after
/// <c>GameSetup.GetCurrentGameSetup()</c>. Free climb still launches as a Summit game, for its save
/// slot. Once Ava is in, <see cref="Apply"/> clears <c>summitMode</c> on the running setup, so she
/// climbs the way she does in story games, which is how Summit climbing worked before 1.1.
///
/// The save code picks the save's folder, Story or Summit, from the same flag, and a free climb
/// must never touch the player's story saves. So while free climb is played:
/// <list type="bullet">
/// <item>the SavegameManager methods that read the flag run with it set again (a summit scope), so
/// free climb goes on saving to, loading from and deleting its own summit slot;</item>
/// <item>a load, delete or slot change that names the story channel outright, as the game-over and
/// map screens do from the flag, goes to the summit one instead.</item>
/// </list>
/// See docs/game-internals.md.
/// </summary>
internal static class ClimbingModePatches
{
    /// <summary>SavegameManager's methods that read the running setup to pick Story or Summit.</summary>
    private static readonly HashSet<string> SummitScoped = new()
    {
        "AutoSave", "ChangeActiveSlot", "Delete", "EnsureActivePackageOpenedForWrite", "GetActiveSlot",
        "GlobalGaleManager_OnGameStart", "Load", "QuickSave", "Reload", "Save", "SaveAndClose",
    };

    /// <summary>Whether a free climb is being played. Set by FreeClimbSession.</summary>
    public static Func<bool> InFreeClimb = () => false;

    private static int _summitScopes;

    /// <summary>A free climb is being played: keep its setup off On the Trail's rules.</summary>
    public static void Apply()
    {
        if (_summitScopes == 0 && SetSummitMode(false))
        {
            MelonLogger.Msg("Free climb climbs with health and stamina, not On the Trail's energy.");
        }
    }

    /// <summary>The free climb is over: give its setup back to the game as the game made it.</summary>
    public static void Restore()
    {
        _summitScopes = 0;
        if (GameApi.IsFreeClimbGame)
        {
            SetSummitMode(true);
        }
    }

    /// <summary>Set the running setup's summitMode; whether that changed it.</summary>
    private static bool SetSummitMode(bool on)
    {
        var scenes = CairnSceneManager.Instance;
        if (scenes == null)
        {
            return false;
        }

        var setup = scenes.GameSetup;
        if (setup == null || setup.summitMode == on)
        {
            return false;
        }

        setup.summitMode = on;
        // GameSetup is a struct: this is a copy, so it has to be stored back.
        scenes.GameSetup = setup;
        return true;
    }

    [HarmonyPatch]
    private static class SummitScopePatch
    {
        // Overloads taking a SlotInfo name their channel in it and don't read the setup.
        private static IEnumerable<MethodBase> TargetMethods() =>
            typeof(SavegameManager).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
                .Where(method => SummitScoped.Contains(method.Name) && !method.GetParameters().Any(parameter => parameter.ParameterType.IsByRef));

        private static void Prefix(out bool __state)
        {
            __state = false;
            try
            {
                if (!InFreeClimb())
                {
                    return;
                }

                __state = true;
                if (_summitScopes++ == 0)
                {
                    SetSummitMode(true);
                }
            }
            catch (Exception e)
            {
                MelonLogger.Error($"Failed pointing the save code at free climb's summit slot: {e}");
            }
        }

        // A finalizer, so the scope ends even if the game's method throws.
        private static void Finalizer(bool __state)
        {
            if (!__state)
            {
                return;
            }

            try
            {
                if (--_summitScopes <= 0)
                {
                    _summitScopes = 0;
                    SetSummitMode(false);
                }
            }
            catch (Exception e)
            {
                MelonLogger.Error($"Failed putting free climb back on health and stamina after saving: {e}");
            }
        }
    }

    [HarmonyPatch]
    private static class StoryChannelPatch
    {
        private static IEnumerable<MethodBase> TargetMethods() =>
            typeof(SavegameManager).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
                .Where(method => method.Name is "Load" or "Delete" or "ChangeActiveSlot"
                    && method.GetParameters() is { Length: > 0 } parameters && parameters[0].ParameterType == typeof(SaveType));

        private static void Prefix(MethodBase __originalMethod, ref SaveType __0)
        {
            try
            {
                if (__0 == SaveType.Story && InFreeClimb())
                {
                    MelonLogger.Msg($"Free climb asked to {__originalMethod.Name} a story save; using its summit slot instead.");
                    __0 = SaveType.Summit;
                }
            }
            catch (Exception e)
            {
                MelonLogger.Error($"Failed keeping free climb away from the story saves: {e}");
            }
        }
    }
}
