using System;
using System.Collections.Generic;
using CairnRouteMaker.Game;
using CairnRouteMaker.Routes;
using Il2Cpp;
using MelonLoader;
using UnityEngine;
using NVector3 = System.Numerics.Vector3;
using Object = UnityEngine.Object;

namespace CairnRouteMaker.Editor;

/// <summary>
/// Draws the painted route as the game's own deferred decals (TGB/Decal), so the paint is lit
/// and colour-graded like the rock it sits on.
///
/// Space is cut into axis-aligned cubes and each cube that paint touches gets one decal whose
/// texture is generated here. The cubes tile without overlapping, so every bit of rock is
/// painted exactly once (no alpha build-up where strokes overlap), and each texel is coloured by
/// the same last-stroke-wins rule as <see cref="Route.IsAllowed"/>.
///
/// The texel loops use System.Numerics and MathF only: under IL2CPP every UnityEngine.Vector3
/// or Mathf call crosses into native code, which made redraws take seconds.
/// </summary>
internal sealed class DecalPaint
{
    private const float CellSize = 1f;
    private const int Resolution = 64;
    private const float TexelSize = CellSize / Resolution;
    private const float PaintAlpha = 0.6f;

    private static readonly Color32 AllowColor = new(255, 205, 40, 255);
    private static readonly Color32 DenyColor = new(240, 45, 30, 255);

    private sealed class Entry
    {
        public int StrokeIndex;
        public StrokeMode Mode;
        public NVector3 Position;
        public float Radius;
    }

    private sealed class Cell
    {
        public Vector3Int Key;
        public NVector3 Center;
        public NVector3 NormalSum;
        public NVector3 Axis;
        public NVector3 Right;
        public NVector3 Up;
        public readonly List<Entry> Entries = new();
        public GameObject GameObject;
        public Material Material;
        public Texture2D Texture;
        public byte[] Pixels;

        /// <summary>Per texel: 0 unpainted, 1 + (int)StrokeMode otherwise.</summary>
        public byte[] Modes;
        public float[] Coverage;
        public bool NeedsRedraw;
        public bool NeedsUpload;
    }

    private readonly Transform _parent;
    private readonly Dictionary<Vector3Int, Cell> _cells = new();

    /// <summary>Cubes with no climbable surface in them: paint there would never be seen.</summary>
    private readonly HashSet<Vector3Int> _empty = new();
    private Material _baseMaterial;
    private TGBDecal _template;
    private bool _warned;

    public DecalPaint(Transform parent) => _parent = parent;

    public int CellCount => _cells.Count;

    public void Clear()
    {
        foreach (var cell in _cells.Values)
        {
            Destroy(cell);
        }

        _cells.Clear();
        _empty.Clear();
    }

    public void Add(int strokeIndex, StrokeMode mode, Stamp stamp)
    {
        var entry = new Entry { StrokeIndex = strokeIndex, Mode = mode, Position = stamp.Position, Radius = stamp.Radius };
        var r = stamp.Radius;
        var p = stamp.Position;
        int minX = Floor((p.X - r) / CellSize), maxX = Floor((p.X + r) / CellSize);
        int minY = Floor((p.Y - r) / CellSize), maxY = Floor((p.Y + r) / CellSize);
        int minZ = Floor((p.Z - r) / CellSize), maxZ = Floor((p.Z + r) / CellSize);
        for (var x = minX; x <= maxX; x++)
        for (var y = minY; y <= maxY; y++)
        for (var z = minZ; z <= maxZ; z++)
        {
            var key = new Vector3Int(x, y, z);
            var center = new NVector3(x + 0.5f, y + 0.5f, z + 0.5f) * CellSize;
            if (_empty.Contains(key) || !SphereTouchesCube(p, r, center))
            {
                continue;
            }

            if (!_cells.TryGetValue(key, out var cell))
            {
                var unityCenter = RouteOverlay.ToUnity(center);
                if (!Physics.CheckBox(unityCenter, Vector3.one * (CellSize * 0.5f), Quaternion.identity, GameApi.SurfaceMask, QueryTriggerInteraction.Ignore))
                {
                    _empty.Add(key);
                    continue;
                }

                cell = new Cell { Key = key, Center = center };
                _cells.Add(key, cell);
            }

            cell.Entries.Add(entry);
            cell.NormalSum += stamp.Normal;
            if (cell.GameObject != null && !cell.NeedsRedraw && DominantAxis(cell.NormalSum) == cell.Axis)
            {
                Rasterize(cell, entry);
                cell.NeedsUpload = true;
            }
            else
            {
                cell.NeedsRedraw = true;
            }
        }
    }

