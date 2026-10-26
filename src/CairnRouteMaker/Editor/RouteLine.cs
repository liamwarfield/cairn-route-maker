using System;
using System.Collections.Generic;
using CairnRouteMaker.Game;
using CairnRouteMaker.Routes;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using MelonLoader;
using UnityEngine;
using UnityEngine.Rendering;
using NVector2 = System.Numerics.Vector2;
using NVector3 = System.Numerics.Vector3;
using Object = UnityEngine.Object;

namespace CairnRouteMaker.Editor;

/// <summary>
/// Draws a route line like the game's map routes: a thick, smooth, dark-bordered line over the rock,
/// with white-ringed dots at its ends and, while editing, at each control point.
///
/// The curve (see <see cref="RouteCurve"/>) is sampled every <see cref="SampleSpacing"/> and each
/// sample is pushed onto the rock by a raycast along the normal, so the line follows the surface
/// between control points instead of cutting through bumps. Samples whose ray finds no rock (zone
/// not streamed in, or a gap) keep the curve's position and are retried every few seconds.
///
/// Everything is one camera-facing ribbon mesh rebuilt when the camera moves, so its width is a
/// fixed number of pixels at any distance (but never thinner than <see cref="MinWorldPerPixel"/>
/// allows up close). Each vertex is slid toward the camera along its view ray, which leaves it at the
/// same spot on screen but in front of rock within a metre or so of the line; larger things in front
/// (a ridge, Ava) still hide it.
/// One mesh and one draw call: triangles are drawn in order, so the border goes first, then the line,
/// then the dots.
/// </summary>
internal sealed class RouteLine
{
    private const int IgnoreRaycastLayer = 2;

    /// <summary>Sizes are in pixels on a 1080-pixel-high screen and scale with the screen.</summary>
    private const float ReferenceScreenHeight = 1080f;

    private const float LinePixels = 7f;
    private const float BorderPixels = 1.5f;
    private const float EndDotPixels = 8f;
    private const float EndRingPixels = 3f;
    private const float PointDotPixels = 4.5f;
    private const float PointRingPixels = 2f;
    private const float SelectedScale = 1.6f;
    private const float HoveredScale = 1.3f;
    private const int DiscSegments = 20;

    /// <summary>How near the cursor has to be to grab a control point, or the line between them.</summary>
    private const float PickPointPixels = 16f;
    private const float PickLinePixels = 6f;

    /// <summary>Up close, a few pixels would be a thread: one pixel never covers less than this, in metres.</summary>
    private const float MinWorldPerPixel = 0.12f / LinePixels;

    private const float SampleSpacing = 0.25f;

    /// <summary>A very long line is sampled more coarsely so the mesh stays well under 65k vertices.</summary>
    private const int MaxSamples = 6000;

    /// <summary>The rock is looked for this far out from the curve and as far behind it.</summary>
    private const float SnapReach = 2f;

    /// <summary>Lifts the line just off the rock, before the camera-ward slide.</summary>
    private const float SurfaceOffset = 0.04f;

    /// <summary>How far each vertex slides toward the camera: a base plus a little per metre away.</summary>
    private const float DepthBias = 0.3f;
    private const float DepthBiasPerMetre = 0.015f;

    private const float SnapRetrySeconds = 2f;

    private static readonly Color32 Border = new(16, 16, 20, 230);
    private static readonly Color32 Ring = new(250, 250, 250, 255);

    /// <summary>One piece between two control points, remembered so moving a point only redoes its neighbours.</summary>
    private sealed class Segment
    {
        public (NVector3, NVector3, NVector3, NVector3, NVector3, NVector3, float) Key;
        public readonly List<CurveSample> Curve = new();
        public readonly List<CurveSample> Snapped = new();
        public int Misses;
    }

    private readonly struct Dot
    {
        public Dot(NVector3 position, float fill, float ring, int point)
        {
            Position = position;
            Fill = fill;
            Ring = ring;
            Point = point;
        }

        public NVector3 Position { get; }
        public float Fill { get; }
        public float Ring { get; }

        /// <summary>Which control point it marks, for the selected and hovered highlights.</summary>
        public int Point { get; }
    }

