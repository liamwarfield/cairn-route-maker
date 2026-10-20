using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2Cpp;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppTMPro;
using Il2CppTheGameBakers.Cairn.UI;
using MelonLoader;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace CairnRouteMaker.Game;

/// <summary>
/// Adds a "Free Climb" button under "Story" on the title screen (MainMenuModeSelectElement). The
/// button is a copy of the Story button with its localisation removed and its click replaced. It's
/// added to the element's GetButtons() so controller navigation and the selection effects include it.
/// </summary>
internal static class MainMenuPatches
{
    private const string ButtonName = "CairnRouteMaker.FreeClimbButton";
    private const string Label = "Free Climb";

    /// <summary>The Free Climb button was pressed.</summary>
    public static event Action FreeClimbClicked;

    /// <summary>The main menu has loaded (at startup, or after quitting a game).</summary>
    public static event Action MainMenuShown;

    /// <summary>
    /// The base class records button positions when it initialises, so the button must exist by then.
    /// Initialize lives on the base class, hence the type check.
    /// </summary>
    [HarmonyPatch(typeof(MainMenuElement), nameof(MainMenuElement.Initialize))]
    private static class InitializePatch
    {
        private static void Prefix(MainMenuElement __instance)
        {
            var modeSelect = __instance.TryCast<MainMenuModeSelectElement>();
            if (modeSelect != null)
            {
                Run("adding the Free Climb button", () => EnsureButton(modeSelect));
            }
        }
    }

    /// <summary>The main menu scene has loaded: whatever game was running is over.</summary>
    [HarmonyPatch(typeof(MainMenu), nameof(MainMenu.Awake))]
    private static class MainMenuAwakePatch
    {
        private static void Postfix() => Run("handling the main menu", () => MainMenuShown?.Invoke());
    }

    /// <summary>While true, the menu's cancel action (B / Esc) is ignored.</summary>
    public static Func<bool> SuppressCancel { get; set; }

    /// <summary>
    /// The menu's cancel action, wired to the Input System directly, so blocking UI input doesn't stop
    /// it. On the title screen it goes back to "press start"; it mustn't fire while our menu is open.
    /// </summary>
    [HarmonyPatch(typeof(MainMenuElement), nameof(MainMenuElement.Cancel_performed))]
    private static class CancelPatch
    {
        private static bool Prefix()
        {
            try
            {
                return SuppressCancel == null || !SuppressCancel();
            }
            catch (Exception e)
            {
                MelonLogger.Error($"Failed checking the menu's cancel: {e}");
                return true;
            }
        }
    }

    [HarmonyPatch(typeof(MainMenuModeSelectElement), nameof(MainMenuModeSelectElement.GetButtons))]
    private static class GetButtonsPatch
    {
        private static void Postfix(MainMenuModeSelectElement __instance, ref Il2CppReferenceArray<Button> __result)
        {
            var buttons = __result;
            Run("listing the Free Climb button", () => buttons = WithFreeClimb(__instance, buttons));
            __result = buttons;
        }
    }

    private static Il2CppReferenceArray<Button> WithFreeClimb(MainMenuModeSelectElement element, Il2CppReferenceArray<Button> buttons)
    {
        var freeClimb = EnsureButton(element);
        if (freeClimb == null || buttons == null)
        {
            return buttons;
        }

        var list = new List<Button>();
        var inserted = false;
        foreach (var button in buttons)
        {
            if (button != null && button.Pointer == freeClimb.Pointer)
            {
                return buttons;
            }

            list.Add(button);
            if (!inserted && button != null && button.Pointer == element.storyModeButton.Pointer)
            {
                list.Add(freeClimb);
                inserted = true;
            }
        }

        if (!inserted)
        {
            list.Insert(Math.Min(1, list.Count), freeClimb);
        }

        return new Il2CppReferenceArray<Button>(list.ToArray());
    }

    /// <summary>Find this menu's Free Climb button, creating it the first time.</summary>
    private static Button EnsureButton(MainMenuModeSelectElement element)
    {
        var story = element.storyModeButton;
        if (story == null)
        {
            return null;
        }

        var parent = story.transform.parent;
        var existing = parent.Find(ButtonName);
        if (existing != null)
        {
            return existing.GetComponent<Button>();
        }

        var copy = Object.Instantiate(story.gameObject, parent, false);
        copy.name = ButtonName;

        // The copy would otherwise translate itself back to "Story".
        foreach (var localized in copy.GetComponentsInChildren<LocalizedText>(true))
        {
            Object.DestroyImmediate(localized);
        }

        foreach (var text in copy.GetComponentsInChildren<TextMeshProUGUI>(true))
        {
            text.text = Label;
        }

        var button = copy.GetComponent<Button>();
        button.onClick = new Button.ButtonClickedEvent();
        button.onClick.AddListener((UnityAction)(() => Run("opening Free Climb", () => FreeClimbClicked?.Invoke())));
        button.interactable = true;

        PlaceBelow(story, copy.transform, element);
        MelonLogger.Msg($"Added the Free Climb button under {story.name} (in {parent.name}, layout group: {parent.GetComponent<LayoutGroup>() != null}).");
        foreach (var other in new[] { story, element.summitModeButton, button, element.settingsButton, element.extrasButton, element.creditsButton, element.quitButton })
        {
            if (other != null)
            {
                MelonLogger.Msg($"  {other.name}: active={other.gameObject.activeSelf} at {other.GetComponent<RectTransform>().anchoredPosition}");
            }
        }
        return button;
    }

    /// <summary>
    /// Put the copy right under Story. In a layout group the sibling order is enough; otherwise
    /// move it down one row and push the buttons below Story down to make room.
    /// </summary>
    private static void PlaceBelow(Button story, Transform copy, MainMenuModeSelectElement element)
    {
        copy.SetSiblingIndex(story.transform.GetSiblingIndex() + 1);
        if (story.transform.parent.GetComponent<LayoutGroup>() != null)
        {
            return;
        }

        var storyRect = story.GetComponent<RectTransform>();
        var copyRect = copy.GetComponent<RectTransform>();
        var below = new List<RectTransform>();
        foreach (var button in new[] { element.summitModeButton, element.settingsButton, element.extrasButton, element.creditsButton, element.steamPageButton, element.quitButton })
        {
            // Hidden buttons (Summit, in some builds) keep their old slot and would skew the row height.
            if (button != null && button.gameObject.activeSelf && button.transform.parent == story.transform.parent)
            {
                var rect = button.GetComponent<RectTransform>();
                if (rect.anchoredPosition.y < storyRect.anchoredPosition.y)
                {
                    below.Add(rect);
                }
            }
        }

        // One row is the gap from Story to the nearest button below it.
        var row = 0f;
        foreach (var rect in below)
        {
            var gap = storyRect.anchoredPosition.y - rect.anchoredPosition.y;
            row = row == 0f ? gap : Mathf.Min(row, gap);
        }

        if (row == 0f)
        {
            row = storyRect.rect.height;
        }

        copyRect.anchoredPosition = storyRect.anchoredPosition - new Vector2(0f, row);
        foreach (var rect in below)
        {
            rect.anchoredPosition -= new Vector2(0f, row);
        }
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
