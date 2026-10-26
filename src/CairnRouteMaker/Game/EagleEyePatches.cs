using System;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;

namespace CairnRouteMaker.Game;

/// <summary>
/// Watches the map's "Display route" button (Alt by default), which shows and hides the path Ava
/// has climbed, so the mod can show and hide its route line along with it.
/// </summary>
internal static class EagleEyePatches
{
    /// <summary>The player pressed "Display route"; true if the game's path is now shown.</summary>
    public static event Action<bool> DisplayRouteToggled;

    [HarmonyPatch(typeof(EagleEyeUI), nameof(EagleEyeUI.OnShowAavaPathInput))]
    private static class ShowPathInputPatch
    {
        private static void Postfix(EagleEyeUI __instance)
        {
            try
            {
                DisplayRouteToggled?.Invoke(__instance.pathOn);
            }
            catch (Exception e)
            {
                MelonLogger.Error($"Failed following the map's Display route button: {e}");
            }
        }
    }
}
