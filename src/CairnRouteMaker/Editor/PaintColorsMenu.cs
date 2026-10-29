using CairnRouteMaker.Game;
using Il2CppTheGameBakers.Cairn.UI;
using MelonLoader;
using UnityEngine;
using UnityEngine.UI;

namespace CairnRouteMaker.Editor;

/// <summary>
/// The Routes page and its paint colour pages, opened from the Routes tab of the game's Settings,
/// on the title screen or in the pause menu. They're the level select's own pages, shown in place of the
/// Settings screen: while they're open it's hidden and its input blocked, and devices are read
/// directly, as in the free-climb launcher. Backing out of the first page goes back to Settings.
/// </summary>
internal sealed class PaintColorsMenu
{
    private readonly LevelSelect _menu = new();
    private readonly EditorInput _input = new();
    private SettingsMenu _settings;
    private Selectable _tab;
    /// <summary>Ours, not the menu's: the menu also closes itself when backed out of its first page.</summary>
    private bool _open;
    private int _openedFrame;
    private int _unblockAtFrame = -1;

    public PaintColorsMenu()
    {
        // On the title screen, B / Esc would also take Settings back to the title menu.
        MainMenuPatches.SuppressCancelWhile(() => _open || _unblockAtFrame >= 0);
    }

    /// <param name="settings">The Settings screen the tab is in.</param>
    /// <param name="tab">The Routes tab, to select again on the way back.</param>
    public void Open(SettingsMenu settings, Selectable tab)
    {
        if (_open || settings == null)
        {
            return;
        }

        _settings = settings;
        _tab = tab;
        _menu.OpenPaintColors();
        _open = true;
        _openedFrame = Time.frameCount;
        _unblockAtFrame = -1;

        // Its Back and Reset are input actions of its own, which the blocks below don't stop.
        settings.ToggleActions(false);
        GameApi.BlockMenuInput(true);
        ShowSettings(false);
    }

    public void Update()
    {
        if (_unblockAtFrame >= 0 && Time.frameCount >= _unblockAtFrame)
        {
            _unblockAtFrame = -1;
            GameApi.BlockMenuInput(false);
            if (IsSettingsShowing)
            {
                _settings.ToggleActions(true);
            }
        }

        if (!_open)
        {
            return;
        }

        // The pause menu can close under us, e.g. when the game ends.
        if (!IsSettingsShowing)
        {
            Close();
            return;
        }

        _input.Read(Time.unscaledDeltaTime);
        // Every frame: the menu fades itself in with tweens when it opens.
        ShowSettings(false);
        LevelSelectView.Show(_menu, _input);

        // Skip the frame the menu opened on, or the press that opened it would choose an entry.
        if (Time.frameCount <= _openedFrame)
        {
            _menu.ClickedIndex = null;
            return;
        }

        if (_input.MenuBack)
        {
            _menu.Back();
            if (!_menu.IsOpen)
            {
                Close();
            }

            return;
        }

        _menu.Move(_input.MenuMove);
        if (_menu.ClickedIndex is { } clicked)
        {
            _menu.Select(clicked);
            _menu.ClickedIndex = null;
            _menu.Current?.Choose();
        }
        else if (_input.MenuConfirm)
        {
            _menu.Current?.Choose();
        }
    }

    private bool IsSettingsShowing => _settings != null && _settings.gameObject.activeInHierarchy;

    private void Close()
    {
        _open = false;
        _menu.Close();
        if (IsSettingsShowing)
        {
            ShowSettings(true);
            if (_tab != null)
            {
                _tab.Select();
            }
        }

        // Next frame: the key that closed the menu mustn't also reach Settings (B there goes back).
        _unblockAtFrame = Time.frameCount + 1;
        MelonLogger.Msg("Closed the Routes page.");
    }

    /// <summary>The whole Settings screen, header and prompts included, so ours stands in for it.</summary>
    private void ShowSettings(bool show)
    {
        var group = _settings == null ? null : _settings.GetComponent<CanvasGroup>();
        if (group != null)
        {
            group.alpha = show ? 1f : 0f;
        }
    }
}
