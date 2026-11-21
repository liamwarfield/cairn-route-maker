using System;
using HarmonyLib;
using Il2Cpp;
using Il2CppTheGameBakers.Cairn.UI;
using MelonLoader;
using UnityEngine;

namespace CairnRouteMaker.Game;

/// <summary>
/// Keeps On the Trail's "Pack your bag" screen out of free climb. Since 1.1 a Summit launch shows its
/// loading screen with the WorldSpecific template, and for that template LoadingScreen.SetDisplayed
/// sets RequiresGearUp: the loading screen's prompt then opens the GearUpMenu instead of starting.
/// Kami has nothing set up for it, so the menu can't be left. Free climb shows the Default template
/// instead, the paintings shown before 1.1, which never asks to gear up. Two ways in:
/// <list type="bullet">
/// <item>A launch from the title screen. The game sets up the new run's GameSetup after the loading
/// screen shows, so the launch flags itself (<see cref="OnFreeClimbLaunching"/>) rather than relying on
/// <see cref="GameApi.IsFreeClimbGame"/>, which still describes the last game played.</item>
/// <item>The pause menu's New run, which reloads the running game from inside it.</item>
/// </list>
/// See docs/game-internals.md.
/// </summary>
internal static class LoadingScreenPatches
{
    private enum FreeClimbLoad
    {
        None,
        /// <summary>Launched; its loading screen hasn't shown yet.</summary>
        Launched,
        /// <summary>Its loading screen is showing.</summary>
        Showing,
    }

    /// <summary>
    /// A launch whose loading screen hasn't shown by then never happened (the menu can ignore the
    /// step), so the player's own On the Trail launch after it still gears up.
    /// </summary>
    private const float LaunchTimeoutSeconds = 60f;

    private static FreeClimbLoad _load;
    private static float _launchedAt;

    /// <summary>A free climb is about to launch: its loading screen mustn't ask to gear up.</summary>
    public static void OnFreeClimbLaunching()
    {
        _load = FreeClimbLoad.Launched;
        _launchedAt = Time.realtimeSinceStartup;
    }

    /// <summary>The launch didn't happen after all.</summary>
    public static void OnFreeClimbLaunchFailed() => _load = FreeClimbLoad.None;

    [HarmonyPatch(typeof(LoadingScreen), nameof(LoadingScreen.SetDisplayed))]
    private static class SetDisplayedPatch
    {
        private static void Prefix(bool displayed, ref LoadingScreen.LoadingScreenTemplate template)
        {
            try
            {
                if (!displayed)
                {
                    // Hidden once loaded. A hide before ours showed is the menu's, not ours.
                    if (_load == FreeClimbLoad.Showing)
                    {
                        _load = FreeClimbLoad.None;
                    }

                    return;
                }

                if (_load == FreeClimbLoad.Launched && Time.realtimeSinceStartup - _launchedAt > LaunchTimeoutSeconds)
                {
                    _load = FreeClimbLoad.None;
                }

                if (_load != FreeClimbLoad.None)
                {
                    _load = FreeClimbLoad.Showing;
                }
                else if (!RestartingFreeClimb())
                {
                    return;
                }

                if (template == LoadingScreen.LoadingScreenTemplate.WorldSpecific)
                {
                    template = LoadingScreen.LoadingScreenTemplate.Default;
                    MelonLogger.Msg("Loading free climb without On the Trail's gear-up screen.");
                }
            }
            catch (Exception e)
            {
                MelonLogger.Error($"Failed keeping the gear-up screen out of free climb: {e}");
            }
        }
    }

    /// <summary>
    /// A loading screen shown while a free climb is running: the pause menu's New run. One shown from
    /// the title screen isn't in game, even though the last game's setup is still free climb's.
    /// </summary>
    private static bool RestartingFreeClimb()
    {
        var game = GlobalGameManager.Instance;
        return game != null && game.IsInGame && GameApi.IsFreeClimbGame;
    }
}
