using System;
using System.Collections.Generic;
using CairnRouteMaker.Controls;
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
///
/// It shows one route at a time, or none: routes are chosen in the menus, never picked for Ava by
/// where she is. Switching to editing with no route showing starts a new one in Ava's zone.
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

    /// <summary>How long Ava must be settled in a route's zone before its paint is drawn.</summary>
    private const float PaintSettleSeconds = 2f;

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
    private readonly Stack<(UndoKind Kind, StartPosition PreviousStart, FinishZone PreviousFinish, LineSnapshot PreviousLines)> _undo = new();

    private Camera _camera;
    private float _yaw;
    private float _pitch;
    private Stroke _stroke;
    private bool _dirty;

    /// <summary>The route's paint is waiting for its rock to load; see <see cref="DrawPaintWhenReady"/>.</summary>
    private bool _paintPending;
    private float _paintReadySince = -1f;
    private bool _sawInput;
    private float _nextStreamUpdate;
    private int _openedFrame;

    /// <summary>The route's lines and which was being edited, for undo.</summary>
    private sealed class LineSnapshot
    {
        public LineSnapshot(List<ClimbLine> lines, int active)
        {
            Lines = lines;
            Active = active;
        }

        public List<ClimbLine> Lines { get; }
        public int Active { get; }
    }

    // Line tool: the point being dragged (or -1), where the drag started on the rock, and the lines
    // as they were before the drag, which become an undo step once the drag actually changes something.
    private int _dragPoint = -1;
    private Vector3 _dragStart;
    private bool _dragFollowing;
    private LineSnapshot _linesBeforeDrag;

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

    /// <summary>The route on show, or null. Never null while editing.</summary>
    public Route Route { get; private set; }
    public StrokeMode Mode { get; private set; } = StrokeMode.Allow;

    /// <summary>The fourth brush: places and moves the route line's control points instead of painting.</summary>
    public bool LineTool { get; private set; }

    /// <summary>The route line the line tool works on, or -1 before the route has one.</summary>
    public int ActiveLine { get; private set; } = -1;

    /// <summary>The active line's point that delete removes and that's drawn largest, or -1 for the last one.</summary>
    public int SelectedPoint { get; private set; } = -1;

    private ClimbLine Active => ActiveLine >= 0 && ActiveLine < Route.Lines.Count ? Route.Lines[ActiveLine] : null;
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

        if (!GameApi.IsAvaReady || GameApi.CurrentZoneKey == null)
        {
            SetStatus("Load into the mountain before opening the route editor.");
            return;
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
        // Editing is always a deliberate switch (R3 / F8 by default), so nothing gets changed by accident.
        Freecam = true;
        IsOpen = true;
        SetStatus(FreecamHint);
    }

    private static string FreecamHint =>
        $"Freecam: {ModControls.Teleport.PadName} or {ModControls.Teleport.KeyName} moves Ava to the marker.";

    public void Close()
    {
        if (!IsOpen)
        {
            return;
        }

        EndStroke();
        EndDrag();
        if (IsBlankNewRoute)
        {
            _dirty = false;
            SetRoute(null);
        }
        else
        {
            SaveChanges();
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

    /// <summary>Whether this is the route on show, the same saved file if not the same object.</summary>
    public bool IsShowing(Route route) => route != null && (route == Route || (route.Id != null && route.Id == Route?.Id));

    /// <summary>
    /// Show a saved route, or none. Walking into another zone keeps it showing, so a climb can
    /// cross a zone boundary. Changes to the route it replaces are saved first.
    /// </summary>
    public void ShowRoute(Route route)
    {
        // The one already showing may have changes its file doesn't.
        if (!IsShowing(route))
        {
            SetRoute(route);
        }
    }

    /// <summary>Start a new route in Ava's zone and switch to editing it.</summary>
    public void EditNewRoute()
    {
        var zone = GameApi.CurrentZoneKey;
        if (zone == null)
        {
            SetStatus("Load into the mountain before starting a route.");
            return;
        }

        var route = _storage.Create(zone);
        route.GameVersion = GameApi.GameVersion;
        SetRoute(route);
        Freecam = false;
        SetStatus($"New route: {route.Name} in {ZoneNames.PrettyKey(zone)}.");
    }

    /// <summary>A route file was deleted from the menu: if it's the one on show, stop showing it.</summary>
    public void OnRouteDeleted(Route route)
    {
        if (IsShowing(route))
        {
            // Its file is gone; saving now would bring it back.
            _dirty = false;
            SetRoute(null);
        }
    }

    /// <summary>
    /// A route's name, grade or description was changed from the menu: if it's the one on show,
    /// the editor's copy takes them too, or its next save would undo the change.
    /// </summary>
    public void OnRouteDetailsChanged(Route route)
    {
        if (IsShowing(route))
        {
            Route.Name = route.Name;
            Route.Grade = route.Grade;
            Route.Description = route.Description;
        }
    }

    /// <summary>
    /// Switch between editing and freecam, opening in freecam if closed. Editing with no route
    /// on show starts a new one.
    /// </summary>
    public void ToggleFreecam()
    {
        if (!IsOpen)
        {
            Open();
            return;
        }

        EndStroke();
        EndDrag();
        if (!Freecam)
        {
            Freecam = true;
            SetStatus(FreecamHint);
        }
        else if (Route == null)
        {
            EditNewRoute();
        }
        else if (_paintPending)
        {
            // Undo and new strokes need the paint on screen to match the route.
            SetStatus($"{Route.Name} can be edited once its paint is drawn.");
        }
        else
        {
            Freecam = false;
            SetStatus($"Editing {Route.Name} ({ZoneNames.PrettyKey(Route.ZoneKey)}).");
        }
    }

    /// <summary>
    /// Put Ava on the route's start, closing the editor first if it's open. If the route belongs
    /// to another zone, travel there first.
    /// </summary>
    public void PlayTest()
    {
        if (Route == null)
        {
            SetStatus("No route is showing: pick one in the level select.");
            return;
        }

        var start = Route.Start;
        if (start == null)
        {
            SetStatus($"This route has no start position yet ({ModControls.SetStart.KeyName}, or {ModControls.SetStart.PadName} on a gamepad, in the editor).");
            return;
        }

        Close();
        var zone = GameApi.CurrentZoneKey;
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
        DrawPaintWhenReady();
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
            // So the menu lists the route as it is now.
            SaveChanges();
            LevelSelect.Open(this, _storage, _traveler, SetStatus);
            return;
        }

        if (Input.ToggleFreecam)
        {
            ToggleFreecam();
        }

        if (Input.ToggleLine)
        {
            PaintColors.SetShowLine(!PaintColors.ShowLine);
            SetStatus(PaintColors.ShowLine ? "Route lines shown." : "Route lines hidden (they still show while the line tool is in use).");
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
                ? $"Line tool: {ModControls.Paint.PadName} on rock adds a point, on a point or the line grabs it."
                : $"Line tool: {ModControls.Paint.KeyName} on rock adds a point; drag a point, or the line, to move it.");
        }
    }

    /// <summary>
    /// The line tool, which works on one line at a time. Pressing on one of its control points grabs
    /// it; pressing on the line between two points puts a new point there and grabs that; pressing on
    /// another line makes that the one being edited; pressing anywhere else on rock adds a point at the
    /// end. Holding on drags the grabbed point over the rock. Delete removes the selected point (the
    /// last one if none is), and every change is one undo step.
    /// </summary>
    private void UpdateLineTool(RaycastHit hit)
    {
        var aim = AimPosition();
        var line = Active == null ? null : _overlay.LineAt(ActiveLine);
        var hovered = _dragPoint >= 0 ? _dragPoint : Looking || line == null ? -1 : line.PickPoint(_camera, aim);
        if (line != null)
        {
            line.Hovered = hovered;
            line.Selected = SelectedPoint;
        }

        if (HasBrushTarget && !Looking && hovered < 0)
        {
            _overlay.ShowMarker(hit.point, LineMarkerRadius);
        }
        else
        {
            _overlay.HideBrush();
        }

        if (Input.NewLine)
        {
            EndDrag();
            NewLine();
        }
        else if (Input.CycleLineColor)
        {
            EndDrag();
            CycleLineColor();
        }
        else if (Input.ClearLine)
        {
            EndDrag();
            DeleteLine();
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
        _linesBeforeDrag = Snapshot();
        if (hovered >= 0)
        {
            // Grab an existing point; it moves once the aim leaves the spot it was grabbed at.
            _dragPoint = hovered;
            _dragStart = hit.point;
            _dragFollowing = false;
            SelectedPoint = hovered;
            return;
        }

        var other = PickOtherLine(aim);
        if (other >= 0)
        {
            _linesBeforeDrag = null;
            SelectLine(other);
            SetStatus($"Editing {LineName(ActiveLine)}.");
            return;
        }

        if (!HasBrushTarget)
        {
            _linesBeforeDrag = null;
            return;
        }

        if (Active == null)
        {
            Route.Lines.Add(new ClimbLine { Grade = UnusedGrade(LineGrade.Green) });
            _overlay.SetLines(Route.Lines);
            SelectLine(Route.Lines.Count - 1);
        }

        var points = Active.Points;
        var point = new LinePoint(RouteOverlay.ToNumerics(hit.point), RouteOverlay.ToNumerics(hit.normal));
        var segment = _overlay.LineAt(ActiveLine).PickSegment(_camera, aim);
        var index = segment >= 0 ? segment + 1 : points.Count;
        points.Insert(index, point);
        PushLineUndo();
        _overlay.SetLine(ActiveLine, Active);
        _dirty = true;
        _dragPoint = index;
        _dragFollowing = true;
        SelectedPoint = index;
        SetStatus(segment >= 0 ? $"Inserted point {index + 1} of {points.Count} on {LineName(ActiveLine)}." : $"Added point {points.Count} to {LineName(ActiveLine)}.");
    }

    /// <summary>Another line drawn under the cursor (a point or the line itself), or -1.</summary>
    private int PickOtherLine(Vector2 aim)
    {
        for (var i = 0; i < Route.Lines.Count; i++)
        {
            if (i != ActiveLine && Route.Lines[i].Points.Count > 0
                && (_overlay.LineAt(i).PickPoint(_camera, aim) >= 0 || _overlay.LineAt(i).PickSegment(_camera, aim) >= 0))
            {
                return i;
            }
        }

        return -1;
    }

    private void MovePoint(int index, RaycastHit hit)
    {
        var points = Active?.Points;
        var point = new LinePoint(RouteOverlay.ToNumerics(hit.point), RouteOverlay.ToNumerics(hit.normal));
        if (points == null || index >= points.Count || points[index] == point)
        {
            return;
        }

        PushLineUndo();
        points[index] = point;
        _overlay.SetLine(ActiveLine, Active);
        _dirty = true;
    }

    /// <summary>Make a line the one being edited, dropping any other line that never got a point.</summary>
    private void SelectLine(int index)
    {
        var line = index >= 0 && index < Route.Lines.Count ? Route.Lines[index] : null;
        if (Route.Lines.RemoveAll(other => other != line && other.Points.Count == 0) > 0)
        {
            _overlay.SetLines(Route.Lines);
        }

        ActiveLine = line == null ? -1 : Route.Lines.IndexOf(line);
        SelectedPoint = -1;
        _overlay.ActiveLine = ActiveLine;
    }

    /// <summary>Start another line; it's drawn from the next point placed.</summary>
    private void NewLine()
    {
        if (Active is { Points.Count: 0 })
        {
            SetStatus($"{Capitalised(LineName(ActiveLine))} is new: place its first point.");
            return;
        }

        _undo.Push((UndoKind.Line, null, null, Snapshot()));
        Route.Lines.Add(new ClimbLine { Grade = UnusedGrade(Active == null ? LineGrade.Green : LineColors.Next(Active.Grade)) });
        _overlay.SetLines(Route.Lines);
        SelectLine(Route.Lines.Count - 1);
        _dirty = true;
        SetStatus($"Started {LineName(ActiveLine)}: place its first point.");
    }

    private void CycleLineColor()
    {
        if (Active == null)
        {
            SetStatus("There's no line to colour yet: place a point first.");
            return;
        }

        _undo.Push((UndoKind.Line, null, null, Snapshot()));
        Active.Grade = LineColors.Next(Active.Grade);
        _overlay.SetLine(ActiveLine, Active);
        _dirty = true;
        SetStatus($"{Capitalised(LineName(ActiveLine))}.");
    }

    /// <summary>The first colour from this one up the scale that no other line uses, or this one if all are taken.</summary>
    private LineGrade UnusedGrade(LineGrade from)
    {
        var grade = from;
        for (var i = 0; i < LineColors.All.Length; i++)
        {
            var taken = grade;
            if (!Route.Lines.Exists(line => line.Grade == taken))
            {
                return grade;
            }

            grade = LineColors.Next(grade);
        }

        return from;
    }

    /// <summary>"line 2 (blue)", or "the line (blue)" when there's only one.</summary>
    private string LineName(int index)
    {
        var colour = LineColors.NameOf(Route.Lines[index].Grade).ToLowerInvariant();
        return Route.Lines.Count == 1 ? $"the line ({colour})" : $"line {index + 1} ({colour})";
    }

    private static string Capitalised(string text) => char.ToUpperInvariant(text[0]) + text.Substring(1);

    private LineSnapshot Snapshot() => new(Route.Lines.ConvertAll(line => line.Clone()), ActiveLine);

    /// <summary>Record the lines as they were before the current press, once per press.</summary>
    private void PushLineUndo()
    {
        if (_linesBeforeDrag == null)
        {
            return;
        }

        _undo.Push((UndoKind.Line, null, null, _linesBeforeDrag));
        _linesBeforeDrag = null;
    }

    private void EndDrag()
    {
        _dragPoint = -1;
        _linesBeforeDrag = null;
    }

    private void DeletePoint()
    {
        var points = Active?.Points;
        if (points == null || points.Count == 0)
        {
            SetStatus("The line has no points to delete.");
            return;
        }

        var index = SelectedPoint >= 0 && SelectedPoint < points.Count ? SelectedPoint : points.Count - 1;
        _undo.Push((UndoKind.Line, null, null, Snapshot()));
        points.RemoveAt(index);
        _overlay.SetLine(ActiveLine, Active);
        _dirty = true;
        // Step back along the line, so pressing delete again keeps removing towards the start.
        SelectedPoint = points.Count == 0 ? -1 : Math.Max(index - 1, 0);
        SetStatus($"Deleted point {index + 1} of {LineName(ActiveLine)}; {points.Count} left.");
    }

    /// <summary>Remove the line being edited; the one before it becomes the line being edited.</summary>
    private void DeleteLine()
    {
        if (Active == null)
        {
            return;
        }

        var name = LineName(ActiveLine);
        _undo.Push((UndoKind.Line, null, null, Snapshot()));
        var index = ActiveLine;
        Route.Lines.RemoveAt(index);
        _overlay.SetLines(Route.Lines);
        SelectLine(Math.Min(Math.Max(index - 1, 0), Route.Lines.Count - 1));
        _dirty = true;
        SetStatus($"Deleted {name} (undo brings it back).");
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
            if (GameApi.TryQuickTeleportAva(hit, _yaw, out var reason))
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
        if (LevelSelect.UpdateTextEntry())
        {
            return;
        }

        if (Input.MenuBack)
        {
            LevelSelect.Back();
            return;
        }

        if (Input.MenuEdit)
        {
            LevelSelect.Edit();
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
    /// back to the game); other entries change the menu page, or close it to carry on editing.
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

        var (kind, previousStart, previousFinish, previousLines) = _undo.Pop();
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
                Route.Lines.Clear();
                Route.Lines.AddRange(previousLines.Lines);
                _overlay.SetLines(Route.Lines);
                ActiveLine = previousLines.Active;
                _overlay.ActiveLine = ActiveLine;
                SelectedPoint = -1;
                break;
        }

        _dirty = true;
    }

    /// <summary>Replace the route on show, saving changes to the old one. With none left to edit, go back to freecam.</summary>
    private void SetRoute(Route route)
    {
        EndStroke();
        EndDrag();
        SaveChanges();
        _undo.Clear();
        SelectedPoint = -1;
        Route = route;
        ActiveLine = route is { Lines.Count: > 0 } ? 0 : -1;
        _dirty = false;
        _overlay.Rebuild(null);
        _overlay.ActiveLine = ActiveLine;
        // The markers need no rock, so they show straight away.
        _overlay.SetStart(route?.Start);
        _overlay.SetFinish(route?.Finish);
        _paintPending = route != null;
        _paintReadySince = -1f;
        DrawPaintWhenReady();
        if (route == null || _paintPending)
        {
            Freecam = true;
        }
        else
        {
            _overlay.Visible = true;
        }

        MelonLogger.Msg(route == null ? "No route showing." : $"Showing {route.Name} ({route.ZoneKey}, {route.Id ?? "not saved yet"}).");
    }

    /// <summary>
    /// Draw the route once its rock is there to paint on: Ava settled on its mountain, on her feet
    /// or climbing, with nothing loading and no trip under way. Paint finds the rock with raycasts
    /// and remembers where there was none, so drawn any sooner (during the trip or warp to the
    /// start, or in the zone the game briefly drops Ava in on a zone trip) it would stay
    /// invisible. It doesn't wait for the route's own zone: a route can start or run on into the
    /// next one. A route with no paint has nothing to wait for.
    /// </summary>
    private void DrawPaintWhenReady()
    {
        if (!_paintPending)
        {
            return;
        }

        if (Route.Strokes.Count > 0)
        {
            var mode = GameApi.Switcher?.CurrentMode;
            var settled = ZoneNames.World(Route.ZoneKey) == ZoneNames.World(GameApi.CurrentZoneKey) && GameApi.CanMoveAva(out _) && !_traveler.IsActive
                && mode is Il2Cpp.PawnControllerSwitcher.Mode.Walking or Il2Cpp.PawnControllerSwitcher.Mode.Climbing;
            if (!settled)
            {
                _paintReadySince = -1f;
                return;
            }

            if (_paintReadySince < 0f)
            {
                _paintReadySince = Time.unscaledTime;
            }

            if (Time.unscaledTime - _paintReadySince < PaintSettleSeconds)
            {
                return;
            }
        }

        _paintPending = false;
        _overlay.Rebuild(Route);
    }

    /// <summary>A new route that nothing has been drawn on: it isn't worth a file, so it's dropped when left.</summary>
    private bool IsBlankNewRoute => Route is { Id: null, IsEmpty: true };

    private void SaveChanges()
    {
        if (_dirty && !IsBlankNewRoute)
        {
            Save();
        }
    }

    private void Save()
    {
        try
        {
            Route.GameVersion = GameApi.GameVersion;
            var path = _storage.Save(Route);
            _dirty = false;
            SetStatus($"Saved {Route.Name}.");
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
