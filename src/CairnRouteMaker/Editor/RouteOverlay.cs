using System;
using System.Collections.Generic;
using CairnRouteMaker.Game;
using CairnRouteMaker.Routes;
using MelonLoader;
using UnityEngine;
using NVector3 = System.Numerics.Vector3;
using Object = UnityEngine.Object;

namespace CairnRouteMaker.Editor;

/// <summary>
/// Draws a route in the world: the painted strokes (as decals, see <see cref="DecalPaint"/>),
/// the route lines (see <see cref="RouteLine"/>), the start and finish markers (see <see cref="StartMarker"/>
/// and <see cref="FinishMarker"/>) and the brush preview. Nothing here has a collider, so neither the brush
/// nor Ava's grab checks can hit it.
/// </summary>
internal sealed class RouteOverlay
{
    private const int IgnoreRaycastLayer = 2;

    // Strong enough to read through the game's colour grading; erase is black so it can't be
    // mistaken for deny. Allow and deny take the paint colours (see PaintColors).
    private const float BrushAllowAlpha = 0.4f;
    private const float BrushDenyAlpha = 0.45f;
    private static readonly Color BrushEraseColor = new(0f, 0f, 0f, 0.55f);
    private static readonly Color MarkerColor = new(0.9f, 0.95f, 1f, 0.5f);

    private readonly GameObject _root;
    private readonly DecalPaint _paint;
    /// <summary>One renderer per route line; spares beyond the route's lines are hidden and reused.</summary>
    private readonly List<RouteLine> _lines = new();
    private int _lineCount;
    private int _activeLine = -1;
    private bool _editingLine;
    private int _strokeCount;
    private readonly StartMarker _start;
    private readonly FinishMarker _finish;
    private readonly GameObject _brush;
    private Material[] _brushMaterials;
    private Material _markerMaterial;
    private bool _visible = true;

    public RouteOverlay()
    {
        _root = new GameObject("CairnRouteMaker.Overlay");
        Object.DontDestroyOnLoad(_root);
        CreateMaterials();
        _paint = new DecalPaint(_root.transform)
        {
            DrawingFinished = summary => MelonLogger.Msg($"Drew route paint: {summary}"),
        };
        ApplyPaintColors();
        PaintColors.Changed += ApplyPaintColors;

        _start = new StartMarker(_root.transform, _paint);
        _finish = new FinishMarker(_root.transform, _paint);

        _brush = CreatePrimitive(PrimitiveType.Sphere, _brushMaterials[0], "Brush");
        _brush.SetActive(false);
    }

    public bool Visible
    {
        get => _visible;
        set
        {
            _visible = value;
            _root.SetActive(value);
        }
    }

    /// <summary>
    /// Show a whole route, or nothing for null. The paint is queued and drawn a few milliseconds
    /// per frame, so it fills in over a moment rather than stalling the game; the log reports the total.
    /// </summary>
    public void Rebuild(Route route)
    {
        _paint.Clear();
        _strokeCount = 0;
        SetStart(route?.Start);
        SetFinish(route?.Finish);
        SetLines(route == null ? System.Array.Empty<ClimbLine>() : route.Lines);
        if (route == null)
        {
            return;
        }

        foreach (var stroke in route.Strokes)
        {
            BeginStroke();
            foreach (var stamp in stroke.Stamps)
            {
                _paint.AddLater(_strokeCount - 1, stroke.Mode, stamp);
            }
        }

        _paint.ReportWhenDrawn();
    }

    /// <summary>Do this frame's share of queued paint drawing and pulse the glow; call every frame.</summary>
    public void Flush()
    {
        _paint.Flush();
        _paint.Animate(Time.unscaledTime);
    }

    /// <summary>Lay the route lines and markers out for the camera; call after the camera has moved for the frame.</summary>
    public void LateUpdate()
    {
        var camera = Camera.main;
        var time = Time.unscaledTime;
        try
        {
            _start.Update(camera, time);
            _finish.Update(camera, time);
        }
        catch (Exception e)
        {
            // As with a line: hide the markers until the next change instead of logging every frame.
            MelonLogger.Error($"Drawing the start and finish markers failed: {e}");
            _start.Set(null);
            _finish.Set(null);
        }

        for (var i = 0; i < _lineCount; i++)
        {
            try
            {
                _lines[i].Update(camera);
            }
            catch (Exception e)
            {
                // Every frame would log the same thing: hide the line until the next change instead.
                MelonLogger.Error($"Drawing route line {i + 1} failed: {e}");
                _lines[i].Visible = false;
            }
        }
    }

    public int LineCount => _lineCount;

    /// <summary>A route line's renderer, for the editor's picking and its selection and hover highlights.</summary>
    public RouteLine LineAt(int index) => _lines[index];

    /// <summary>Show these lines, reusing renderers (and their unchanged pieces) where they're already drawn.</summary>
    public void SetLines(IReadOnlyList<ClimbLine> lines)
    {
        while (_lines.Count < lines.Count)
        {
            _lines.Add(new RouteLine(_root.transform));
        }

        _lineCount = lines.Count;
        for (var i = 0; i < _lines.Count; i++)
        {
            if (i < lines.Count)
            {
                SetLine(i, lines[i]);
            }
            else
            {
                _lines[i].SetPoints(System.Array.Empty<LinePoint>());
            }
        }

        if (_activeLine >= _lineCount)
        {
            _activeLine = -1;
        }

        ApplyLineVisibility();
    }

