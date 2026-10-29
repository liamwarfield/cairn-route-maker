using System;
using HarmonyLib;
using Il2Cpp;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppTGBTools.UI;
using Il2CppTheGameBakers.Cairn.UI;
using Il2CppTMPro;
using MelonLoader;
using UnityEngine.Events;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace CairnRouteMaker.Game;

/// <summary>
/// Adds a "Routes" tab to the game's Settings, below Audio, on the title screen and in the
/// pause menu alike. It's a copy of the Audio button, but instead of opening a settings page it
/// raises <see cref="RoutesClicked"/>, which opens the mod's own Routes page over the Settings
/// screen. See docs/game-internals.md.
/// </summary>
internal static class SettingsMenuPatches
{
    private const string ButtonName = "CairnRouteMaker.SettingsPageButton";
    private const string Label = "Routes";

    /// <summary>The Routes tab was chosen, in this Settings screen, from this button.</summary>
    public static event Action<SettingsMenu, Selectable> RoutesClicked;

    /// <summary>
    /// The arrow zone around the page buttons lists them when it wakes, for the bouncing arrow, so
    /// ours has to be there by then. This runs before anything opens the menu.
    /// </summary>
    [HarmonyPatch(typeof(BouncingArrowZone), nameof(BouncingArrowZone.Awake))]
    private static class ArrowZoneAwakePatch
    {
        private static void Prefix(BouncingArrowZone __instance) => Run("adding the Routes settings tab", () =>
        {
            var buttons = __instance.GetComponentsInChildren<SettingsPageButton>(true);
            if (buttons.Length > 0)
            {
                AddButton(buttons, __instance.GetComponent<BouncingButtons>());
            }
        });
    }

    /// <summary>Copy the last page button (Audio) into a Routes button just below it.</summary>
    private static void AddButton(Il2CppArrayBase<SettingsPageButton> buttons, BouncingButtons bouncing)
    {
        SettingsPageButton template = null;
        foreach (var button in buttons)
        {
            if (button.name == ButtonName)
            {
                return;
            }

            template = button;
        }

        var menu = template.settingsMenu;
        var templatePage = template.settingsPage;
        var copy = Object.Instantiate(template.gameObject, template.transform.parent, false);
        copy.name = ButtonName;
        copy.transform.SetSiblingIndex(template.transform.GetSiblingIndex() + 1);

        // The copy's Awake pointed the template's page back at the copy. The copy keeps its
        // reference to that page, but never opens it: its click is replaced below.
        if (templatePage != null)
        {
            templatePage.settingsMenuButton = template;
        }

        // The copy would otherwise translate itself back to the template's label.
        foreach (var localized in copy.GetComponentsInChildren<LocalizationUI>(true))
        {
            Object.DestroyImmediate(localized);
        }

        foreach (var text in copy.GetComponentsInChildren<TextMeshProUGUI>(true))
        {
            text.text = Label;
        }

        var selectable = copy.GetComponent<Button>();
        selectable.onClick = new Button.ButtonClickedEvent();
        selectable.onClick.AddListener((UnityAction)(() => Run("opening the Routes page", () => RoutesClicked?.Invoke(menu, selectable))));

        LinkBelow(template.GetComponent<Selectable>(), selectable);
        AddToBouncingButtons(bouncing, copy.GetComponent<ButtonWithMoreEvents>());
        MelonLogger.Msg($"Added the Routes settings tab below {template.name}.");
    }

    /// <summary>
    /// BouncingButtons lists the page buttons the first time it's asked for them; it turns them on
    /// and off as the menu opens and closes, and slides the selected one's label. SettingsMenu.Awake
    /// can get there first (it turns them all off), and then our copy, made from a button that's
    /// off, would never be turned on. So add it to the list as the list itself would have.
    /// </summary>
    private static void AddToBouncingButtons(BouncingButtons bouncing, ButtonWithMoreEvents button)
    {
        // The field, not the ButtonsData property, which would make the list.
        var data = bouncing == null ? null : bouncing.buttonsData;
        if (data == null || button == null || data.ContainsKey(button))
        {
            return;
        }

        data.Add(button, new BouncingButtons.ButtonData(button.GetComponentInChildren<TextMeshProUGUI>()));
        button.add_OnPointerEntered((Action<ButtonWithMoreEvents>)bouncing.SelectOnPointerEnter);
    }

    /// <summary>The page buttons navigate explicitly: put <paramref name="added"/> between <paramref name="above"/> and whatever was below it.</summary>
    private static void LinkBelow(Selectable above, Selectable added)
    {
        var aboveNavigation = above.navigation;
        var below = aboveNavigation.selectOnDown;

        // A copy of the template's, so moving sideways goes where it does from the template.
        var navigation = above.navigation;
        navigation.selectOnUp = above;
        navigation.selectOnDown = below;
        added.navigation = navigation;

        aboveNavigation.selectOnDown = added;
        above.navigation = aboveNavigation;
        if (below != null)
        {
            var belowNavigation = below.navigation;
            if (belowNavigation.selectOnUp == above)
            {
                belowNavigation.selectOnUp = added;
                below.navigation = belowNavigation;
            }
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
