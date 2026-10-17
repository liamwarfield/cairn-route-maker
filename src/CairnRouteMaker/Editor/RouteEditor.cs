using System;
using System.Collections.Generic;
using CairnRouteMaker.Game;
using CairnRouteMaker.Routes;
using MelonLoader;
using UnityEngine;
using UnityEngine.InputSystem;

namespace CairnRouteMaker.Editor;

/// <summary>
/// The in-game route editor: a free-flying camera, a brush that paints allow/deny stamps on rock,
/// and a start marker. While open, the game's own input is blocked and devices are read directly.
/// </summary>
internal sealed class RouteEditor
{
    private const float MinRadius = 0.1f;
    private const float MaxRadius = 5f;
    private const float MaxPaintDistance = 300f;
    private const float FlySpeed = 6f;
    private const float LookSensitivity = 0.12f;

    /// <summary>A new stamp is laid once the brush has moved this fraction of its radius.</summary>
    private const float StampSpacing = 0.35f;

    private enum UndoKind
    {
        Stroke,
        Start,
    }

    private readonly RouteStorage _storage;
    private readonly RouteOverlay _overlay;
    private readonly Stack<(UndoKind Kind, StartPosition PreviousStart)> _undo = new();

    private Camera _camera;
    private float _yaw;
    private float _pitch;
    private Stroke _stroke;
    private bool _dirty;
    private bool _sawInput;
    private int _openedFrame;

    public RouteEditor(RouteStorage storage, RouteOverlay overlay)
    {
        _storage = storage;
        _overlay = overlay;
    }

    public bool IsOpen { get; private set; }
    public Route Route { get; private set; } = new();
    public StrokeMode Mode { get; private set; } = StrokeMode.Allow;
    public float Radius { get; private set; } = 0.6f;
    public bool HasBrushTarget { get; private set; }
    public bool Looking { get; private set; }
    public string Status { get; private set; }
    public float StatusTime { get; private set; }

    public void Open()
    {
        if (IsOpen)
        {
            return;
        }

        var zone = GameApi.CurrentZoneKey;
        if (!GameApi.IsAvaReady || zone == null)
        {
            SetStatus("Load into the mountain before opening the route editor.");
            return;
        }

        if (Route.IsEmpty)
        {
            LoadOrCreate(zone);
        }

        _camera = GameApi.TakeCamera();
        if (_camera == null)
        {
            SetStatus("No main camera found.");
            return;
        }

        var euler = _camera.transform.rotation.eulerAngles;
        _yaw = euler.y;
        _pitch = euler.x > 180f ? euler.x - 360f : euler.x;
        GameApi.BlockGameInput(true);
        _overlay.Visible = true;
        _sawInput = false;
        _openedFrame = Time.frameCount;
        IsOpen = true;
        SetStatus($"Editing route for {Route.ZoneKey}.");
    }

    public void Close()
    {
        if (!IsOpen)
        {
            return;
        }

        EndStroke();
        if (_dirty)
        {
            Save();
        }

        _overlay.HideBrush();
        GameApi.ReleaseCamera();
        GameApi.BlockGameInput(false);
        _camera = null;
        Looking = false;
        IsOpen = false;
    }

    /// <summary>Teleport Ava to the route's start, closing the editor first if it's open.</summary>
    public void PlayTest()
    {
        Close();
        var zone = GameApi.CurrentZoneKey;
        if (Route.IsEmpty && zone != null)
        {
            LoadOrCreate(zone);
        }

        if (Route.Start == null)
        {
            SetStatus("This route has no start position yet (press G in the editor).");
            return;
        }

        var start = Route.Start;
        if (GameApi.TryTeleportAva(RouteOverlay.ToUnity(start.Position), start.YawDegrees, out var reason))
        {
            _overlay.Visible = true;
            SetStatus("Teleported to route start. Climb on!");
        }
        else
        {
            SetStatus(reason);
        }
    }

    public void Update()
    {
        if (!IsOpen)
        {
            return;
        }

        var keyboard = Keyboard.current;
        var mouse = Mouse.current;
        if (keyboard == null || mouse == null || _camera == null)
        {
            return;
        }

        // Skip the frame the editor opened on, or the F6 press itself would count.
        if (!_sawInput && Time.frameCount > _openedFrame && (keyboard.anyKey.wasPressedThisFrame || mouse.leftButton.wasPressedThisFrame || mouse.rightButton.wasPressedThisFrame))
        {
            _sawInput = true;
            MelonLogger.Msg("Editor is receiving keyboard/mouse input.");
        }

        var ctrl = keyboard.leftCtrlKey.isPressed || keyboard.rightCtrlKey.isPressed;
        if (keyboard.escapeKey.wasPressedThisFrame)
        {
            Close();
            return;
        }

        if (keyboard.pKey.wasPressedThisFrame)
        {
            PlayTest();
            return;
        }

        if (keyboard.tabKey.wasPressedThisFrame)
        {
            Mode = Mode == StrokeMode.Allow ? StrokeMode.Deny : StrokeMode.Allow;
        }

        if (ctrl && keyboard.zKey.wasPressedThisFrame)
        {
            Undo();
        }

        if (ctrl && keyboard.sKey.wasPressedThisFrame)
        {
            Save();
        }

        var scroll = mouse.scroll.ReadValue().y;
        if (scroll != 0f)
        {
            Radius = Mathf.Clamp(Radius * (scroll > 0f ? 1.1f : 1f / 1.1f), MinRadius, MaxRadius);
        }

        UpdateCamera(keyboard, mouse, ctrl);

        var ray = _camera.ScreenPointToRay(mouse.position.ReadValue());
        HasBrushTarget = Physics.Raycast(ray, out var hit, MaxPaintDistance, GameApi.SurfaceMask, QueryTriggerInteraction.Ignore);
        if (HasBrushTarget && !Looking)
        {
            _overlay.ShowBrush(hit.point, Radius, Mode);
        }
        else
        {
            _overlay.HideBrush();
        }

        if (keyboard.gKey.wasPressedThisFrame && HasBrushTarget)
        {
            SetStart(hit.point);
        }

        if (mouse.leftButton.wasPressedThisFrame && !Looking)
        {
            BeginStroke();
        }

        if (_stroke != null && mouse.leftButton.isPressed && HasBrushTarget)
        {
            Paint(hit.point, hit.normal);
        }

        if (mouse.leftButton.wasReleasedThisFrame)
        {
            EndStroke();
        }

        _overlay.Flush();
    }

