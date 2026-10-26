using System;
using System.Collections.Generic;
using CairnRouteMaker.Game;
using CairnRouteMaker.Routes;
using MelonLoader;
using UnityEngine;

namespace CairnRouteMaker.Editor;

/// <summary>
/// The in-game route editor: a free-flying camera, a brush that paints allow/deny/erase stamps
/// on rock, a line tool that places the route line's control points, start and finish markers, and
/// a level select for jumping around to test. While open, the game's own input is blocked and
/// devices are read directly (see <see cref="EditorInput"/>).
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

    /// <summary>Radius of the line tool's aiming sphere.</summary>
    private const float LineMarkerRadius = 0.12f;

    /// <summary>A grabbed control point only follows the cursor once the aim has moved this far on the rock.</summary>
    private const float DragThreshold = 0.03f;

    private enum UndoKind
    {
        Stroke,
        Start,
        Finish,
        Line,
    }

    private readonly RouteStorage _storage;
    private readonly RouteOverlay _overlay;
    private readonly Traveler _traveler;
    private readonly Stack<(UndoKind Kind, StartPosition PreviousStart, FinishZone PreviousFinish, List<LinePoint> PreviousLine)> _undo = new();

    private Camera _camera;
    private float _yaw;
    private float _pitch;
    private Stroke _stroke;
    private bool _dirty;
    private bool _sawInput;
    private float _nextStreamUpdate;
    private int _openedFrame;

    // Line tool: the point being dragged (or -1), where the drag started on the rock, and the line
    // as it was before the drag, which becomes an undo step once the drag actually changes something.
    private int _dragPoint = -1;
    private Vector3 _dragStart;
    private bool _dragFollowing;
    private List<LinePoint> _lineBeforeDrag;

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

    /// <summary>The fourth brush: places and moves the route line's control points instead of painting.</summary>
    public bool LineTool { get; private set; }

    /// <summary>The line point that delete removes and that's drawn largest, or -1 for the last one.</summary>
    public int SelectedPoint { get; private set; } = -1;
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
        SetStatus(ModeStatus);
    }

    public void Close()
    {
        if (!IsOpen)
        {
            return;
        }

        EndStroke();
        EndDrag();
        if (_dirty)
        {
            Save();
        }

        LevelSelect.Close();
        _overlay.HideBrush();
        _overlay.EditingLine = false;
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
        EndDrag();
        Freecam = !Freecam;
        SetStatus(ModeStatus);
    }

    private string ModeStatus => Freecam ? "Freecam: A or click moves Ava to the marker." : $"Editing route for {Route.ZoneKey}.";

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

        if (Route.ZoneKey != zone && _traveler.StartToRoute(Route, SetStatus))
        {
            SetStatus($"Travelling to {Route.ZoneKey}...");
            return;
        }

        if (GameApi.TryTeleportAva(RouteOverlay.ToUnity(start.Position), start.YawDegrees, out var reason))
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
            EndDrag();
            _overlay.HideBrush();
            _overlay.EditingLine = false;
            LevelSelect.Open(_storage, _traveler, SetStatus, OnRouteDeleted);
            return;
        }

        if (Input.ToggleFreecam)
        {
            ToggleFreecam();
        }

        if (Input.ToggleLine)
        {
            PaintColors.SetShowLine(!PaintColors.ShowLine);
            SetStatus(PaintColors.ShowLine ? "Route line shown." : "Route line hidden (it still shows while the line tool is in use).");
        }

        if (Freecam)
        {
            _overlay.EditingLine = false;
            UpdateFreecam();
            return;
        }

        if (Input.CycleBrush)
        {
            // Allow, deny, erase, then the line tool.
            if (LineTool)
            {
                SelectTool(0);
            }
            else
            {
                SelectTool(Mode == StrokeMode.Erase ? 3 : (int)Mode + 1);
            }
        }

        if (Input.SelectBrush is { } brush)
        {
            SelectTool(brush);
        }

        _overlay.EditingLine = LineTool;

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
        if (Input.SetStart && HasBrushTarget)
        {
            SetStart(hit.point);
        }

        if (Input.SetFinish && HasBrushTarget)
        {
            SetFinish(hit.point);
        }

        if (LineTool)
        {
            UpdateLineTool(hit);
            return;
        }

        if (HasBrushTarget && !Looking)
        {
            _overlay.ShowBrush(hit.point, Radius, Mode);
        }
        else
        {
            _overlay.HideBrush();
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

    /// <summary>0-2 are the allow, deny and erase brushes, 3 the line tool.</summary>
    private void SelectTool(int tool)
    {
        EndStroke();
        EndDrag();
        LineTool = tool == 3;
        Input.LineTool = LineTool;
        if (!LineTool)
        {
            Mode = (StrokeMode)tool;
        }
        else
        {
            SetStatus(Input.UsingGamepad
                ? "Line tool: RT on rock adds a point, on a point or the line grabs it."
                : "Line tool: click rock to add a point; drag a point, or the line, to move it.");
        }
    }

    /// <summary>
    /// The line tool. Pressing on a control point grabs it; pressing on the line between two points
    /// puts a new point there and grabs that; pressing anywhere else on rock adds a point at the end.
    /// Holding on drags the grabbed point over the rock. Delete removes the selected point (the last
    /// one if none is), and every change is one undo step.
    /// </summary>
    private void UpdateLineTool(RaycastHit hit)
    {
        var line = _overlay.Line;
        var aim = AimPosition();
        var hovered = _dragPoint >= 0 ? _dragPoint : Looking ? -1 : line.PickPoint(_camera, aim);
        line.Hovered = hovered;
        line.Selected = SelectedPoint;
        if (HasBrushTarget && !Looking && hovered < 0)
        {
            _overlay.ShowMarker(hit.point, LineMarkerRadius);
        }
        else
        {
            _overlay.HideBrush();
        }

        if (Input.ClearLine)
        {
            EndDrag();
            ClearLine();
        }
        else if (Input.DeletePoint)
        {
            EndDrag();
            DeletePoint();
        }

        if (Input.PaintPressed && !Looking)
        {
            GrabOrAddPoint(hit, hovered, aim);
        }

        if (_dragPoint >= 0 && Input.PaintHeld && HasBrushTarget
            && (_dragFollowing || Vector3.Distance(hit.point, _dragStart) > DragThreshold))
        {
            // From here on, the point follows the aim exactly.
            _dragFollowing = true;
            MovePoint(_dragPoint, hit);
        }

        if (Input.PaintReleased)
        {
            EndDrag();
        }
    }

    private void GrabOrAddPoint(RaycastHit hit, int hovered, Vector2 aim)
    {
        _lineBeforeDrag = new List<LinePoint>(Route.Line);
        if (hovered >= 0)
        {
            // Grab an existing point; it moves once the aim leaves the spot it was grabbed at.
            _dragPoint = hovered;
            _dragStart = hit.point;
            _dragFollowing = false;
            SelectedPoint = hovered;
            return;
        }

        if (!HasBrushTarget)
        {
            _lineBeforeDrag = null;
            return;
        }

        var point = new LinePoint(RouteOverlay.ToNumerics(hit.point), RouteOverlay.ToNumerics(hit.normal));
        var segment = _overlay.Line.PickSegment(_camera, aim);
        var index = segment >= 0 ? segment + 1 : Route.Line.Count;
        Route.Line.Insert(index, point);
        PushLineUndo();
        _overlay.SetLine(Route.Line);
        _dirty = true;
        _dragPoint = index;
        _dragFollowing = true;
        SelectedPoint = index;
        SetStatus(segment >= 0 ? $"Inserted line point {index + 1} of {Route.Line.Count}." : $"Added line point {Route.Line.Count}.");
    }

    private void MovePoint(int index, RaycastHit hit)
    {
        var point = new LinePoint(RouteOverlay.ToNumerics(hit.point), RouteOverlay.ToNumerics(hit.normal));
        if (index >= Route.Line.Count || Route.Line[index] == point)
        {
            return;
        }

        PushLineUndo();
        Route.Line[index] = point;
        _overlay.SetLine(Route.Line);
        _dirty = true;
    }

    /// <summary>Record the line as it was before the current press, once per press.</summary>
    private void PushLineUndo()
    {
        if (_lineBeforeDrag == null)
        {
            return;
        }

        _undo.Push((UndoKind.Line, null, null, _lineBeforeDrag));
        _lineBeforeDrag = null;
    }

    private void EndDrag()
    {
        _dragPoint = -1;
        _lineBeforeDrag = null;
    }

    private void DeletePoint()
    {
        if (Route.Line.Count == 0)
        {
            SetStatus("The route line has no points to delete.");
            return;
        }

        var index = SelectedPoint >= 0 && SelectedPoint < Route.Line.Count ? SelectedPoint : Route.Line.Count - 1;
        _undo.Push((UndoKind.Line, null, null, new List<LinePoint>(Route.Line)));
        Route.Line.RemoveAt(index);
        _overlay.SetLine(Route.Line);
        _dirty = true;
        // Step back along the line, so pressing delete again keeps removing towards the start.
        SelectedPoint = Route.Line.Count == 0 ? -1 : Math.Max(index - 1, 0);
        SetStatus($"Deleted line point {index + 1}; {Route.Line.Count} left.");
    }

    private void ClearLine()
    {
        if (Route.Line.Count == 0)
        {
            return;
        }

        _undo.Push((UndoKind.Line, null, null, new List<LinePoint>(Route.Line)));
        Route.Line.Clear();
        _overlay.SetLine(Route.Line);
        _dirty = true;
        SelectedPoint = -1;
        SetStatus("Cleared the route line (undo brings it back).");
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

        var aim = AimPosition();
        var ray = _camera.ScreenPointToRay(new Vector3(aim.x, aim.y, 0f));
        HasBrushTarget = Physics.Raycast(ray, out var hit, MaxPaintDistance, GameApi.SurfaceMask, QueryTriggerInteraction.Ignore);
        return hit;
    }

    /// <summary>With a gamepad there's no cursor, so it aims at the middle of the screen.</summary>
    private Vector2 AimPosition() =>
        Input.UsingGamepad ? new Vector2(Screen.width * 0.5f, Screen.height * 0.5f) : Input.PointerPosition;

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
        LoadOrCreate(zone);
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
            _undo.Push((UndoKind.Stroke, null, null, null));
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
        _undo.Push((UndoKind.Start, Route.Start, null, null));
        Route.Start = new StartPosition { Position = RouteOverlay.ToNumerics(point), YawDegrees = _yaw };
        _overlay.SetStart(Route.Start);
        _dirty = true;
        SetStatus("Start position set.");
    }

    private void SetFinish(Vector3 point)
    {
        _undo.Push((UndoKind.Finish, null, Route.Finish, null));
        Route.Finish = new FinishZone { Position = RouteOverlay.ToNumerics(point), Radius = Mathf.Max(Radius, MinFinishRadius) };
        _overlay.SetFinish(Route.Finish);
        _dirty = true;
        SetStatus($"Finish set ({Route.Finish.Radius:0.0} m radius).");
    }

    private void Undo()
    {
        EndStroke();
        EndDrag();
        if (_undo.Count == 0)
        {
            SetStatus("Nothing to undo.");
            return;
        }

        var (kind, previousStart, previousFinish, previousLine) = _undo.Pop();
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
            case UndoKind.Line:
                Route.Line.Clear();
                Route.Line.AddRange(previousLine);
                _overlay.SetLine(Route.Line);
                SelectedPoint = -1;
                break;
        }

        _dirty = true;
    }

    private void LoadOrCreate(string zone)
    {
        _undo.Clear();
        EndDrag();
        SelectedPoint = -1;
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
