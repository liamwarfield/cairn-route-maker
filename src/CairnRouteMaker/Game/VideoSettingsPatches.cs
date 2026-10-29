using System;
using System.Linq;
using CairnRouteMaker.Editor;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppTGBTools.UI;
using Il2CppTheGameBakers.Cairn.UI;
using MelonLoader;

namespace CairnRouteMaker.Game;

/// <summary>
/// Adds a "Route paint distance" row to the game's Video settings page, with the levels in
/// <see cref="PaintColors.PaintDistanceLevels"/>. A settings page builds its rows in code, as
/// fields handed to the menu's shared FieldsUI, so the row is a field of the same kind as the
/// game's quality rows (a FieldListArrows), added after the page's own when it opens.
/// See docs/game-internals.md.
/// </summary>
internal static class VideoSettingsPatches
{
    private const string Label = "Route paint distance";

    [HarmonyPatch(typeof(VideoSettingsPage), nameof(VideoSettingsPage.OnOpened))]
    private static class OpenedPatch
    {
        private static void Postfix(VideoSettingsPage __instance) => Run("adding the route paint distance to the Video settings", () => AddField(__instance));
    }

    private static void AddField(VideoSettingsPage page)
    {
        var fieldsUI = page.SettingsMenu?.FieldsUI;
        if (fieldsUI == null || fieldsUI.Instances.ToArray().Any(ui => ui != null && ui.Field?.Label?.Value == Label))
        {
            return;
        }

        var levels = PaintColors.PaintDistanceLevels;
        var values = new Il2CppReferenceArray<FieldLabel>(levels.Length);
        for (var i = 0; i < levels.Length; i++)
        {
            values[i] = new FieldLabel(PaintColors.DescribePaintDistance(levels[i].Metres));
        }

        var field = new FieldListArrows(
            new FieldLabel(Label),
            values,
            PaintColors.LevelOf(PaintColors.PaintDistance),
            (Action<int, int>)((_, next) => Run("changing the route paint distance", () => PaintColors.SetPaintDistanceLevel(next))),
            new Il2CppSystem.Nullable<int>(PaintColors.DefaultPaintDistanceLevel));
        fieldsUI.Add(field);
    }

    /// <summary>Exceptions must not escape into the game's own menu code.</summary>
    private static void Run(string what, Action action)
    {
        try
        {
            action();
        }
        catch (Exception e)
        {
            MelonLogger.Error($"Failed {what}: {e}");
        }
    }
}