    /// <summary>Called after the game's own updates so our cursor state wins.</summary>
    public void LateUpdate()
    {
        if (!IsOpen)
        {
            return;
        }

        Cursor.lockState = Looking ? CursorLockMode.Locked : CursorLockMode.None;
        Cursor.visible = !Looking;
    }

    private void UpdateCamera(Keyboard keyboard, Mouse mouse, bool ctrl)
    {
        var dt = Time.unscaledDeltaTime;
        Looking = mouse.rightButton.isPressed;
        if (Looking)
        {
            var delta = mouse.delta.ReadValue();
            _yaw += delta.x * LookSensitivity;
            _pitch = Mathf.Clamp(_pitch - delta.y * LookSensitivity, -89f, 89f);
        }

        var rotation = Quaternion.Euler(_pitch, _yaw, 0f);
        var move = Vector3.zero;
        if (!ctrl)
        {
            if (keyboard.wKey.isPressed) move += rotation * Vector3.forward;
            if (keyboard.sKey.isPressed) move += rotation * Vector3.back;
            if (keyboard.dKey.isPressed) move += rotation * Vector3.right;
            if (keyboard.aKey.isPressed) move += rotation * Vector3.left;
            if (keyboard.eKey.isPressed) move += Vector3.up;
            if (keyboard.qKey.isPressed) move += Vector3.down;
        }

        var speed = FlySpeed * (keyboard.leftShiftKey.isPressed ? 4f : 1f) * (keyboard.leftAltKey.isPressed ? 0.25f : 1f);
        var transform = _camera.transform;
        transform.SetPositionAndRotation(transform.position + move * (speed * dt), rotation);
    }

    private void BeginStroke()
    {
        _stroke = new Stroke(Mode);
        _overlay.BeginStroke();
    }

    private void Paint(Vector3 point, Vector3 normal)
    {
        var position = RouteOverlay.ToNumerics(point);
        if (_stroke.Stamps.Count > 0)
        {
            var last = _stroke.Stamps[^1];
            if (System.Numerics.Vector3.Distance(last.Position, position) < Radius * StampSpacing)
            {
                return;
            }
        }

        if (_stroke.Stamps.Count == 0)
        {
            Route.Strokes.Add(_stroke);
            _undo.Push((UndoKind.Stroke, null));
        }

        var stamp = new Stamp(position, RouteOverlay.ToNumerics(normal), Radius);
        _stroke.Stamps.Add(stamp);
        _overlay.AddStamp(_stroke.Mode, stamp);
        _dirty = true;
    }

    private void EndStroke()
    {
        if (_stroke != null && _stroke.Stamps.Count == 0)
        {
            // Clicked on nothing: drop the empty overlay group so undo stays in step.
            _overlay.RemoveLastStroke();
        }

        _stroke = null;
    }

    private void SetStart(Vector3 point)
    {
        _undo.Push((UndoKind.Start, Route.Start));
        Route.Start = new StartPosition { Position = RouteOverlay.ToNumerics(point), YawDegrees = _yaw };
        _overlay.SetStart(Route.Start);
        _dirty = true;
        SetStatus("Start position set.");
    }

    private void Undo()
    {
        EndStroke();
        if (_undo.Count == 0)
        {
            SetStatus("Nothing to undo.");
            return;
        }

        var (kind, previousStart) = _undo.Pop();
        if (kind == UndoKind.Stroke)
        {
            Route.Strokes.RemoveAt(Route.Strokes.Count - 1);
            _overlay.RemoveLastStroke();
        }
        else
        {
            Route.Start = previousStart;
            _overlay.SetStart(previousStart);
        }

        _dirty = true;
    }

    private void LoadOrCreate(string zone)
    {
        _undo.Clear();
        Route = new Route { ZoneKey = zone, GameVersion = GameApi.GameVersion };
        if (_storage.Exists(zone))
        {
            try
            {
                Route = _storage.Load(zone);
                MelonLogger.Msg($"Loaded route from {_storage.PathFor(zone)}");
            }
            catch (Exception e)
            {
                MelonLogger.Error($"Couldn't load {_storage.PathFor(zone)}: {e.Message}");
                SetStatus("Existing route file is unreadable; starting a new one (old file kept until you save).");
            }
        }

        _overlay.Rebuild(Route);
        _dirty = false;
    }

    private void Save()
    {
        try
        {
            Route.GameVersion = GameApi.GameVersion;
            var path = _storage.Save(Route);
            _dirty = false;
            SetStatus("Route saved.");
            MelonLogger.Msg($"Saved route to {path}");
        }
        catch (Exception e)
        {
            MelonLogger.Error($"Saving route failed: {e}");
            SetStatus("Saving failed (see log).");
        }
    }

    private void SetStatus(string message)
    {
        Status = message;
        StatusTime = Time.unscaledTime;
        MelonLogger.Msg(message);
    }
}
