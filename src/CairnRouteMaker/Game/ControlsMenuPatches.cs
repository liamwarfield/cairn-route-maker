using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using CairnRouteMaker.Controls;
using HarmonyLib;
using Il2Cpp;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppTGBTools.Localization;
using Il2CppTheGameBakers.Cairn;
using MelonLoader;
using UnityEngine;
using UnityEngine.InputSystem;

namespace CairnRouteMaker.Game;

/// <summary>
/// Lists the mod's controls (<see cref="ModControls"/>) under "Context: Freecam" and "Context: Route
/// Editor" in Settings > Controls > Remap controls, so the game's own screen rebinds them.
///
/// RebindSheet.Start builds the screen once from two RebindList assets, one for gamepad and one for
/// keyboard/mouse. Each RebindData entry becomes a row, and an entry whose headerLabelLocKey isn't
/// empty gets a heading above it. We add our entries to the end of both lists before Start runs.
/// Labels are localisation keys, so ours are made-up keys that the LocalizationManager.Get patch
/// answers. Rows reach their action through InputActionReferenceWrapper.InputAction, which asks
/// TGBTools' InputManager to resolve it. That returns an action from an asset it has no resolver
/// for (ours) unchanged, so our actions need no registering. See docs/game-internals.md.
/// </summary>
internal static class ControlsMenuPatches
{
    private const string FreecamHeader = "Context: Freecam";
    private const string RouteEditorHeader = "Context: Route Editor";

    // RebindContextStringIdEnum is a flag set; the game uses bits up to 1 << 18. The remap screen
    // flags two rows as clashing when they share a binding and a context bit.
    private const RebindContextStringIdEnum FreecamContext = (RebindContextStringIdEnum)(1 << 24);
    private const RebindContextStringIdEnum RouteEditorContext = (RebindContextStringIdEnum)(1 << 25);

    /// <summary>Our made-up localisation keys and their text.</summary>
    private static readonly Dictionary<int, string> Texts = new();
    private static readonly Dictionary<IntPtr, InputActionReference> References = new();

    [HarmonyPatch(typeof(RebindSheet), nameof(RebindSheet.Start))]
    private static class StartPatch
    {
        private static void Prefix(RebindSheet __instance) => Run("adding the mod's controls to the remap screen", () =>
        {
            ModControls.EnsureCreated();
            AddContexts();
            AddRows(__instance.kbmRebindList, gamepad: false);
            AddRows(__instance.gamepadRebindList, gamepad: true);
        });
    }

    /// <summary>Called after every rebind and reset, ours or the game's.</summary>
    [HarmonyPatch(typeof(RebindSheet), nameof(RebindSheet.OnRebind))]
    private static class OnRebindPatch
    {
        private static void Postfix() => Run("saving the mod's controls", ModControls.OnBindingsChanged);
    }

    [HarmonyPatch(typeof(LocalizationManager), nameof(LocalizationManager.Get), typeof(LocKeyStringId))]
    private static class LocalizationPatch
    {
        private static bool Prefix(LocKeyStringId key, ref string __result)
        {
            if (Texts.Count == 0 || !Texts.TryGetValue(key.value, out var text))
            {
                return true;
            }

            __result = text;
            return false;
        }
    }

    /// <summary>Give our context bits to the clash check, which only looks at the bits in this list.</summary>
    private static void AddContexts()
    {
        FieldRebind<FieldKbmRebind, FieldUIKbmRebind>.contextEnums = WithOurContexts(FieldRebind<FieldKbmRebind, FieldUIKbmRebind>.contextEnums);
        FieldRebind<FieldGamepadRebind, FieldUIGamepadRebind>.contextEnums = WithOurContexts(FieldRebind<FieldGamepadRebind, FieldUIGamepadRebind>.contextEnums);
    }

    private static Il2CppStructArray<RebindContextStringIdEnum> WithOurContexts(Il2CppStructArray<RebindContextStringIdEnum> contexts)
    {
        var values = contexts == null ? new List<RebindContextStringIdEnum>() : contexts.ToList();
        if (values.Contains(FreecamContext))
        {
            return contexts;
        }

        values.Add(FreecamContext);
        values.Add(RouteEditorContext);
        return new Il2CppStructArray<RebindContextStringIdEnum>(values.ToArray());
    }