    /// <summary>Redraw one line after its points or colour changed.</summary>
    public void SetLine(int index, ClimbLine line)
    {
        _lines[index].SetColor(line.Grade);
        _lines[index].SetPoints(line.Points);
    }

    /// <summary>The line being edited: only it shows every control point. -1 for none.</summary>
    public int ActiveLine
    {
        get => _activeLine;
        set
        {
            _activeLine = value;
            ApplyLineVisibility();
        }
    }

    /// <summary>While the line tool is in use the lines show, even if they're switched off.</summary>
    public bool EditingLine
    {
        get => _editingLine;
        set
        {
            if (_editingLine != value)
            {
                _editingLine = value;
                ApplyLineVisibility();
            }
        }
    }

    private void ApplyLineVisibility()
    {
        for (var i = 0; i < _lines.Count; i++)
        {
            var line = _lines[i];
            var active = _editingLine && i == _activeLine;
            line.Visible = i < _lineCount && (_editingLine || PaintColors.ShowLine);
            line.ShowControlPoints = active;
            if (!active)
            {
                line.Selected = -1;
                line.Hovered = -1;
            }
        }
    }

    public void BeginStroke() => _strokeCount++;

    public void AddStamp(StrokeMode mode, Stamp stamp) => _paint.Add(_strokeCount - 1, mode, stamp);

    public void RemoveLastStroke()
    {
        if (_strokeCount == 0)
        {
            return;
        }

        _strokeCount--;
        _paint.RemoveStroke(_strokeCount);
    }

    public void SetStart(StartPosition start) => _start.Set(start);

    /// <summary>The finish zone, drawn as the sphere Ava has to reach.</summary>
    public void SetFinish(FinishZone finish) => _finish.Set(finish);

    /// <summary>A translucent sphere the size of the brush, tinted by mode.</summary>
    public void ShowBrush(Vector3 position, float radius, StrokeMode mode) => ShowSphere(position, radius, _brushMaterials[(int)mode]);

    /// <summary>The freecam's marker: where Ava goes if she's moved. Shares the brush's sphere.</summary>
    public void ShowMarker(Vector3 position, float radius) => ShowSphere(position, radius, _markerMaterial);

    public void HideBrush() => _brush.SetActive(false);

    private void ShowSphere(Vector3 position, float radius, Material material)
    {
        _brush.SetActive(true);
        _brush.transform.position = position;
        _brush.transform.localScale = Vector3.one * radius * 2f;
        _brush.GetComponent<Renderer>().sharedMaterial = material;
    }

    public static Vector3 ToUnity(NVector3 v) => new(v.X, v.Y, v.Z);

    public static NVector3 ToNumerics(Vector3 v) => new(v.x, v.y, v.z);

    private GameObject CreatePrimitive(PrimitiveType type, Material material, string name)
    {
        var go = GameObject.CreatePrimitive(type);
        go.name = name;
        go.layer = IgnoreRaycastLayer;
        Object.Destroy(go.GetComponent<Collider>());
        go.transform.SetParent(_root.transform, false);
        var renderer = go.GetComponent<Renderer>();
        renderer.sharedMaterial = material;
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        renderer.receiveShadows = false;
        return go;
    }

    private void CreateMaterials()
    {
        // Allow and deny are tinted by ApplyPaintColors.
        _brushMaterials = new[] { CreateMaterial(Color.clear), CreateMaterial(Color.clear), CreateMaterial(BrushEraseColor) };
        _markerMaterial = CreateMaterial(MarkerColor);
    }

    /// <summary>Redraw the paint in the current paint colours and style, and retint the brush.</summary>
    private void ApplyPaintColors()
    {
        _paint.SetColors(PaintColors.Allow, PaintColors.Deny);
        _paint.SetStyle(PaintColors.ShowsFill, PaintColors.ShowsGlow);
        _paint.GlowPulse = PaintColors.GlowPulse;
        _brushMaterials[(int)StrokeMode.Allow].color = WithAlpha(PaintColors.Allow, BrushAllowAlpha);
        _brushMaterials[(int)StrokeMode.Deny].color = WithAlpha(PaintColors.Deny, BrushDenyAlpha);
        ApplyLineVisibility();
    }

    private static Color WithAlpha(Color color, float alpha) => new(color.r, color.g, color.b, alpha);

    private static Shader _shader;

    private static Material CreateMaterial(Color color) =>
        RuntimeAssets.Keep(new Material(FindShader()) { color = color, renderQueue = 3000 });

    internal static Shader FindShader()
    {
        if (_shader != null)
        {
            return _shader;
        }

        // IL2CPP builds only ship shaders something referenced, so try the usual transparent ones.
        foreach (var name in new[] { "Sprites/Default", "Unlit/Transparent", "Legacy Shaders/Transparent/Diffuse", "Hidden/Internal-Colored", "Unlit/Color" })
        {
            var shader = Shader.Find(name);
            if (shader != null)
            {
                MelonLogger.Msg($"Overlay shader: {name}");
                return _shader = shader;
            }
        }

        var fallback = GameApi.LineMaterialShader();
        MelonLogger.Warning($"No standard overlay shader found; using {(fallback != null ? fallback.name : "nothing")}.");
        return _shader = fallback;
    }
}