    private readonly GameObject _object;
    private readonly Mesh _mesh;
    private readonly List<LinePoint> _points = new();
    private readonly List<Segment> _segments = new();
    private readonly List<Dot> _dots = new();
    private NVector3[] _positions = Array.Empty<NVector3>();

    /// <summary>For each entry of <see cref="_positions"/>, the segment it lies on.</summary>
    private int[] _sampleSegments = Array.Empty<int>();
    private NVector3[] _scratch = Array.Empty<NVector3>();
    private Il2CppStructArray<Vector3> _vertices;
    private NVector3[] _built = Array.Empty<NVector3>();
    private Color32 _color = new(70, 210, 90, 255);
    private bool _showPoints;
    private int _selected = -1;
    private int _hovered = -1;
    private bool _topologyDirty = true;
    private bool _positionsDirty = true;
    private float _nextSnapRetry;
    private (Vector3 Position, Quaternion Rotation, float Fov, int Width, int Height) _lastView;

    public RouteLine(Transform parent)
    {
        _object = new GameObject("RouteLine") { layer = IgnoreRaycastLayer };
        _object.transform.SetParent(parent, false);
        _mesh = RuntimeAssets.Keep(new Mesh { name = "RouteLine" });
        _mesh.MarkDynamic();
        _mesh.indexFormat = IndexFormat.UInt32;
        _object.AddComponent<MeshFilter>().sharedMesh = _mesh;
        var renderer = _object.AddComponent<MeshRenderer>();
        renderer.sharedMaterial = CreateMaterial();
        renderer.shadowCastingMode = ShadowCastingMode.Off;
        renderer.receiveShadows = false;
        renderer.allowOcclusionWhenDynamic = false;
    }

    public bool Visible
    {
        get => _object.activeSelf;
        set => _object.SetActive(value);
    }

    /// <summary>Draw a dot on every control point (for editing), not just the two ends.</summary>
    public bool ShowControlPoints
    {
        get => _showPoints;
        set
        {
            if (_showPoints != value)
            {
                _showPoints = value;
                RebuildDots();
            }
        }
    }

    /// <summary>The control point drawn largest, or -1.</summary>
    public int Selected
    {
        get => _selected;
        set
        {
            if (_selected != value)
            {
                _selected = value;
                _positionsDirty = true;
            }
        }
    }

    /// <summary>The control point under the cursor, drawn a little larger, or -1.</summary>
    public int Hovered
    {
        get => _hovered;
        set
        {
            if (_hovered != value)
            {
                _hovered = value;
                _positionsDirty = true;
            }
        }
    }

    public void SetColor(Color32 color)
    {
        _color = color;
        _topologyDirty = true;
    }

    /// <summary>Show a line through these points. Pieces whose points didn't change are reused.</summary>
    public void SetPoints(IReadOnlyList<LinePoint> points)
    {
        _points.Clear();
        _points.AddRange(points);
        var spacing = Spacing();
        var mask = GameApi.SurfaceMask;
        var count = RouteCurve.SegmentCount(_points.Count);
        while (_segments.Count > count)
        {
            _segments.RemoveAt(_segments.Count - 1);
        }

        for (var i = 0; i < count; i++)
        {
            if (i == _segments.Count)
            {
                _segments.Add(new Segment());
            }

            var segment = _segments[i];
            var (p0, p1, p2, p3) = RouteCurve.Neighbours(_points, i);
            var key = (p0, p1, p2, p3, _points[i].Normal, _points[i + 1].Normal, spacing);
            if (segment.Curve.Count > 0 && segment.Key == key)
            {
                continue;
            }

            segment.Key = key;
            segment.Curve.Clear();
            RouteCurve.SampleSegment(_points, i, spacing, segment.Curve, includeStart: true);
            Snap(segment, mask);
        }

        Join();
        _nextSnapRetry = Time.unscaledTime + SnapRetrySeconds;
    }