    private static void AddRows(RebindList list, bool gamepad)
    {
        if (list == null)
        {
            MelonLogger.Warning($"The remap screen has no {(gamepad ? "gamepad" : "keyboard")} list.");
            return;
        }

        var existing = list.rebinds?.ToList() ?? new List<RebindData>();
        if (existing.Any(r => r != null && Texts.ContainsKey(r.labelLocKey.value)))
        {
            // The lists are shared assets, so they keep our rows for the rest of the session.
            return;
        }

        var noHeader = NoHeaderKey(existing);
        var rows = new List<RebindData>();
        ControlSection? section = null;
        foreach (var control in ModControls.All)
        {
            var action = gamepad ? control.Gamepad : control.Keyboard;
            if (action == null)
            {
                continue;
            }

            var header = noHeader;
            if (section != control.Section)
            {
                section = control.Section;
                header = Text(control.Section == ControlSection.Freecam ? FreecamHeader : RouteEditorHeader);
            }

            rows.Add(new RebindData
            {
                labelLocKey = Text(gamepad ? control.GamepadLabel : control.Label),
                headerLabelLocKey = header,
                actionWrapper = new InputActionReferenceWrapper { reference = Reference(action) },
                rebindContext = Context(control.Modes),
                // The game looks the binding up by id, and throws on null.
                firstBindingId = ModControls.BindingId(control, gamepad),
                secondBindingId = "",
                // The row's constructor walks this without a null check.
                subActionWrappers = new Il2CppReferenceArray<InputActionReferenceWrapper>(0),
            });
        }

        list.rebinds = new Il2CppReferenceArray<RebindData>(existing.Concat(rows).ToArray());
        MelonLogger.Msg($"Added {rows.Count} mod controls to the {(gamepad ? "gamepad" : "keyboard/mouse")} remap list.");
    }

    /// <summary>
    /// The key that means "no heading". The game compares against a static we can't name, so use
    /// the value most of its own rows have, since only the first row of each context has a heading.
    /// </summary>
    private static LocKeyStringId NoHeaderKey(List<RebindData> rows)
    {
        var common = rows.Where(r => r != null)
            .GroupBy(r => r.headerLabelLocKey.value)
            .OrderByDescending(g => g.Count())
            .Select(g => (int?)g.Key)
            .FirstOrDefault();
        return new LocKeyStringId(common ?? 0);
    }

    private static RebindContextStringIdEnum Context(ControlModes modes) =>
        (modes.HasFlag(ControlModes.Freecam) ? FreecamContext : 0) | (modes.HasFlag(ControlModes.RouteEditor) ? RouteEditorContext : 0);

    private static InputActionReference Reference(InputAction action)
    {
        if (!References.TryGetValue(action.Pointer, out var reference) || reference == null)
        {
            reference = InputActionReference.Create(action);
            // Only our RebindData points at it, which doesn't keep it loaded across scene changes.
            reference.hideFlags = HideFlags.DontUnloadUnusedAsset;
            References[action.Pointer] = reference;
        }

        return reference;
    }

    /// <summary>A made-up localisation key for some text, steering clear of the game's keys.</summary>
    private static LocKeyStringId Text(string text)
    {
        var id = BitConverter.ToInt32(MD5.HashData(Encoding.UTF8.GetBytes("CairnRouteMaker/" + text)), 0);
        while (id == 0 || Enum.IsDefined(typeof(LocKeyStringIdEnum), id) || (Texts.TryGetValue(id, out var other) && other != text))
        {
            id++;
        }

        Texts[id] = text;
        return new LocKeyStringId(id);
    }

    private static IEnumerable<(Control Control, InputAction Action)> Actions()
    {
        foreach (var control in ModControls.All)
        {
            if (control.Keyboard != null)
            {
                yield return (control, control.Keyboard);
            }

            if (control.Gamepad != null)
            {
                yield return (control, control.Gamepad);
            }
        }
    }

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
