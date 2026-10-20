using System;
using System.Collections.Generic;
using CairnRouteMaker.Game;
using CairnRouteMaker.Routes;
using MelonLoader;
using UnityEngine;

namespace CairnRouteMaker.Editor;

/// <summary>
/// The in-game route editor: a free-flying camera, a brush that paints allow/deny/erase stamps
/// on rock, start and finish markers, and a level select for jumping around to test. While open, the
/// game's own input is blocked and devices are read directly (see <see cref="EditorInput"/>).
/// It has a second mode, freecam: the same camera with a marker instead of the brush, for looking
/// around and moving Ava, which can't change the route.
/// </summary>
internal sealed class RouteEditor
{
    private const float MinRadius = 0.1f;
    private const float MaxRadius = 5f;
    private const float MaxPaintDistance = 300f;
    /// <summary>Cruising speed in m/s; see EditorInput for the Shift/LT and Alt multipliers.</summary>
    private const float FlySpeed = 6f;

    /// <summary>The finish takes the brush's size, but no smaller than this, so Ava can't miss it.</summary>
    private const float MinFinishRadius = 1f;

    /// <summary>Radius of the freecam's marker sphere.</summary>
    private const float MarkerRadius = 0.3f;

    /// <summary>A new stamp is laid once the brush has moved this fraction of its radius.</summary>
    private const float StampSpacing = 0.35f;

    private enum UndoKind
    {
        Stroke,
        Start,
        Finish,
    }

    private readonly RouteStorage _storage;
    private readonly RouteOverlay _overlay;
    private readonly Traveler _traveler;
    private readonly Stack<(UndoKind Kind, StartPosition PreviousStart, FinishZone PreviousFinish)> _undo = new();

    private Camera _camera;
    private float _yaw;
    private float _pitch;
    private Stroke _stroke;
    private bool _dirty;
    private bool _sawInput;
    private float _nextStreamUpdate;
    private int _openedFrame;

    public RouteEditor(RouteStorage storage, RouteOverlay overlay, Traveler traveler)
    {
        _storage = storage;
        _overlay = overlay;
        _traveler = traveler;
    }

    public EditorInput Input { get; } = new();
    public LevelSelect LevelSelect { get; } = new();

    public bool IsOpen { get; private set; }

    /// <summary>Freecam mode: look around and move Ava, but no editing. Opening always starts here.</summary>
    public bool Freecam { get; private set; }
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

        if (Route.IsEmpty || Route.ZoneKey != zone)
        {
            if (_dirty)
            {
                Save();
            }

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
        Input.Reset();
        // Editing is always a deliberate switch (R3 / F8), so nothing gets changed by accident.
        Freecam = true;
        IsOpen = true;
        SetStatus(Freecam ? "Freecam: A or click moves Ava to the marker." : $"Editing route for {Route.ZoneKey}.");
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

        LevelSelect.Close();
        _overlay.HideBrush();
        GameApi.StopStreamingAroundCamera();
        GameApi.ReleaseCamera();
        GameApi.BlockGameInput(false);
        _camera = null;
        Looking = false;
        IsOpen = false;
    }

    /// <summary>
    /// Show the saved route for a zone Ava has just entered. Zones without a saved route keep the
    /// current one showing, so walking over a zone boundary mid-climb doesn't hide the route.
    /// </summary>
    public void OnZoneChanged(string zone)
    {
        if (IsOpen || _dirty || zone == null || zone == Route.ZoneKey || !_storage.Exists(zone))
        {
            return;
        }

        LoadOrCreate(zone);
        SetStatus($"Showing the saved route for {zone}.");
    }

    /// <summary>Switch between editing and freecam, opening in freecam if closed.</summary>
    public void ToggleFreecam()
    {
        if (!IsOpen)
        {
            Open();
            return;
        }

        EndStroke();
        Freecam = !Freecam;
        SetStatus(Freecam ? "Freecam: A or click moves Ava to the marker." : $"Editing route for {Route.ZoneKey}.");
    }