    /// <summary>Do the deferred texture work; call once per frame and after bulk changes.</summary>
    public void Flush()
    {

        foreach (var cell in _cells.Values)
        {
            if (cell.NeedsRedraw)
            {
                Redraw(cell);
            }

            if (cell.NeedsUpload && cell.Texture != null)
            {
                cell.Texture.LoadRawTextureData(cell.Pixels);
                cell.Texture.Apply(false);
                cell.NeedsUpload = false;
            }
        }
    }

    public void RemoveStroke(int strokeIndex)
    {
        var emptied = new List<Vector3Int>();
        foreach (var cell in _cells.Values)
        {
            if (cell.Entries.RemoveAll(e => e.StrokeIndex == strokeIndex) == 0)
            {
                continue;
            }

            if (cell.Entries.Count == 0)
            {
                emptied.Add(cell.Key);
                continue;
            }

            // Normals aren't kept per entry, so the dominant axis stays as it was; good enough.
            cell.NeedsRedraw = true;
        }

        foreach (var key in emptied)
        {
            Destroy(_cells[key]);
            _cells.Remove(key);
        }

        Flush();
    }

    public void RedrawAll()
    {
        foreach (var cell in _cells.Values)
        {
            cell.NeedsRedraw = true;
        }

        Flush();
    }

    private void Redraw(Cell cell)
    {
        cell.NeedsRedraw = false;
        if (cell.GameObject == null && !TryCreateDecal(cell))
        {
            return;
        }

        cell.Axis = DominantAxis(cell.NormalSum);
        var (rotation, right, up) = Orientation(cell.Axis);
        cell.Right = right;
        cell.Up = up;
        cell.GameObject.transform.SetPositionAndRotation(RouteOverlay.ToUnity(cell.Center), rotation);

        Array.Clear(cell.Modes);
        Array.Clear(cell.Coverage);
        Array.Clear(cell.Pixels);

        // Entries are kept in the order they were painted, so replaying them in order gives
        // each texel to the newest stroke covering it.
        foreach (var entry in cell.Entries)
        {
            Rasterize(cell, entry);
        }

        cell.NeedsUpload = true;
    }

    /// <summary>
    /// Paint one stamp over a cell's texture. It's newer than everything already there, so it
    /// takes every texel it covers; where the colour is unchanged the stronger coverage is kept,
    /// so overlapping dabs of one stroke don't leave faint rings.
    /// </summary>
    private static void Rasterize(Cell cell, Entry entry)
    {
        var local = entry.Position - cell.Center;
        var s = NVector3.Dot(local, cell.Right);
        var t = NVector3.Dot(local, cell.Up);
        var ci = (s + CellSize * 0.5f) / TexelSize - 0.5f;
        var cj = (t + CellSize * 0.5f) / TexelSize - 0.5f;
        var radius = entry.Radius / TexelSize;
        var i0 = Math.Max(0, (int)MathF.Floor(ci - radius));
        var i1 = Math.Min(Resolution - 1, (int)MathF.Ceiling(ci + radius));
        var j0 = Math.Max(0, (int)MathF.Floor(cj - radius));
        var j1 = Math.Min(Resolution - 1, (int)MathF.Ceiling(cj + radius));
        var mode = (byte)(1 + (int)entry.Mode);
        var color = entry.Mode == StrokeMode.Allow ? AllowColor : DenyColor;
        var rim = radius * 0.2f;
        for (var j = j0; j <= j1; j++)
        for (var i = i0; i <= i1; i++)
        {
            var dx = i - ci;
            var dy = j - cj;
            var distance = MathF.Sqrt(dx * dx + dy * dy);
            if (distance >= radius)
            {
                continue;
            }

            var coverage = Math.Clamp((radius - distance) / rim, 0f, 1f);
            var texel = j * Resolution + i;
            if (cell.Modes[texel] == mode)
            {
                coverage = Math.Max(coverage, cell.Coverage[texel]);
            }

            cell.Modes[texel] = mode;
            cell.Coverage[texel] = coverage;
            var index = texel * 4;
            cell.Pixels[index] = color.r;
            cell.Pixels[index + 1] = color.g;
            cell.Pixels[index + 2] = color.b;
            cell.Pixels[index + 3] = (byte)(PaintAlpha * coverage * SprayGrain(cell.Key, i, j) * 255f);
        }
    }

