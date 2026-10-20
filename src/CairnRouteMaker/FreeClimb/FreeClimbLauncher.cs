using CairnRouteMaker.Editor;
using CairnRouteMaker.Game;
using CairnRouteMaker.Routes;
using MelonLoader;
using UnityEngine;

namespace CairnRouteMaker.FreeClimb;

/// <summary>
/// The menu the title screen's Free Climb button opens: the level select's launcher pages, drawn
/// over the title screen. Choosing a place starts a free-climb game there. While it's open the
/// title screen's own input is blocked; devices are read directly.
/// </summary>
internal sealed class FreeClimbLauncher
{
    private readonly RouteStorage _storage;
    private readonly FreeClimbSession _session;
    private readonly LevelSelect _menu = new();
    private readonly EditorInput _input = new();
    /// <summary>Ours, not the menu's: the menu also closes itself when backed out of its first page.</summary>
    private bool _open;
    private int _openedFrame;
    private int _unblockAtFrame = -1;
    private string _status;
    private float _statusTime;

    public FreeClimbLauncher(RouteStorage storage, FreeClimbSession session)
    {
        _storage = storage;
        _session = session;
        MainMenuPatches.SuppressCancel = () => _open || _unblockAtFrame >= 0;
    }

    public bool IsOpen => _open;

    public void Open()
    {
        if (_open)
        {
            return;
        }

        MelonLogger.Msg($"Opening the free-climb menu; title screen: {GameApi.DescribeMainMenu()}");
        _menu.OpenLauncher(_storage, Launch);
        _open = true;
        _openedFrame = Time.frameCount;
        _unblockAtFrame = -1;
        GameApi.BlockMenuInput(true);
    }

    public void Close()
    {
        if (!_open)
        {
            return;
        }

        _open = false;
        _menu.Close();
        // Next frame: the key that closed the menu mustn't also reach the title screen (B there goes back).
        _unblockAtFrame = Time.frameCount + 1;
    }

    public void Update()
    {
        if (_unblockAtFrame >= 0 && Time.frameCount >= _unblockAtFrame)
        {
            _unblockAtFrame = -1;
            GameApi.BlockMenuInput(false);
            MelonLogger.Msg($"Closed the free-climb menu; title screen: {GameApi.DescribeMainMenu()}");
        }

        if (!_open)
        {
            return;
        }

        _input.Read(Time.unscaledDeltaTime);

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

    public void Draw()
    {
        if (_menu.IsOpen)
        {
            EditorHud.DrawMenu(_menu, _input.UsingGamepad);
        }

        EditorHud.DrawStatus(_status, _statusTime);
    }

    private void Launch(LevelSelect.LaunchTarget target)
    {
        if (!GameApi.TryLaunchFreeClimb(target.World, target.Zone, target.Spawner, out var reason))
        {
            SetStatus($"Couldn't start free climb: {reason}");
            return;
        }

        _session.OnLaunched(target.Route);
        Close();
    }

    private void SetStatus(string message)
    {
        _status = message;
        _statusTime = Time.unscaledTime;
        MelonLogger.Msg(message);
    }
}