    /// <summary>Retry rock that wasn't there yet and redraw for the camera; call once a frame, after the camera has moved.</summary>
    public void Update(Camera camera)
    {
        if (!_object.activeInHierarchy || camera == null)
        {
            return;
        }

        if (Time.unscaledTime >= _nextSnapRetry)
        {
            _nextSnapRetry = Time.unscaledTime + SnapRetrySeconds;
            RetrySnap();
        }

        if (_topologyDirty)
        {
            BuildTopology();
        }

        var transform = camera.transform;
        var view = (transform.position, transform.rotation, camera.fieldOfView, Screen.width, Screen.height);
        if (_positionsDirty || view != _lastView)
        {
            _lastView = view;
            _positionsDirty = false;
            UpdateVertices(camera, view.position, view.rotation);
        }
    }

    /// <summary>The control point drawn nearest a screen position (within a few pixels of its dot), or -1.</summary>
    public int PickPoint(Camera camera, Vector2 screen)
    {
        var view = new ScreenProjection(camera);
        var target = Numerics(screen);
        var best = -1;
        var bestDistance = view.Pixels(PickPointPixels);
        for (var i = 0; i < _points.Count; i++)
        {
            if (view.TryProject(_points[i].Position, out var p) && (p - target).Length() < bestDistance)
            {
                best = i;
                bestDistance = (p - target).Length();
            }
        }

        return best;
    }

    /// <summary>The segment drawn under a screen position (within a few pixels of the line), or -1.</summary>
    public int PickSegment(Camera camera, Vector2 screen)
    {
        var view = new ScreenProjection(camera);
        var target = Numerics(screen);
        var best = -1;
        var bestDistance = view.Pixels(LinePixels * 0.5f + PickLinePixels);
        for (var i = 1; i < RibbonSamples; i++)
        {
            if (!view.TryProject(_positions[i - 1], out var a) || !view.TryProject(_positions[i], out var b))
            {
                continue;
            }

            var ab = b - a;
            var t = ab.LengthSquared() > 0f ? Math.Clamp(NVector2.Dot(target - a, ab) / ab.LengthSquared(), 0f, 1f) : 0f;
            var distance = (a + ab * t - target).Length();
            if (distance < bestDistance)
            {
                best = _sampleSegments[i];
                bestDistance = distance;
            }
        }

        return best;
    }

    private static NVector2 Numerics(Vector2 v) => new(v.x, v.y);

    /// <summary>World to screen pixels, worked out here rather than asking the camera once per point.</summary>
    private readonly struct ScreenProjection
    {
        private readonly NVector3 _eye;
        private readonly NVector3 _right;
        private readonly NVector3 _up;
        private readonly NVector3 _forward;
        private readonly float _focal;
        private readonly float _near;
        private readonly float _width;
        private readonly float _height;

        public ScreenProjection(Camera camera)
        {
            var transform = camera.transform;
            var rotation = transform.rotation;
            _eye = RouteOverlay.ToNumerics(transform.position);
            _right = RouteOverlay.ToNumerics(rotation * Vector3.right);
            _up = RouteOverlay.ToNumerics(rotation * Vector3.up);
            _forward = RouteOverlay.ToNumerics(rotation * Vector3.forward);
            _width = Screen.width;
            _height = Screen.height;
            _focal = _height * 0.5f / MathF.Tan(camera.fieldOfView * 0.5f * MathF.PI / 180f);
            _near = camera.nearClipPlane;
        }

        /// <summary>A size in reference pixels (see <see cref="ReferenceScreenHeight"/>) in this screen's pixels.</summary>
        public float Pixels(float reference) => reference * _height / ReferenceScreenHeight;

        public bool TryProject(NVector3 point, out NVector2 screen)
        {
            var offset = point - _eye;
            var depth = NVector3.Dot(offset, _forward);
            if (depth < _near)
            {
                screen = default;
                return false;
            }

            screen = new NVector2(
                _width * 0.5f + NVector3.Dot(offset, _right) * _focal / depth,
                _height * 0.5f + NVector3.Dot(offset, _up) * _focal / depth);
            return true;
        }
    }

