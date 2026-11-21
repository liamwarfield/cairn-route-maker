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
/// Adds a "Free Climb" button under "Story" on the title screen (MainMenuModeSelectElement), and a
/// "Loading Screen Art" button at the bottom of the Extras page (MainMenuExtraSelectElement). Each
/// button is a copy of one of the page's own buttons with its localisation removed and its click
/// replaced. It's added to the element's GetButtons() so controller navigation and the selection
/// effects include it.
/// </summary>
internal static class MainMenuPatches
{
    private const string ButtonName = "CairnRouteMaker.FreeClimbButton";
    private const string Label = "Free Climb";
    private const string ArtButtonName = "CairnRouteMaker.LoadingArtButton";
    private const string ArtLabel = "Loading Screen Art";

    /// <summary>The Free Climb button was pressed.</summary>
    public static event Action FreeClimbClicked;

    /// <summary>The Extras page's Loading Screen Art button was pressed.</summary>
    public static event Action LoadingArtClicked;

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

            var extras = __instance.TryCast<MainMenuExtraSelectElement>();
            if (extras != null)
            {
                Run("adding the Loading Screen Art button", () => EnsureArtButton(extras));
            }
        }
    }

    /// <summary>The main menu scene has loaded: whatever game was running is over.</summary>
    [HarmonyPatch(typeof(MainMenu), nameof(MainMenu.Awake))]
    private static class MainMenuAwakePatch
    {
        private static void Postfix() => Run("handling the main menu", () => MainMenuShown?.Invoke());
    }

    private static readonly List<Func<bool>> CancelBlockers = new();

    /// <summary>While any of these returns true, the menu's cancel action (B / Esc) is ignored.</summary>
    public static void SuppressCancelWhile(Func<bool> blocked) => CancelBlockers.Add(blocked);

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
                foreach (var blocked in CancelBlockers)
                {
                    if (blocked())
                    {
                        return false;
                    }
                }

                return true;
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

    [HarmonyPatch(typeof(MainMenuExtraSelectElement), nameof(MainMenuExtraSelectElement.GetButtons))]
    private static class ExtrasGetButtonsPatch
    {
        private static void Postfix(MainMenuExtraSelectElement __instance, ref Il2CppReferenceArray<Button> __result)
        {
            var buttons = __result;
            Run("listing the Loading Screen Art button", () => buttons = WithArt(__instance, buttons));
            __result = buttons;
        }
    }

    /// <summary>The Extras page's buttons with ours added at the end, where it sits on screen.</summary>
    private static Il2CppReferenceArray<Button> WithArt(MainMenuExtraSelectElement element, Il2CppReferenceArray<Button> buttons)
    {
        var art = EnsureArtButton(element);
        if (art == null || buttons == null)
        {
            return buttons;
        }

        var list = new List<Button>();
        foreach (var button in buttons)
        {
            if (button != null && button.Pointer == art.Pointer)
            {
                return buttons;
            }

            list.Add(button);
        }

        list.Add(art);
        return new Il2CppReferenceArray<Button>(list.ToArray());
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

        var existing = story.transform.parent.Find(ButtonName);
        if (existing != null)
        {
            return existing.GetComponent<Button>();
        }

        var button = CloneButton(story, ButtonName, Label, "opening Free Climb", () => FreeClimbClicked?.Invoke());
        PlaceBelow(story, button.transform, element);
        MelonLogger.Msg($"Added the Free Climb button under {story.name} (in {story.transform.parent.name}, layout group: {story.transform.parent.GetComponent<LayoutGroup>() != null}).");
        LogPositions(story, element.summitButton, button, element.settingsButton, element.extrasButton, element.creditsButton, element.quitButton);
        return button;
    }

    /// <summary>
    /// Find the Extras page's Loading Screen Art button, creating it the first time: a copy of the
    /// Deluxe PDF button, one row below the page's last button.
    /// </summary>
    private static Button EnsureArtButton(MainMenuExtraSelectElement element)
    {
        var pdf = element.deluxePDFButton;
        Button tshirt = element.tshirtButton;
        var template = pdf ?? tshirt;
        if (template == null)
        {
            return null;
        }

        var existing = template.transform.parent.Find(ArtButtonName);
        if (existing != null)
        {
            return existing.GetComponent<Button>();
        }

        var button = CloneButton(template, ArtButtonName, ArtLabel, "opening the loading screen art", () => LoadingArtClicked?.Invoke());
        PlaceLast(button.transform, pdf, tshirt);
        MelonLogger.Msg($"Added the Loading Screen Art button to Extras (copied from {template.name} in {template.transform.parent.name}, layout group: {template.transform.parent.GetComponent<LayoutGroup>() != null}).");
        LogPositions(pdf, tshirt, button);
        return button;
    }

    /// <summary>A copy of one of the menu's buttons, beside it, with its own label and click.</summary>
    private static Button CloneButton(Button template, string name, string label, string what, Action onClick)
    {
        var copy = Object.Instantiate(template.gameObject, template.transform.parent, false);
        copy.name = name;
        copy.SetActive(true);

        // The copy would otherwise translate itself back to the original's label.
        foreach (var localized in copy.GetComponentsInChildren<LocalizedText>(true))
        {
            Object.DestroyImmediate(localized);
        }

        foreach (var text in copy.GetComponentsInChildren<TextMeshProUGUI>(true))
        {
            text.text = label;
        }

        var button = copy.GetComponent<Button>();
        button.onClick = new Button.ButtonClickedEvent();
        button.onClick.AddListener((UnityAction)(() => Run(what, onClick)));
        button.interactable = true;
        return button;
    }

    private static void LogPositions(params Button[] buttons)
    {
        foreach (var button in buttons)
        {
            if (button != null)
            {
                MelonLogger.Msg($"  {button.name}: active={button.gameObject.activeSelf} at {button.GetComponent<RectTransform>().anchoredPosition}");
            }
        }
    }

    /// <summary>
    /// Put the copy one row below the lowest of <paramref name="others"/> that's showing. In a layout
    /// group being the last sibling is enough.
    /// </summary>
    private static void PlaceLast(Transform copy, params Button[] others)
    {
        copy.SetAsLastSibling();
        if (copy.parent.GetComponent<LayoutGroup>() != null)
        {
            return;
        }

        var shown = new List<RectTransform>();
        foreach (var button in others)
        {
            if (button != null && button.gameObject.activeSelf && button.transform.parent == copy.parent)
            {
                shown.Add(button.GetComponent<RectTransform>());
            }
        }

        if (shown.Count == 0)
        {
            return;
        }

        shown.Sort((a, b) => a.anchoredPosition.y.CompareTo(b.anchoredPosition.y));
        var lowest = shown[0];
        var row = shown.Count > 1 ? shown[1].anchoredPosition.y - lowest.anchoredPosition.y : lowest.rect.height;
        if (row <= 0f)
        {
            row = lowest.rect.height;
        }

        copy.GetComponent<RectTransform>().anchoredPosition = lowest.anchoredPosition - new Vector2(0f, row);
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
        foreach (var button in new[] { element.summitButton, element.settingsButton, element.extrasButton, element.creditsButton, element.steamPageButton, element.quitButton })
        {
            // Hidden buttons (the DLC's, in some builds) keep their old slot and would skew the row height.
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