    /// <summary>
    /// Put Ava on the route's start, closing the editor first if it's open. If the route belongs
    /// to another zone, travel there first.
    /// </summary>
    public void PlayTest()
    {
        Close();
        var zone = GameApi.CurrentZoneKey;
        if (Route.IsEmpty && zone != null)
        {
            LoadOrCreate(zone);
        }

        var start = Route.Start;
        if (start == null)
        {
            SetStatus("This route has no start position yet (G, or Y on a gamepad, in the editor).");
            return;
        }

        var position = RouteOverlay.ToUnity(start.Position);
        if (Route.ZoneKey != zone)
        {
            var parts = Route.ZoneKey.Split('/', 2);
            if (parts.Length == 2 && Enum.TryParse<Il2Cpp.StreamingTweakables.World>(parts[0], out var world))
            {
                _traveler.Start(world, parts[1], position, start.YawDegrees, SetStatus);
                SetStatus($"Travelling to {Route.ZoneKey}...");
                return;
            }
        }

        if (GameApi.TryTeleportAva(position, start.YawDegrees, out var reason))
        {
            // Every attempt starts with Ava fresh, whatever the last climb took out of her.
            GameApi.RestoreAva();
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
        if (!IsOpen || _camera == null)
        {
            return;
        }

        Input.Read(Time.unscaledDeltaTime);

        // Skip the frame the editor opened on, or the key that opened it would count.
        if (!_sawInput && Time.frameCount > _openedFrame && (Input.Move != Vector3.zero || Input.PaintPressed || Input.Looking))
        {
            _sawInput = true;
            MelonLogger.Msg($"Editor is receiving {(Input.UsingGamepad ? "gamepad" : "keyboard/mouse")} input.");
        }

        if (LevelSelect.IsOpen)
        {
            UpdateLevelSelect();
            return;
        }

        if (Input.Close)
        {
            Close();
            return;
        }

        if (Input.PlayTest)
        {
            PlayTest();
            return;
        }

        if (Input.LevelSelect)
        {
            EndStroke();
            _overlay.HideBrush();
            LevelSelect.Open(_storage, _traveler, SetStatus, OnRouteDeleted);
            return;
        }

        if (Input.ToggleFreecam)
        {
            ToggleFreecam();
        }

        if (Freecam)
        {
            UpdateFreecam();
            return;
        }

        if (Input.CycleBrush)
        {
            Mode = Mode switch { StrokeMode.Allow => StrokeMode.Deny, StrokeMode.Deny => StrokeMode.Erase, _ => StrokeMode.Allow };
        }

        if (Input.SelectBrush is { } brush)
        {
            Mode = (StrokeMode)brush;
        }

        if (Input.Undo)
        {
            Undo();
        }

        if (Input.Save)
        {
            Save();
        }

        Radius = Mathf.Clamp(Radius * Input.BrushScale, MinRadius, MaxRadius);
        UpdateCamera();
        var hit = Aim();
        if (HasBrushTarget && !Looking)
        {
            _overlay.ShowBrush(hit.point, Radius, Mode);
        }
        else
        {
            _overlay.HideBrush();
        }

        if (Input.SetStart && HasBrushTarget)
        {
            SetStart(hit.point);
        }

        if (Input.SetFinish && HasBrushTarget)
        {
            SetFinish(hit.point);
        }

        if (Input.PaintPressed)
        {
            BeginStroke();
        }

        if (_stroke != null && Input.PaintHeld && HasBrushTarget)
        {
            Paint(hit.point, hit.normal);
        }

        if (Input.PaintReleased)
        {
            EndStroke();
        }
    }

    /// <summary>Fly and aim as in the editor, but the only thing to do at the marker is move Ava there.</summary>
    private void UpdateFreecam()
    {
        UpdateCamera();
        var hit = Aim();
        if (HasBrushTarget && !Looking)
        {
            _overlay.ShowMarker(hit.point, MarkerRadius);
        }
        else
        {
            _overlay.HideBrush();
        }

        if (Input.Teleport && HasBrushTarget)
        {
            if (GameApi.TryQuickTeleportAva(hit.point, _yaw, out var reason))
            {
                GameApi.RestoreAva();
                Close();
                SetStatus("Moved Ava to the marker.");
            }
            else
            {
                SetStatus(reason);
            }
        }
    }

    /// <summary>Keep scenery streamed in around the camera and find the rock under the cursor.</summary>
    private RaycastHit Aim()
    {
        if (Time.unscaledTime >= _nextStreamUpdate)
        {
            // Load full-detail scenery where the camera is, not just around Ava.
            _nextStreamUpdate = Time.unscaledTime + 0.5f;
            GameApi.StreamAroundCamera();
        }

        // With a gamepad there's no cursor, so aim at the middle of the screen.
        var aim = Input.UsingGamepad ? new Vector2(Screen.width * 0.5f, Screen.height * 0.5f) : Input.PointerPosition;
        var ray = _camera.ScreenPointToRay(new Vector3(aim.x, aim.y, 0f));
        HasBrushTarget = Physics.Raycast(ray, out var hit, MaxPaintDistance, GameApi.SurfaceMask, QueryTriggerInteraction.Ignore);
        return hit;
    }

    private void UpdateLevelSelect()
    {
        LevelSelectView.Show(LevelSelect, Input);
        if (Input.MenuBack)
        {
            LevelSelect.Back();
            return;
        }

        LevelSelect.Move(Input.MenuMove);
        if (LevelSelect.ClickedIndex is { } clicked)
        {
            LevelSelect.Select(clicked);
            LevelSelect.ClickedIndex = null;
            ChooseLevel();
        }
        else if (Input.MenuConfirm)
        {
            ChooseLevel();
        }
    }

    /// <summary>
    /// Run the highlighted menu entry. Travel closes the editor first (handing input and camera
    /// back to the game); other entries just change the menu page.
    /// </summary>
    private void ChooseLevel()
    {
        var entry = LevelSelect.Current;
        if (entry == null)
        {
            return;
        }

        if (!entry.LeavesEditor)
        {
            entry.Choose();
            return;
        }

        Close();
        entry.Choose();
        if (_traveler.IsActive)
        {
            SetStatus($"Travelling to {_traveler.Destination}...");
        }
    }

    /// <summary>A route file was deleted from the menu: if it's the one being shown, start fresh.</summary>
    private void OnRouteDeleted(string zone)
    {
        if (Route.ZoneKey != zone)
        {
            return;
        }

        EndStroke();
        _undo.Clear();
        Route = new Route { ZoneKey = zone, GameVersion = GameApi.GameVersion };
        _dirty = false;
        _overlay.Rebuild(Route);
    }

    /// <summary>Called after the game's own updates so our cursor state wins.</summary>
    public void LateUpdate()
    {
        if (!IsOpen)
        {
            return;
        }

        var hideCursor = Looking || (Input.UsingGamepad && !LevelSelect.IsOpen);
        Cursor.lockState = Looking ? CursorLockMode.Locked : CursorLockMode.None;
        Cursor.visible = !hideCursor;
    }

    private void UpdateCamera()
    {
        Looking = Input.Looking;
        var look = Input.LookDegrees;
        _yaw += look.x;
        _pitch = Mathf.Clamp(_pitch - look.y, -89f, 89f);

        var rotation = Quaternion.Euler(_pitch, _yaw, 0f);
        var input = Input.Move;
        var move = rotation * new Vector3(input.x, 0f, input.z) + Vector3.up * input.y;
        var speed = FlySpeed * Input.MoveSpeedScale;
        var transform = _camera.transform;
        transform.SetPositionAndRotation(transform.position + move * (speed * Time.unscaledDeltaTime), rotation);
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
            _undo.Push((UndoKind.Stroke, null, null));
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
        _undo.Push((UndoKind.Start, Route.Start, null));
        Route.Start = new StartPosition { Position = RouteOverlay.ToNumerics(point), YawDegrees = _yaw };
        _overlay.SetStart(Route.Start);
        _dirty = true;
        SetStatus("Start position set.");
    }

    private void SetFinish(Vector3 point)
    {
        _undo.Push((UndoKind.Finish, null, Route.Finish));
        Route.Finish = new FinishZone { Position = RouteOverlay.ToNumerics(point), Radius = Mathf.Max(Radius, MinFinishRadius) };
        _overlay.SetFinish(Route.Finish);
        _dirty = true;
        SetStatus($"Finish set ({Route.Finish.Radius:0.0} m radius).");
    }

    private void Undo()
    {
        EndStroke();
        if (_undo.Count == 0)
        {
            SetStatus("Nothing to undo.");
            return;
        }

        var (kind, previousStart, previousFinish) = _undo.Pop();
        switch (kind)
        {
            case UndoKind.Stroke:
                Route.Strokes.RemoveAt(Route.Strokes.Count - 1);
                _overlay.RemoveLastStroke();
                break;
            case UndoKind.Start:
                Route.Start = previousStart;
                _overlay.SetStart(previousStart);
                break;
            case UndoKind.Finish:
                Route.Finish = previousFinish;
                _overlay.SetFinish(previousFinish);
                break;
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