    /// <summary>Sample spacing, widened for very long lines.</summary>
    private float Spacing()
    {
        var length = 0f;
        for (var i = 1; i < _points.Count; i++)
        {
            length += NVector3.Distance(_points[i - 1].Position, _points[i].Position);
        }

        // The curve is a bit longer than the straight pieces; leave room for that.
        return MathF.Max(SampleSpacing, length * 1.5f / MaxSamples);
    }

    /// <summary>Move each sample onto the rock along its normal, keeping the curve where no rock is found.</summary>
    private static void Snap(Segment segment, int mask)
    {
        segment.Snapped.Clear();
        segment.Misses = 0;
        foreach (var sample in segment.Curve)
        {
            if (TrySnap(sample, mask, out var snapped))
            {
                segment.Snapped.Add(snapped);
            }
            else
            {
                segment.Snapped.Add(sample);
                segment.Misses++;
            }
        }
    }

    private static bool TrySnap(CurveSample sample, int mask, out CurveSample snapped)
    {
        var origin = sample.Position + sample.Normal * SnapReach;
        if (Physics.Raycast(RouteOverlay.ToUnity(origin), RouteOverlay.ToUnity(-sample.Normal), out var hit, 2f * SnapReach, mask, QueryTriggerInteraction.Ignore))
        {
            var normal = RouteOverlay.ToNumerics(hit.normal);
            // Hitting the back of something, or a face at right angles, would throw the line off the wall.
            if (NVector3.Dot(normal, sample.Normal) > 0.2f)
            {
                snapped = new CurveSample(RouteOverlay.ToNumerics(hit.point), normal);
                return true;
            }
        }

        snapped = sample;
        return false;
    }

    private void RetrySnap()
    {
        var changed = false;
        var mask = 0;
        foreach (var segment in _segments)
        {
            if (segment.Misses == 0)
            {
                continue;
            }

            if (mask == 0)
            {
                mask = GameApi.SurfaceMask;
            }

            for (var i = 0; i < segment.Curve.Count; i++)
            {
                if (segment.Snapped[i] == segment.Curve[i] && TrySnap(segment.Curve[i], mask, out var snapped))
                {
                    segment.Snapped[i] = snapped;
                    segment.Misses--;
                    changed = true;
                }
            }
        }

        if (changed)
        {
            Join();
        }
    }

    /// <summary>Chain the segments into one polyline, soften the steps snapping left, and lift it off the rock.</summary>
    private void Join()
    {
        var count = 0;
        foreach (var segment in _segments)
        {
            count += segment.Snapped.Count - 1;
        }

        count = _segments.Count == 0 ? 0 : count + 1;
        if (_positions.Length != count)
        {
            _positions = new NVector3[count];
            _scratch = new NVector3[count];
            _sampleSegments = new int[count];
            _topologyDirty = true;
        }

        var normals = new NVector3[count];
        var n = 0;
        for (var s = 0; s < _segments.Count; s++)
        {
            var samples = _segments[s].Snapped;
            for (var i = s == 0 ? 0 : 1; i < samples.Count; i++)
            {
                _positions[n] = samples[i].Position;
                normals[n] = samples[i].Normal;
                _sampleSegments[n] = s;
                n++;
            }
        }

        // Two passes of a 1-2-1 blur; the ends stay put.
        for (var pass = 0; pass < 2; pass++)
        {
            Array.Copy(_positions, _scratch, count);
            for (var i = 1; i < count - 1; i++)
            {
                _positions[i] = (_scratch[i - 1] + 2f * _scratch[i] + _scratch[i + 1]) * 0.25f;
            }
        }

        for (var i = 0; i < count; i++)
        {
            _positions[i] += normals[i] * SurfaceOffset;
        }

        RebuildDots();
    }

