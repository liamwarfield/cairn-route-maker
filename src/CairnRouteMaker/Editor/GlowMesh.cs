using System;
using System.Collections.Generic;
using CairnRouteMaker.Game;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using MelonLoader;
using UnityEngine;
using UnityEngine.Rendering;
using NVector3 = System.Numerics.Vector3;
using Object = UnityEngine.Object;

namespace CairnRouteMaker.Editor;

/// <summary>
/// An unlit mesh for the start and finish markers. Its owner rewrites the vertices and colours every
/// frame, since parts of it face the camera and it animates. Positions are relative to the mesh's
/// object, which sits on the marker's point.
///
/// Colours are premultiplied (see <see cref="Paint"/>): rgb is added over what's behind and alpha is
/// how much of that it hides. Pure glow washes out against snow and sky, so whatever has to stay
/// readable there carries some alpha too.
/// </summary>
internal sealed class GlowMesh
{
    private const int IgnoreRaycastLayer = 2;

    /// <summary>After the paint brush (3000), before the route lines (3100), which stay on top.</summary>
    private const int RenderQueue = 3050;

    /// <summary>Across a beam, -1 to 1; bunched in the middle, where its brightness falls fastest.</summary>
    private static readonly float[] BeamAcross = { -1f, -0.72f, -0.48f, -0.28f, -0.12f, 0f, 0.12f, 0.28f, 0.48f, 0.72f, 1f };

    /// <summary>Up a beam, 0 to 1; bunched at the foot, where it's brightest.</summary>
    private static readonly float[] BeamAlong = { 0f, 0.02f, 0.05f, 0.1f, 0.17f, 0.26f, 0.37f, 0.5f, 0.64f, 0.8f, 1f };

    /// <summary>How much of a beam's height its foot takes to brighten in, so it doesn't start on a hard edge.</summary>
    private const float BeamFoot = 0.04f;

    public static int BeamVertices => BeamAcross.Length * BeamAlong.Length;

    private static Material _material;

    private readonly GameObject _object;
    private readonly Mesh _mesh;
    private Il2CppStructArray<Vector3> _vertices;
    private Il2CppStructArray<Color32> _colors;
    private NVector3 _min;
    private NVector3 _max;

    public GlowMesh(Transform parent, string name)
    {
        _object = new GameObject(name) { layer = IgnoreRaycastLayer };
        _object.transform.SetParent(parent, false);
        _mesh = RuntimeAssets.Keep(new Mesh { name = name });
        _mesh.MarkDynamic();
        _object.AddComponent<MeshFilter>().sharedMesh = _mesh;
        var renderer = _object.AddComponent<MeshRenderer>();
        renderer.sharedMaterial = Material();
        renderer.shadowCastingMode = ShadowCastingMode.Off;
        renderer.receiveShadows = false;
        renderer.allowOcclusionWhenDynamic = false;
    }

    public bool Visible
    {
        get => _object.activeSelf;
        set => _object.SetActive(value);
    }

    /// <summary>Visible and so is the overlay: worth laying out this frame.</summary>
    public bool Shown => _object.activeInHierarchy;

    public Vector3 Position
    {
        set => _object.transform.position = value;
    }

    /// <summary>Fix the vertex count and triangles; the vertices are then written with <see cref="Set"/> each frame.</summary>
    public void SetTriangles(int vertexCount, List<int> triangles)
    {
        _mesh.Clear();
        _vertices = new Il2CppStructArray<Vector3>(vertexCount);
        _colors = new Il2CppStructArray<Color32>(vertexCount);
        _mesh.vertices = _vertices;
        _mesh.colors32 = _colors;
        _mesh.triangles = triangles.ToArray();
    }

    /// <summary>Triangles for a grid of vertices laid out row by row from <paramref name="first"/>.</summary>
    public static void AddGrid(List<int> triangles, int first, int columns, int rows)
    {
        for (var row = 0; row < rows - 1; row++)
        {
            for (var column = 0; column < columns - 1; column++)
            {
                var a = first + row * columns + column;
                var b = a + columns;
                triangles.Add(a);
                triangles.Add(b);
                triangles.Add(a + 1);
                triangles.Add(a + 1);
                triangles.Add(b);
                triangles.Add(b + 1);
            }
        }
    }

