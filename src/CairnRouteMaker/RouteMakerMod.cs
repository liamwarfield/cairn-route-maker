using System;
using System.IO;
using CairnRouteMaker.Climbing;
using CairnRouteMaker.Controls;
using CairnRouteMaker.Editor;
using CairnRouteMaker.Extras;
using CairnRouteMaker.FreeClimb;
using CairnRouteMaker.Game;
using CairnRouteMaker.Routes;
using MelonLoader;
using MelonLoader.Utils;
using UnityEngine.InputSystem;

[assembly: MelonInfo(typeof(CairnRouteMaker.RouteMakerMod), "Cairn Route Maker", "0.4.1", "Liam Warfield")]
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
    private AttemptTracker _attempt;
    private FreeClimbSession _session;
    private FreeClimbLauncher _launcher;
    private readonly LoadingArtGallery _gallery = new();
    private readonly PaintColorsMenu _paintColors = new();
    private string _lastZone;
    private float _nextZoneCheck;

    public override void OnInitializeMelon()
    {
        PaintColors.Load();
        var directory = Path.Combine(MelonEnvironment.UserDataDirectory, "CairnRouteMaker", "Routes");
        _storage = new RouteStorage(directory);
        _attempt = new AttemptTracker(_storage.Bests);
        GameApi.Teleported += placement => _attempt.OnTeleported(placement, _editor?.Route);
        ModControls.Initialize(Path.Combine(MelonEnvironment.UserDataDirectory, "CairnRouteMaker", "Controls.json"));
        FreeClimbSaves.Initialize(Path.Combine(MelonEnvironment.UserDataDirectory, "CairnRouteMaker", "FreeClimbSaves.txt"));
        _session = new FreeClimbSession(_traveler);
        _session.Started += OnFreeClimbStarted;
        _session.Ended += OnFreeClimbEnded;
        _launcher = new FreeClimbLauncher(_storage, _session);
        MainMenuPatches.FreeClimbClicked += _launcher.Open;
        MainMenuPatches.LoadingArtClicked += _gallery.Open;
        SettingsMenuPatches.RoutesClicked += _paintColors.Open;
        EagleEyePatches.DisplayRouteToggled += OnDisplayRouteToggled;
        LoggerInstance.Msg($"Loaded. Routes are stored in {directory}");
        LoggerInstance.Msg("Choose Free Climb on the title screen. In free climb: F6 (or L3+R3) route editor, F8 (or R3 in the editor) freecam, F7 teleport to route start, F9 show/hide route.");
        LoggerInstance.Msg("These are the defaults; change them in Settings > Controls > Remap controls, under Context: Freecam and Context: Route Editor.");
    }

    public override void OnUpdate()
    {
        try
        {
            Diagnostics.SampleFrame();
            ModControls.EnsureCreated();
            _launcher.Update();
            _gallery.Update();
            _paintColors.Update();
            _session.Update();
            HandleDiagnosticsKey();
            if (_session.IsActive)
            {
                LogZoneChanges();
                HandleHotkeys();
                _editor?.Update();
                _traveler.Update();
                UpdateAttempt();
                UpdatePaintFocus();
            }

            // Drawing paint casts rays at the rock, so it waits out loading screens.
            if (!GameApi.IsWorldBusy)
            {
                _overlay?.Flush();
            }
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
        _overlay?.LateUpdate();
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

    /// <summary>
    /// Paint only shows near Ava, or near the camera in the route editor: every painted spot costs
    /// frame rate, and the route lines show the way from further off.
    /// </summary>
    private void UpdatePaintFocus()
    {
        if (_overlay == null)
        {
            return;
        }

        if (_editor is { IsOpen: true })
        {
            if (_editor.CameraPosition is { } camera)
            {
                _overlay.ShowPaintAround(camera);
            }
        }
        else if (GameApi.AvaPosition is { } ava)
        {
            _overlay.ShowPaintAround(ava);
        }
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

    /// <summary>The map's "Display route" button shows and hides the route line along with Ava's own path.</summary>
    private void OnDisplayRouteToggled(bool shown)
    {
        if (shown != PaintColors.ShowLine)
        {
            PaintColors.SetShowLine(shown);
            LoggerInstance.Msg($"Display route pressed: route line {(shown ? "shown" : "hidden")}.");
        }
    }

    /// <summary>F10 logs game internals anywhere, menus and story included: it only writes to the log.</summary>
    private void HandleDiagnosticsKey()
    {
        var keyboard = Keyboard.current;
        if (keyboard != null && keyboard.f10Key.wasPressedThisFrame)
        {
            Diagnostics.Dump();
            if (_overlay != null)
            {
                var (cubes, shown, faded) = _overlay.PaintCounts;
                LoggerInstance.Msg($"Route paint: {shown} of {cubes} cubes drawn, {faded} of them fading out (slowest frame of fading since the last F10: "
                    + $"{_overlay.TakeSlowestPaintFadeMs():0.00} ms); draw distance {PaintColors.DescribePaintDistance(PaintColors.PaintDistance)}.");
            }
        }
    }

    private void HandleHotkeys()
    {
        // Rebound to letters, these would go off while typing a route's name.
        if (_editor?.LevelSelect.TextEntry != null)
        {
            return;
        }

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
            // Going back to the start is how to give up on an attempt and try again. Reset first: the
            // teleport sets up the next attempt, which waits for her on a wall start.
            _attempt.Reset();
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
