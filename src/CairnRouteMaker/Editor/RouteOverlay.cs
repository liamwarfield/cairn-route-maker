using CairnRouteMaker.Game;
using CairnRouteMaker.Routes;
using MelonLoader;
using UnityEngine;
using NVector3 = System.Numerics.Vector3;
using Object = UnityEngine.Object;

namespace CairnRouteMaker.Editor;

/// <summary>
/// Draws a route in the world: the painted strokes (as decals, see <see cref="DecalPaint"/>),
/// the start marker and the brush preview. Nothing here has a collider, so neither the brush
/// nor Ava's grab checks can hit it.
/// </summary>
internal sealed class RouteOverlay
{
    private const int IgnoreRaycastLayer = 2;

    private static readonly Color AllowColor = new(1f, 0.82f, 0.15f, 0.35f);
    private static readonly Color DenyColor = new(0.95f, 0.15f, 0.1f, 0.45f);
    private static readonly Color StartColor = new(0.2f, 0.9f, 0.4f, 0.6f);

    private readonly GameObject _root;
    private readonly DecalPaint _paint;
    private int _strokeCount;
    private readonly GameObject _start;
    private readonly GameObject _brush;
    private Material _startMaterial;
    private Material _brushAllow;
    private Material _brushDeny;
    private bool _visible = true;

    public RouteOverlay()
    {
        _root = new GameObject("CairnRouteMaker.Overlay");
        Object.DontDestroyOnLoad(_root);
        CreateMaterials();
        _paint = new DecalPaint(_root.transform);

        _start = CreateStartMarker();
        _start.SetActive(false);

        _brush = CreatePrimitive(PrimitiveType.Sphere, _brushAllow, "Brush");
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

    public int PaintCellCount => _paint.CellCount;

    public void Rebuild(Route route)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        _paint.Clear();
        _strokeCount = 0;
        foreach (var stroke in route.Strokes)
        {
            BeginStroke();
            foreach (var stamp in stroke.Stamps)
            {
                AddStamp(stroke.Mode, stamp);
            }
        }

        _paint.Flush();
        SetStart(route.Start);
        MelonLogger.Msg($"Drew route: {_paint.CellCount} paint cells in {timer.ElapsedMilliseconds} ms.");
    }

    /// <summary>Push this frame's paint changes to the GPU.</summary>
    public void Flush() => _paint.Flush();

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

    public void ShowBrush(Vector3 position, float radius, StrokeMode mode)
    {
        _brush.SetActive(true);
        _brush.transform.position = position;
        _brush.transform.localScale = Vector3.one * radius * 2f;
        _brush.GetComponent<Renderer>().sharedMaterial = mode == StrokeMode.Allow ? _brushAllow : _brushDeny;
    }

    public void HideBrush() => _brush.SetActive(false);

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
        _brushAllow = CreateMaterial(new Color(AllowColor.r, AllowColor.g, AllowColor.b, 0.15f));
        _brushDeny = CreateMaterial(new Color(DenyColor.r, DenyColor.g, DenyColor.b, 0.15f));
    }

    private static Shader _shader;

    private static Material CreateMaterial(Color color)
    {
        var material = new Material(FindShader()) { color = color, renderQueue = 3000 };
        Object.DontDestroyOnLoad(material);
        return material;
    }

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
