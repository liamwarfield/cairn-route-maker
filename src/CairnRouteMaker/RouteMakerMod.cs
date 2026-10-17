using System;
using System.IO;
using CairnRouteMaker.Editor;
using CairnRouteMaker.Game;
using CairnRouteMaker.Routes;
using MelonLoader;
using MelonLoader.Utils;
using UnityEngine.InputSystem;

[assembly: MelonInfo(typeof(CairnRouteMaker.RouteMakerMod), "Cairn Route Maker", "0.1.0", "Liam Warfield")]
[assembly: MelonGame("TheGameBakers", "Cairn")]

namespace CairnRouteMaker;

public sealed class RouteMakerMod : MelonMod
{
    private RouteStorage _storage;
    private RouteOverlay _overlay;
    private RouteEditor _editor;
    private readonly Traveler _traveler = new();
    private string _lastZone;
    private float _nextZoneCheck;

    public override void OnInitializeMelon()
    {
        var directory = Path.Combine(MelonEnvironment.UserDataDirectory, "CairnRouteMaker", "Routes");
        _storage = new RouteStorage(directory);
        LoggerInstance.Msg($"Loaded. Routes are stored in {directory}");
        LoggerInstance.Msg("F6 (or L3+R3) route editor, F7 teleport to route start, F8 level select, F9 show/hide route.");
    }

    public override void OnUpdate()
    {
        try
        {
            LogZoneChanges();
            HandleHotkeys();
            _editor?.Update();
            _traveler.Update();
            _overlay?.Flush();
        }
        catch (Exception e)
        {
            LoggerInstance.Error($"Route editor error: {e}");
            _editor?.Close();
        }
    }

    public override void OnLateUpdate() => _editor?.LateUpdate();

    public override void OnGUI()
    {
        if (_editor != null)
        {
            EditorHud.Draw(_editor);
        }
    }

    private void HandleHotkeys()
    {
        var keyboard = Keyboard.current;
        if (keyboard == null)
        {
            return;
        }

        if (keyboard.f6Key.wasPressedThisFrame || EditorInput.GamepadToggleEditor())
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
        else if (keyboard.f7Key.wasPressedThisFrame)
        {
            EnsureEditor();
            _editor.PlayTest();
        }
        else if (keyboard.f8Key.wasPressedThisFrame)
        {
            EnsureEditor();
            if (_editor.LevelSelect.IsOpen)
            {
                _editor.Close();
            }
            else
            {
                _editor.OpenLevelSelect();
            }
        }
        else if (keyboard.f9Key.wasPressedThisFrame && _overlay != null)
        {
            _overlay.Visible = !_overlay.Visible;
        }
        else if (keyboard.f10Key.wasPressedThisFrame)
        {
            Diagnostics.Dump();
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
            if (zone != null && (_editor != null || _storage.Exists(zone)))
            {
                EnsureEditor();
                _editor.OnZoneChanged(zone);
            }
        }
    }
}
