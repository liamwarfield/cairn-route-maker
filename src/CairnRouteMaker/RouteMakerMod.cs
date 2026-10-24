using System;
using System.IO;
using CairnRouteMaker.Climbing;
using CairnRouteMaker.Controls;
using CairnRouteMaker.Editor;
using CairnRouteMaker.FreeClimb;
using CairnRouteMaker.Game;
using CairnRouteMaker.Routes;
using MelonLoader;
using MelonLoader.Utils;
using UnityEngine.InputSystem;

[assembly: MelonInfo(typeof(CairnRouteMaker.RouteMakerMod), "Cairn Route Maker", "0.2.0", "Liam Warfield")]
[assembly: MelonGame("TheGameBakers", "Cairn")]

namespace CairnRouteMaker;

/// <summary>
/// Entry point. The title screen gets a Free Climb button that opens the launcher; the editor,
/// free camera, teleports and route overlay only work inside a free-climb game, never in story.
/// </summary>
public sealed class RouteMakerMod : MelonMod
{
    private RouteStorage _storage;
    private RouteOverlay _overlay;
    private RouteEditor _editor;
    private readonly Traveler _traveler = new();
    private readonly AttemptTracker _attempt = new();
    private FreeClimbSession _session;
    private FreeClimbLauncher _launcher;
    private string _lastZone;
    private float _nextZoneCheck;

    public override void OnInitializeMelon()
    {
        var directory = Path.Combine(MelonEnvironment.UserDataDirectory, "CairnRouteMaker", "Routes");
        _storage = new RouteStorage(directory);
        ModControls.Initialize(Path.Combine(MelonEnvironment.UserDataDirectory, "CairnRouteMaker", "Controls.json"));
        _session = new FreeClimbSession(_traveler);
        _session.Started += OnFreeClimbStarted;
        _session.Ended += OnFreeClimbEnded;
        _launcher = new FreeClimbLauncher(_storage, _session);
        MainMenuPatches.FreeClimbClicked += _launcher.Open;
        LoggerInstance.Msg($"Loaded. Routes are stored in {directory}");
        LoggerInstance.Msg("Choose Free Climb on the title screen. In free climb: F6 (or L3+R3) route editor, F8 (or R3 in the editor) freecam, F7 teleport to route start, F9 show/hide route.");
        LoggerInstance.Msg("These are the defaults; change them in Settings > Controls > Remap controls, under Context: Freecam and Context: Route Editor.");
    }

    public override void OnUpdate()
    {
        try
        {
            ModControls.EnsureCreated();
            _launcher.Update();
            _session.Update();
            HandleDiagnosticsKey();
            if (_session.IsActive)
            {
                LogZoneChanges();
                HandleHotkeys();
                _editor?.Update();
                _traveler.Update();
                UpdateAttempt();
            }

            _overlay?.Flush();
        }
        catch (Exception e)
        {
            LoggerInstance.Error($"Route editor error: {e}");
            _editor?.Close();
        }
    }

    public override void OnLateUpdate()
    {
        _editor?.LateUpdate();
        LevelSelectView.EndFrame();
    }

    public override void OnGUI()
    {
        _launcher.Draw();
        if (_editor != null && _session.IsActive)
        {
            EditorHud.Draw(_editor);
        }

        if (_session.IsActive && _editor is { IsOpen: false })
        {
            AttemptHud.Draw(_attempt);
        }
    }

    /// <summary>Time climbs of the route on show; editing it calls off any attempt in progress.</summary>
    private void UpdateAttempt()
    {
        if (_editor == null || _editor.IsOpen)
        {
            _attempt.Reset();
            return;
        }

        _attempt.Update(_editor.Route);
    }

    /// <summary>Show the route chosen in the launcher, or none: a zone's routes are never picked automatically.</summary>
    private void OnFreeClimbStarted(Route route)
    {
        _lastZone = null;
        _nextZoneCheck = 0f;
        if (route != null)
        {
            EnsureEditor();
        }

        _editor?.ShowRoute(route);
        if (_overlay != null)
        {
            _overlay.Visible = true;
        }
    }

    private void OnFreeClimbEnded()
    {
        _editor?.Close();
        _editor?.ShowRoute(null);
        _traveler.Cancel();
        _attempt.Reset();
        if (_overlay != null)
        {
            // The overlay outlives scenes, so it must be hidden by hand.
            _overlay.Visible = false;
        }

        _lastZone = null;
    }

    /// <summary>F10 logs game internals anywhere, menus and story included: it only writes to the log.</summary>
    private void HandleDiagnosticsKey()
    {
        var keyboard = Keyboard.current;
        if (keyboard != null && keyboard.f10Key.wasPressedThisFrame)
        {
            Diagnostics.Dump();
        }
    }

    private void HandleHotkeys()
    {
        if (ModControls.OpenEditor.KeyPressed || EditorInput.GamepadToggleEditor())
        {
            EnsureEditor();
            if (_editor.IsOpen)
            {
                _editor.Close();
            }
            else
            {
                _editor.Open();
            }
        }
        else if (ModControls.GoToStart.KeyPressed)
        {
            EnsureEditor();
            _editor.PlayTest();
        }
        else if (ModControls.ToggleFreecam.KeyPressed)
        {
            EnsureEditor();
            _editor.ToggleFreecam();
        }
        else if (ModControls.ShowRoute.KeyPressed && _overlay != null)
        {
            _overlay.Visible = !_overlay.Visible;
        }
    }

    /// <summary>Created lazily: Unity objects can't be made before the game has started up.</summary>
    private void EnsureEditor()
    {
        _overlay ??= new RouteOverlay();
        _editor ??= new RouteEditor(_storage, _overlay, _traveler);
    }

    private void LogZoneChanges()
    {
        if (UnityEngine.Time.unscaledTime < _nextZoneCheck)
        {
            return;
        }

        _nextZoneCheck = UnityEngine.Time.unscaledTime + 1f;
        var zone = GameApi.CurrentZoneKey;
        if (zone != _lastZone)
        {
            _lastZone = zone;
            LoggerInstance.Msg($"Zone: {zone ?? "(none)"}");
        }
    }
}