    /// <summary>Fixed per-texel speckle so the paint reads as sprayed rather than flat.</summary>
    private static float SprayGrain(Vector3Int key, int i, int j)
    {
        var h = (uint)(key.x * 73856093 ^ key.y * 19349663 ^ key.z * 83492791 ^ i * 2654435761 ^ j * 40503);
        h ^= h >> 13;
        h *= 0x5bd1e995;
        h ^= h >> 15;
        return 0.7f + 0.3f * (h & 0xffff) / 65535f;
    }

    private static int Floor(float value) => (int)MathF.Floor(value);

    private static bool SphereTouchesCube(NVector3 position, float radius, NVector3 center)
    {
        var half = new NVector3(CellSize * 0.5f);
        var closest = NVector3.Clamp(position, center - half, center + half);
        return NVector3.DistanceSquared(closest, position) <= radius * radius;
    }

    private static NVector3 DominantAxis(NVector3 n)
    {
        var ax = MathF.Abs(n.X);
        var ay = MathF.Abs(n.Y);
        var az = MathF.Abs(n.Z);
        if (ax >= ay && ax >= az)
        {
            return n.X >= 0 ? NVector3.UnitX : -NVector3.UnitX;
        }

        if (ay >= az)
        {
            return n.Y >= 0 ? NVector3.UnitY : -NVector3.UnitY;
        }

        return n.Z >= 0 ? NVector3.UnitZ : -NVector3.UnitZ;
    }

    /// <summary>
    /// Decal box rotation for a surface facing <paramref name="normal"/> (local Y = normal,
    /// local Z = "up" along the surface), plus the world directions a viewer facing the surface
    /// would call right and up.
    /// </summary>
    private static (Quaternion Rotation, NVector3 Right, NVector3 Up) Orientation(NVector3 normal)
    {
        var reference = MathF.Abs(normal.Y) > 0.9f ? NVector3.UnitZ : NVector3.UnitY;
        var up = NVector3.Normalize(reference - normal * NVector3.Dot(reference, normal));
        var right = NVector3.Cross(normal, up);
        return (Quaternion.LookRotation(RouteOverlay.ToUnity(up), RouteOverlay.ToUnity(normal)), right, up);
    }

    private bool TryCreateDecal(Cell cell)
    {
        var created = CreateDecal($"RoutePaint {cell.Key}", CellSize);
        if (created == null)
        {
            return false;
        }

        (cell.GameObject, cell.Material, cell.Texture) = created.Value;
        cell.Pixels = new byte[Resolution * Resolution * 4];
        cell.Modes = new byte[Resolution * Resolution];
        cell.Coverage = new float[Resolution * Resolution];
        cell.GameObject.SetActive(true);
        return true;
    }

