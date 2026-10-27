using System;
using HarmonyLib;
using Il2Cpp;
using Il2CppTheGameBakers.Cairn;
using MelonLoader;

namespace CairnRouteMaker.Game;

/// <summary>
/// Getting out of a death without a level load, as a game that can warp does, leaves the game
/// half in its game-over state. Two fixes:
/// <list type="bullet">
/// <item>The death screen's map can fast travel to a pin. That warp revives Ava but leaves the
/// screen open, and with it the GameOver state: time stays stopped and the death music keeps
/// playing. The screen's own retry closes it after warping; this does the same for fast travel.</item>
/// <item>The screen's retry flags the sound manager as going back to the main menu, which only a
/// level load clears. While it's set, returning to the game leaves the sound engine's game state
/// at game over (no sound effects) and stops the music without starting it again. So when the
/// screen closes in free climb, the flag is cleared and the in-game sound state and music are
/// set again.</item>
/// </list>
/// </summary>
internal static class GameOverPatches
{
    private static bool _warped;

    [HarmonyPatch(typeof(FreeRoamManager), nameof(FreeRoamManager.WarpToPoint))]
    private static class WarpPatch
    {
        private static void Prefix() => _warped = true;
    }

    [HarmonyPatch(typeof(GameOverMenu), nameof(GameOverMenu.OnClosing))]
    private static class ClosePatch
    {
        private static void Postfix()
        {
            try
            {
                var game = GlobalGameManager.Instance;
                if (!GameApi.IsFreeClimbGame || game == null || game.CurrentGameState != GlobalGameManager.GameState.InGame)
                {
                    return;
                }

                GameApi.RestoreInGameSound();
            }
            catch (Exception e)
            {
                MelonLogger.Error($"Failed restoring the sound after the game-over screen: {e}");
            }
        }
    }

    [HarmonyPatch(typeof(EagleEyeUI), nameof(EagleEyeUI.OnReloadSaveInput))]
    private static class FastTravelPatch
    {
        private static void Prefix(out bool __state)
        {
            _warped = false;
            __state = false;
            try
            {
                __state = GameApi.IsGameOver;
            }
            catch (Exception e)
            {
                MelonLogger.Error($"Failed checking for game over before fast travel: {e}");
            }
        }

        private static void Postfix(bool __state)
        {
            if (__state && _warped)
            {
                MelonLogger.Msg("Fast travel from the game-over screen.");
                GameApi.CloseGameOverMenu();
            }
        }
    }
}