    private void RebuildDots()
    {
        _dots.Clear();
        for (var i = 0; i < _points.Count; i++)
        {
            var end = i == 0 || i == _points.Count - 1;
            if (!end && !_showPoints)
            {
                continue;
            }

            var normal = _points[i].Normal;
            var position = _points[i].Position + (normal.LengthSquared() > 1e-8f ? NVector3.Normalize(normal) * SurfaceOffset : NVector3.Zero);
            _dots.Add(end ? new Dot(position, EndDotPixels, EndRingPixels, i) : new Dot(position, PointDotPixels, PointRingPixels, i));
        }

        _topologyDirty = true;
    }

    private int RibbonSamples => _positions.Length >= 2 ? _positions.Length : 0;

    /// <summary>Vertex count, triangles and colours: these only change with the points, colour or dots shown.</summary>
    private void BuildTopology()
    {
        _topologyDirty = false;
        _positionsDirty = true;
        var ribbon = RibbonSamples;
        var discVertices = DiscSegments + 1;
        var vertexCount = 4 * ribbon + 3 * discVertices * _dots.Count;
        var triangles = new int[(ribbon > 0 ? 12 * (ribbon - 1) : 0) + 9 * DiscSegments * _dots.Count];
        var colors = new Color32[vertexCount];
        var t = 0;
        var v = 0;
        var line = new Color32(_color.r, _color.g, _color.b, 255);
        foreach (var color in new[] { Border, line })
        {
            if (ribbon == 0)
            {
                break;
            }

            for (var i = 0; i < ribbon; i++)
            {
                colors[v + 2 * i] = color;
                colors[v + 2 * i + 1] = color;
                if (i > 0)
                {
                    var a = v + 2 * (i - 1);
                    var b = v + 2 * i;
                    triangles[t++] = a;
                    triangles[t++] = b;
                    triangles[t++] = a + 1;
                    triangles[t++] = a + 1;
                    triangles[t++] = b;
                    triangles[t++] = b + 1;
                }
            }

            v += 2 * ribbon;
        }

        for (var d = 0; d < _dots.Count; d++)
        {
            foreach (var color in new[] { Border, Ring, line })
            {
                for (var i = 0; i < discVertices; i++)
                {
                    colors[v + i] = color;
                }

                for (var i = 0; i < DiscSegments; i++)
                {
                    triangles[t++] = v;
                    triangles[t++] = v + 1 + i;
                    triangles[t++] = v + 1 + (i + 1) % DiscSegments;
                }

                v += discVertices;
            }
        }

        _mesh.Clear();
        _vertices = new Il2CppStructArray<Vector3>(vertexCount);
        _built = new NVector3[vertexCount];
        _mesh.vertices = _vertices;
        _mesh.colors32 = colors;
        _mesh.triangles = triangles;
    }