    /// <summary>
    /// A TGBDecal with its own material and texture, left inactive: the decal registers itself
    /// with the game's decal system in OnEnable, so callers activate it once it's positioned.
    /// </summary>
    private (GameObject, Material, Texture2D)? CreateDecal(string name, float size)
    {
        var baseMaterial = BaseMaterial();
        if (baseMaterial == null)
        {
            if (!_warned)
            {
                _warned = true;
                MelonLogger.Error("TGB/Decal shader not found; painted areas can't be shown.");
            }

            return null;
        }

        var texture = new Texture2D(Resolution, Resolution, TextureFormat.RGBA32, false)
        {
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear,
            name = name,
        };
        var material = new Material(baseMaterial) { name = name, mainTexture = texture };

        var go = new GameObject(name);
        go.SetActive(false);
        go.transform.SetParent(_parent, false);
        go.transform.localScale = Vector3.one * size;
        var decal = go.AddComponent<TGBDecal>();
        decal.m_Material = material;
        decal.layer = TGBDecal.Layer.Enviro0;
        decal.tint = new Color(0.5f, 0.5f, 0.5f, 0.5f);
        decal.tiling = Vector2.one;
        decal.offset = Vector2.zero;
        decal.cullDistance = _template != null ? Mathf.Max(_template.cullDistance, 150f) : 150f;
        decal.lifetimeOpacityCurve = _template != null && _template.lifetimeOpacityCurve != null
            ? _template.lifetimeOpacityCurve
            : AnimationCurve.Constant(0f, 1f, 1f);
        decal.opacityMul = 1f;
        decal.fadeOutOpacityMul = 1f;
        decal.lifetimeOpacityMul = 1f;
        return (go, material, texture);
    }

    private Material BaseMaterial()
    {
        if (_baseMaterial != null)
        {
            return _baseMaterial;
        }

        // Borrow settings from one of the game's environment decals when one is loaded.
        foreach (var decal in Object.FindObjectsOfType<TGBDecal>(true))
        {
            var material = decal.m_Material;
            if (decal.layer == TGBDecal.Layer.Enviro0 && material != null && material.shader.name == "TGB/Decal")
            {
                _template = decal;
                _baseMaterial = new Material(material);
                break;
            }
        }

        if (_baseMaterial == null)
        {
            var shader = Shader.Find("TGB/Decal");
            if (shader == null)
            {
                return null;
            }

            _baseMaterial = new Material(shader);
            _baseMaterial.EnableKeyword("BETTERLIGHTING");
            _baseMaterial.SetFloat("_BetterLighting", 1f);
        }

        // Plain colour paint: no bump, distortion, emission or fades that would cause seams.
        foreach (var keyword in new[] { "USEBUMP", "UVDISTORTION", "BOXFADE", "NORMALSFADE", "NEARFADE", "UVAUTOZOOMINOUT" })
        {
            _baseMaterial.DisableKeyword(keyword);
        }

        _baseMaterial.SetFloat("_UseBump", 0f);
        _baseMaterial.SetFloat("_UvDistortion", 0f);
        _baseMaterial.SetFloat("_BoxFade", 0f);
        _baseMaterial.SetFloat("_NormalsFade", 0f);
        _baseMaterial.SetFloat("_NearFade", 0f);
        _baseMaterial.SetFloat("_MainTexAsEmission", 0f);
        _baseMaterial.SetFloat("_Opacity", 1f);
        _baseMaterial.SetColor("_Tint", new Color(0.5f, 0.5f, 0.5f, 0.5f));

        // The shader computes uv = (local.xz + 0.5) * _MainTex_ST * _TilingOffset. A decal added
        // from code keeps an empty MaterialPropertyBlock, so both come from the material: the
        // borrowed tape material mirrors and halves its texture (_MainTex_ST = -0.5, 1), which
        // with clamped sampling pins every pixel to one texel column, and _TilingOffset is unset.
        _baseMaterial.mainTextureScale = Vector2.one;
        _baseMaterial.mainTextureOffset = Vector2.zero;
        _baseMaterial.SetVector("_TilingOffset", new Vector4(1f, 1f, 0f, 0f));
        _baseMaterial.name = "RoutePaintBase";
        Object.DontDestroyOnLoad(_baseMaterial);
        MelonLogger.Msg($"Route paint uses TGB/Decal ({(_template != null ? $"settings from '{_template.name}'" : "default settings")}).");
        return _baseMaterial;
    }

    private static void Destroy(Cell cell)
    {
        if (cell.GameObject != null)
        {
            Object.Destroy(cell.GameObject);
        }

        if (cell.Material != null)
        {
            Object.Destroy(cell.Material);
        }

        if (cell.Texture != null)
        {
            Object.Destroy(cell.Texture);
        }
    }
}
