using CairnRouteMaker.Game;
using CairnRouteMaker.Routes;
using MelonLoader;
using UnityEngine;
using NVector3 = System.Numerics.Vector3;
using Object = UnityEngine.Object;

namespace CairnRouteMaker.Editor;

/// <summary>
/// Draws a route in the world: the painted strokes (as decals, see <see cref="DecalPaint"/>),
/// the start and finish markers and the brush preview. Nothing here has a collider, so neither the brush
/// nor Ava's grab checks can hit it.
/// </summary>
internal sealed class RouteOverlay
{
    private const int IgnoreRaycastLayer = 2;

    private static readonly Color StartColor = new(0.2f, 0.9f, 0.4f, 0.6f);
    private static readonly Color FinishColor = new(0.25f, 0.6f, 1f, 0.3f);

    // Strong enough to read through the game's colour grading; erase is black so it can't be
    // mistaken for deny. Allow and deny take the paint colours (see PaintColors).
    private const float BrushAllowAlpha = 0.4f;
    private const float BrushDenyAlpha = 0.45f;
    private static readonly Color BrushEraseColor = new(0f, 0f, 0f, 0.55f);
    private static readonly Color MarkerColor = new(0.9f, 0.95f, 1f, 0.5f);

    private readonly GameObject _root;
    private readonly DecalPaint _paint;
    private int _strokeCount;
    private readonly GameObject _start;
    private readonly GameObject _finish;
    private readonly GameObject _brush;
    private Material _startMaterial;
    private Material _finishMaterial;
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

        _start = CreateStartMarker();
        _start.SetActive(false);

        _finish = CreatePrimitive(PrimitiveType.Sphere, _finishMaterial, "Finish");
        _finish.SetActive(false);

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
    /// Show a whole route. The paint is queued and drawn a few milliseconds per frame, so it
    /// fills in over a moment rather than stalling the game; the log reports the total.
    /// </summary>
    public void Rebuild(Route route)
    {
        _paint.Clear();
        _strokeCount = 0;
        foreach (var stroke in route.Strokes)
        {
            BeginStroke();
            foreach (var stamp in stroke.Stamps)
            {
                _paint.AddLater(_strokeCount - 1, stroke.Mode, stamp);
            }
        }

        _paint.ReportWhenDrawn();
        SetStart(route.Start);
        SetFinish(route.Finish);
    }

    /// <summary>Do this frame's share of queued paint drawing and pulse the glow; call every frame.</summary>
    public void Flush()
    {
        _paint.Flush();
        _paint.Animate(Time.unscaledTime);
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

    public void SetStart(StartPosition start)
    {
        _start.SetActive(start != null);
        if (start == null)
        {
            return;
        }

        _start.transform.position = ToUnity(start.Position);
        _start.transform.rotation = Quaternion.Euler(0f, start.YawDegrees, 0f);
    }

    /// <summary>The finish zone, drawn as the sphere Ava has to reach.</summary>
    public void SetFinish(FinishZone finish)
    {
        _finish.SetActive(finish != null);
        if (finish == null)
        {
            return;
        }

        _finish.transform.position = ToUnity(finish.Position);
        _finish.transform.localScale = Vector3.one * finish.Radius * 2f;
    }

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

    private GameObject CreateStartMarker()
    {
        // A body-height capsule standing on the start point, with a nose showing which way Ava faces.
        var marker = new GameObject("Start");
        marker.layer = IgnoreRaycastLayer;
        marker.transform.SetParent(_root.transform, false);

        var body = CreatePrimitive(PrimitiveType.Capsule, _startMaterial, "Body");
        body.transform.SetParent(marker.transform, false);
        body.transform.localPosition = new Vector3(0f, 0.85f, 0f);
        body.transform.localScale = new Vector3(0.5f, 0.85f, 0.5f);

        var nose = CreatePrimitive(PrimitiveType.Cube, _startMaterial, "Facing");
        nose.transform.SetParent(marker.transform, false);
        nose.transform.localPosition = new Vector3(0f, 1.4f, 0.35f);
        nose.transform.localScale = new Vector3(0.15f, 0.15f, 0.4f);
        return marker;
    }

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
        _startMaterial = CreateMaterial(StartColor);
        _finishMaterial = CreateMaterial(FinishColor);
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
    }

    private static Color WithAlpha(Color color, float alpha) => new(color.r, color.g, color.b, alpha);

    private static Shader _shader;

    private static Material CreateMaterial(Color color) =>
        RuntimeAssets.Keep(new Material(FindShader()) { color = color, renderQueue = 3000 });

    private static Shader FindShader()
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