    public static void AddBeam(List<int> triangles, int first) => AddGrid(triangles, first, BeamAcross.Length, BeamAlong.Length);

    public void BeginFrame()
    {
        _min = new NVector3(float.MaxValue);
        _max = new NVector3(float.MinValue);
    }

    public void Set(int index, NVector3 position, Color32 color)
    {
        _min = NVector3.Min(_min, position);
        _max = NVector3.Max(_max, position);

        // Fields, not the constructor: that would be a call into the game per vertex.
        var vertex = default(Vector3);
        vertex.x = position.X;
        vertex.y = position.Y;
        vertex.z = position.Z;
        _vertices[index] = vertex;
        _colors[index] = color;
    }

    public void EndFrame()
    {
        _mesh.vertices = _vertices;
        _mesh.colors32 = _colors;
        if (_min.X <= _max.X)
        {
            _mesh.bounds = new Bounds(RouteOverlay.ToUnity((_min + _max) * 0.5f), RouteOverlay.ToUnity(_max - _min));
        }
    }

    /// <summary>
    /// A soft upright shaft of light, written from vertex <paramref name="first"/> (see
    /// <see cref="AddBeam"/>). It turns about its axis to face <paramref name="eye"/>, and is brightest
    /// down its middle and at its foot, fading out to the sides and toward the top.
    /// </summary>
    public void SetBeam(int first, NVector3 foot, float height, float halfWidth, NVector3 eye, NVector3 color, float tint, float glow)
    {
        var toEye = eye - foot;
        var side = new NVector3(toEye.Z, 0f, -toEye.X);
        side = side.LengthSquared() > 1e-6f ? NVector3.Normalize(side) : NVector3.UnitX;
        var v = first;
        foreach (var along in BeamAlong)
        {
            var fade = MathF.Min(along / BeamFoot, 1f) * (1f - along) * (1f - along);
            foreach (var across in BeamAcross)
            {
                var falloff = 1f - across * across;
                var strength = fade * falloff * falloff;
                Set(v++, foot + side * (across * halfWidth) + NVector3.UnitY * (along * height), Paint(color, tint * strength, glow * strength));
            }
        }
    }

    /// <summary>
    /// A premultiplied vertex colour: <paramref name="tint"/> of <paramref name="color"/> laid over
    /// what's behind (hiding that much of it), plus <paramref name="glow"/> of it added as light.
    /// </summary>
    public static Color32 Paint(NVector3 color, float tint, float glow)
    {
        tint = Math.Clamp(tint, 0f, 1f);
        var light = tint + MathF.Max(glow, 0f);
        var result = default(Color32);
        result.r = Byte(color.X * light);
        result.g = Byte(color.Y * light);
        result.b = Byte(color.Z * light);
        result.a = Byte(tint);
        return result;
    }

    public static float Smoothstep(float edge0, float edge1, float x)
    {
        var t = Math.Clamp((x - edge0) / (edge1 - edge0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    /// <summary>The slow breathing both markers share: 1 at its brightest, dipping by <paramref name="depth"/>.</summary>
    public static float Breath(float time, float depth) =>
        PaintColors.GlowPulse ? 1f - depth * (0.5f - 0.5f * MathF.Cos(time * (2f * MathF.PI / BreathSeconds))) : 1f;

    private const float BreathSeconds = 3f;

    private static byte Byte(float value) => (byte)(Math.Clamp(value, 0f, 1f) * 255f + 0.5f);

    private static Material Material()
    {
        if (_material != null)
        {
            return _material;
        }

        // The route line's shader (see RouteLine.CreateMaterial), blending premultiplied colours.
        var shader = Shader.Find("Hidden/Internal-Colored");
        if (shader == null)
        {
            shader = RouteOverlay.FindShader();
            MelonLogger.Warning($"Hidden/Internal-Colored not found; the start and finish markers use {(shader != null ? shader.name : "nothing")}.");
        }

        _material = RuntimeAssets.Keep(new Material(shader) { name = "RouteMarkerGlow", color = Color.white, renderQueue = RenderQueue });
        _material.SetInt("_SrcBlend", (int)BlendMode.One);
        _material.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
        _material.SetInt("_Cull", (int)CullMode.Off);
        _material.SetInt("_ZWrite", 0);
        _material.SetInt("_ZTest", (int)CompareFunction.LessEqual);
        return _material;
    }
}