    /// <summary>Lay the ribbon and dots out facing the camera, a fixed number of pixels wide.</summary>
    private void UpdateVertices(Camera camera, Vector3 cameraPosition, Quaternion cameraRotation)
    {
        var eye = RouteOverlay.ToNumerics(cameraPosition);
        var forward = RouteOverlay.ToNumerics(cameraRotation * Vector3.forward);
        var right = RouteOverlay.ToNumerics(cameraRotation * Vector3.right);
        var up = RouteOverlay.ToNumerics(cameraRotation * Vector3.up);
        var near = camera.nearClipPlane;
        var pixelsPerReference = Screen.height / ReferenceScreenHeight;
        var metresPerPixelPerDepth = 2f * MathF.Tan(camera.fieldOfView * 0.5f * MathF.PI / 180f) / Math.Max(Screen.height, 1);

        var ribbon = RibbonSamples;
        var v = 0;
        var side = right;
        for (var i = 0; i < ribbon; i++)
        {
            var (position, depth) = TowardCamera(_positions[i], eye, forward, near);
            var unit = MathF.Max(depth * metresPerPixelPerDepth * pixelsPerReference, MinWorldPerPixel);
            var tangent = _positions[Math.Min(i + 1, ribbon - 1)] - _positions[Math.Max(i - 1, 0)];
            var across = NVector3.Cross(tangent, position - eye);
            if (across.LengthSquared() > 1e-10f)
            {
                side = NVector3.Normalize(across);
            }

            var line = side * (LinePixels * 0.5f * unit);
            var border = side * ((LinePixels * 0.5f + BorderPixels) * unit);
            _built[v + 2 * i] = position - border;
            _built[v + 2 * i + 1] = position + border;
            _built[v + 2 * ribbon + 2 * i] = position - line;
            _built[v + 2 * ribbon + 2 * i + 1] = position + line;
        }

        v += 4 * ribbon;
        foreach (var dot in _dots)
        {
            var (center, depth) = TowardCamera(dot.Position, eye, forward, near);
            var scale = dot.Point == _selected ? SelectedScale : dot.Point == _hovered ? HoveredScale : 1f;
            var unit = MathF.Max(depth * metresPerPixelPerDepth * pixelsPerReference, MinWorldPerPixel) * scale;
            for (var layer = 0; layer < 3; layer++)
            {
                // Border, white ring, coloured fill.
                var radius = dot.Fill + (layer < 2 ? dot.Ring : 0f) + (layer == 0 ? BorderPixels : 0f);
                _built[v] = center;
                for (var i = 0; i < DiscSegments; i++)
                {
                    var angle = 2f * MathF.PI * i / DiscSegments;
                    _built[v + 1 + i] = center + (right * MathF.Cos(angle) + up * MathF.Sin(angle)) * (radius * unit);
                }

                v += DiscSegments + 1;
            }
        }

        var min = new NVector3(float.MaxValue);
        var max = new NVector3(float.MinValue);
        for (var i = 0; i < _built.Length; i++)
        {
            var p = _built[i];
            min = NVector3.Min(min, p);
            max = NVector3.Max(max, p);
            _vertices[i] = ToUnityFast(p);
        }

        _mesh.vertices = _vertices;
        if (_built.Length > 0)
        {
            _mesh.bounds = new Bounds(RouteOverlay.ToUnity((min + max) * 0.5f), RouteOverlay.ToUnity(max - min));
        }
    }

    /// <summary>
    /// Slide a point toward the camera along its view ray: it stays on the same pixel but ends up in
    /// front of nearby rock. Returns the new point and its depth in front of the camera.
    /// </summary>
    private static (NVector3 Position, float Depth) TowardCamera(NVector3 point, NVector3 eye, NVector3 forward, float near)
    {
        var offset = point - eye;
        var distance = offset.Length();
        if (distance < 1e-4f)
        {
            return (point, near);
        }

        var slid = MathF.Max(distance - DepthBias - DepthBiasPerMetre * distance, MathF.Min(distance, near * 2f));
        var position = eye + offset * (slid / distance);
        return (position, MathF.Max(NVector3.Dot(position - eye, forward), near));
    }

    /// <summary>Fills the fields directly: a Unity Vector3 constructor would be a call into the game per vertex.</summary>
    private static Vector3 ToUnityFast(NVector3 v)
    {
        var u = default(Vector3);
        u.x = v.X;
        u.y = v.Y;
        u.z = v.Z;
        return u;
    }

    private static Material CreateMaterial()
    {
        // The editor-handles shader takes depth test, culling and blending as properties and multiplies
        // in vertex colours; it ships with every build. Sprites/Default (the overlay's usual pick) also
        // has vertex colours, so it's the fallback.
        var shader = Shader.Find("Hidden/Internal-Colored");
        if (shader == null)
        {
            shader = RouteOverlay.FindShader();
        }

        MelonLogger.Msg($"Route line shader: {(shader != null ? shader.name : "none")}");
        var material = RuntimeAssets.Keep(new Material(shader) { name = "RouteLine", color = Color.white, renderQueue = 3100 });
        material.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
        material.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
        material.SetInt("_Cull", (int)CullMode.Off);
        material.SetInt("_ZWrite", 0);
        material.SetInt("_ZTest", (int)CompareFunction.LessEqual);
        return material;
    }
}
