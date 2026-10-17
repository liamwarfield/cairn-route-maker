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
/// Space is cut into axis-aligned cubes. Each cube that paint touches gets a decal per facing
/// direction (+X, -X, +Y, ...) that paint there faces, each projecting along its own axis with a
/// texture generated here. The decals use the shader's normals fade, so each one only colours
/// rock that faces its way: paint wraps over ridges and around corners without smearing, and
/// since the cubes tile without overlapping, overlapping strokes don't build up. Each texel is
/// coloured by the same last-stroke-wins rule as <see cref="Route.IsAllowed"/>.
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

    /// <summary>
    /// The decal shader draws nothing where dot(surface normal, decal axis) is below the threshold
    /// and fades in over the next smoothness (a smoothstep). Every surface is within 54.7 degrees
    /// (dot 0.577) of some axis, so 0.5 leaves no gaps; facets near 45 degrees get a little overlap.
    /// </summary>
    private const float NormalsFadeThreshold = 0.5f;
    private const float NormalsFadeSmoothness = 0.12f;

    /// <summary>A dab is drawn by every direction's decal its normal is within about 70 degrees of.</summary>
    private const float BucketMinDot = 0.35f;

    private const int CursorResolution = 256;

    private static readonly NVector3[] Axes =
    {
        NVector3.UnitX, -NVector3.UnitX, NVector3.UnitY, -NVector3.UnitY, NVector3.UnitZ, -NVector3.UnitZ,
    };

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
        public int Bucket;
        public NVector3 Center;
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
    private readonly Dictionary<(Vector3Int Cube, int Bucket), Cell> _cells = new();

    /// <summary>Cubes with no climbable surface in them: paint there would never be seen.</summary>
    private readonly HashSet<Vector3Int> _empty = new();
    private Material _baseMaterial;
    private GameObject _cursor;
    private TGBDecal _cursorDecal;
    private Material[] _cursorMaterials;
    private StrokeMode _cursorMode = (StrokeMode)(-1);
    private TGBDecal _template;
    private bool _warned;

    public DecalPaint(Transform parent) => _parent = parent;

    public int CellCount => _cells.Count;

    /// <summary>
    /// Show the brush as a ring projected onto the surface, drawn by the same decal shader as the
    /// paint so its colour matches what it will paint (a lit, colour-graded preview). Erase is black.
    /// </summary>
    public void ShowCursor(Vector3 point, Vector3 normal, float radius, StrokeMode mode)
    {
        if (_cursor == null && !CreateCursor())
        {
            return;
        }

        if (mode != _cursorMode)
        {
            _cursorDecal.m_Material = _cursorMaterials[(int)mode];
            _cursorMode = mode;
        }

        var (rotation, _, _) = Orientation(RouteOverlay.ToNumerics(normal));
        _cursor.transform.SetPositionAndRotation(point, rotation);
        // Deep enough to reach into bumps and hollows, but not through to the far side of a ridge.
        _cursor.transform.localScale = new Vector3(radius * 2f, Mathf.Max(0.3f, radius), radius * 2f);
        _cursor.SetActive(true);
    }

    public void HideCursor()
    {
        if (_cursor != null)
        {
            _cursor.SetActive(false);
        }
    }

    private bool CreateCursor()
    {
        var created = CreateDecal("RoutePaint brush", 1f);
        if (created == null)
        {
            return false;
        }

        var (go, material, texture) = created.Value;
        Object.Destroy(texture);
        Object.Destroy(material);
        _cursorMaterials = new Material[3];
        foreach (var mode in new[] { StrokeMode.Allow, StrokeMode.Deny, StrokeMode.Erase })
        {
            var ring = new Texture2D(CursorResolution, CursorResolution, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, name = $"RoutePaint brush {mode}" };
            ring.SetPixels32(CursorPixels(mode));
            ring.Apply(false);
            _cursorMaterials[(int)mode] = new Material(_baseMaterial) { name = ring.name, mainTexture = ring };
        }

        _cursor = go;
        _cursorDecal = go.GetComponent<TGBDecal>();
        return true;
    }

    /// <summary>
    /// A ring at the brush edge with a light fill, in the brush's paint colour (black for erase).
    /// Edges are anti-aliased so the ring stays smooth when the brush is large.
    /// </summary>
    private static Color32[] CursorPixels(StrokeMode mode)
    {
        const float ringInner = 0.88f;
        const float pixel = 2f / CursorResolution;
        var color = mode switch { StrokeMode.Allow => AllowColor, StrokeMode.Deny => DenyColor, _ => new Color32(0, 0, 0, 255) };
        var fill = mode == StrokeMode.Erase ? 0.3f : 0.16f;
        var pixels = new Color32[CursorResolution * CursorResolution];
        for (var j = 0; j < CursorResolution; j++)
        for (var i = 0; i < CursorResolution; i++)
        {
            var dx = (i + 0.5f) / CursorResolution * 2f - 1f;
            var dy = (j + 0.5f) / CursorResolution * 2f - 1f;
            var r = MathF.Sqrt(dx * dx + dy * dy);
            var outside = Math.Clamp((r - 1f) / pixel + 0.5f, 0f, 1f);
            var inRing = Math.Clamp((r - ringInner) / pixel + 0.5f, 0f, 1f);
            var alpha = (1f - outside) * (fill + (0.92f - fill) * inRing);
            pixels[j * CursorResolution + i] = new Color32(color.r, color.g, color.b, (byte)(alpha * 255f));
        }

        return pixels;
    }

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

            for (var bucket = 0; bucket < Axes.Length; bucket++)
            {
                Cell cell;
                if (mode == StrokeMode.Erase)
                {
                    // Erase clears whatever is painted in reach, whichever way it faces.
                    if (!_cells.TryGetValue((key, bucket), out cell))
                    {
                        continue;
                    }
                }
                else
                {
                    if (NVector3.Dot(stamp.Normal, Axes[bucket]) < BucketMinDot && Dominant(stamp.Normal) != bucket)
                    {
                        continue;
                    }

                    if (!TryGetOrAddCell(key, bucket, center, out cell))
                    {
                        break;
                    }
                }

                cell.Entries.Add(entry);
                if (cell.GameObject != null && !cell.NeedsRedraw)
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
    }

    private bool TryGetOrAddCell(Vector3Int key, int bucket, NVector3 center, out Cell cell)
    {
        if (_cells.TryGetValue((key, bucket), out cell))
        {
            return true;
        }

        // Physics check once per cube: no climbable surface inside means nothing would ever show.
        var cubeKnown = false;
        for (var b = 0; b < Axes.Length && !cubeKnown; b++)
        {
            cubeKnown = _cells.ContainsKey((key, b));
        }

        if (!cubeKnown && !Physics.CheckBox(RouteOverlay.ToUnity(center), Vector3.one * (CellSize * 0.5f), Quaternion.identity, GameApi.SurfaceMask, QueryTriggerInteraction.Ignore))
        {
            _empty.Add(key);
            return false;
        }

        var (_, right, up) = Orientation(Axes[bucket]);
        cell = new Cell { Key = key, Bucket = bucket, Center = center, Axis = Axes[bucket], Right = right, Up = up, NeedsRedraw = true };
        _cells.Add((key, bucket), cell);
        return true;
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
        var emptied = new List<(Vector3Int, int)>();
        foreach (var cell in _cells.Values)
        {
            if (cell.Entries.RemoveAll(e => e.StrokeIndex == strokeIndex) == 0)
            {
                continue;
            }

            if (cell.Entries.Count == 0)
            {
                emptied.Add((cell.Key, cell.Bucket));
                continue;
            }

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

        var (rotation, _, _) = Orientation(cell.Axis);
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
    /// so overlapping dabs of one stroke don't leave faint rings. Erase stamps fade out whatever
    /// is there instead.
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
        var erase = entry.Mode == StrokeMode.Erase;
        var mode = (byte)(1 + (int)entry.Mode);
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
            if (erase)
            {
                if (cell.Modes[texel] == 0)
                {
                    continue;
                }

                var remaining = cell.Coverage[texel] * (1f - coverage);
                if (remaining < 0.02f)
                {
                    cell.Modes[texel] = 0;
                    cell.Coverage[texel] = 0f;
                    cell.Pixels[texel * 4 + 3] = 0;
                    continue;
                }

                coverage = remaining;
            }
            else if (cell.Modes[texel] == mode)
            {
                coverage = Math.Max(coverage, cell.Coverage[texel]);
            }

            var texelMode = erase ? cell.Modes[texel] : mode;
            cell.Modes[texel] = texelMode;
            cell.Coverage[texel] = coverage;
            var color = texelMode == 1 + (int)StrokeMode.Allow ? AllowColor : DenyColor;
            var index = texel * 4;
            cell.Pixels[index] = color.r;
            cell.Pixels[index + 1] = color.g;
            cell.Pixels[index + 2] = color.b;
            cell.Pixels[index + 3] = (byte)(PaintAlpha * coverage * SprayGrain(cell.Key, cell.Bucket, i, j) * 255f);
        }
    }

    /// <summary>Fixed per-texel speckle so the paint reads as sprayed rather than flat.</summary>
    private static float SprayGrain(Vector3Int key, int bucket, int i, int j)
    {
        var h = (uint)(key.x * 73856093 ^ key.y * 19349663 ^ key.z * 83492791 ^ bucket * 1299709 ^ i * 2654435761 ^ j * 40503);
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

    /// <summary>Index into <see cref="Axes"/> of the direction a normal faces most.</summary>
    private static int Dominant(NVector3 n)
    {
        var best = 0;
        for (var b = 1; b < Axes.Length; b++)
        {
            if (NVector3.Dot(n, Axes[b]) > NVector3.Dot(n, Axes[best]))
            {
                best = b;
            }
        }

        return best;
    }

    /// <summary>
    /// Decal box rotation for a surface facing <paramref name="normal"/>, plus the world
    /// directions of texture u and v. The shader projects along the box's local Y, takes
    /// uv from local X and Z, and its normals fade compares the surface with the box's -Y, so
    /// +Y must point into the rock. u and v are read back from the rotation so they always
    /// match what the shader samples.
    /// </summary>
    private static (Quaternion Rotation, NVector3 Right, NVector3 Up) Orientation(NVector3 normal)
    {
        var reference = MathF.Abs(normal.Y) > 0.9f ? NVector3.UnitZ : NVector3.UnitY;
        var along = NVector3.Normalize(reference - normal * NVector3.Dot(reference, normal));
        var rotation = Quaternion.LookRotation(RouteOverlay.ToUnity(along), RouteOverlay.ToUnity(-normal));
        return (rotation, RouteOverlay.ToNumerics(rotation * Vector3.right), RouteOverlay.ToNumerics(rotation * Vector3.forward));
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

        // Plain colour paint: no bump, distortion, emission or box/near fades that would cause seams.
        foreach (var keyword in new[] { "USEBUMP", "UVDISTORTION", "BOXFADE", "NEARFADE", "UVAUTOZOOMINOUT" })
        {
            _baseMaterial.DisableKeyword(keyword);
        }

        _baseMaterial.SetFloat("_UseBump", 0f);
        _baseMaterial.SetFloat("_UvDistortion", 0f);
        _baseMaterial.SetFloat("_BoxFade", 0f);
        // Each decal only colours rock facing its axis (see NormalsFadeThreshold).
        _baseMaterial.EnableKeyword("NORMALSFADE");
        _baseMaterial.SetFloat("_NormalsFade", 1f);
        _baseMaterial.SetFloat("_NormalsFadeThreshold", NormalsFadeThreshold);
        _baseMaterial.SetFloat("_NormalsFadeSmoothness", NormalsFadeSmoothness);
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
