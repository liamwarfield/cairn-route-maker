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
/// Space is cut into axis-aligned cubes. The first time paint reaches a cube, rays are cast
/// through it to find which ways its rock faces, and it gets a decal per facing direction
/// (+X, -X, +Y, ...), each projecting along its own axis with a texture generated here. The decals use the shader's normals fade, so each one only colours
/// rock that faces its way: paint wraps over ridges and around corners without smearing, and
/// since the cubes tile without overlapping, overlapping strokes don't build up. Each texel is
/// coloured by the same last-stroke-wins rule as <see cref="Route.IsAllowed"/>.
///
/// Texture work is queued and done a few milliseconds per frame, so loading a big route never
/// stalls the game. The texel loops use System.Numerics and MathF only: under IL2CPP every
/// UnityEngine.Vector3 or Mathf call crosses into native code, which made redraws take seconds.
/// </summary>
internal sealed class DecalPaint
{
    private const float CellSize = 2f;
    private const int Resolution = 128;
    private const float TexelSize = CellSize / Resolution;
    private const float PaintAlpha = 0.6f;

    /// <summary>
    /// The decal shader draws nothing where dot(surface normal, decal axis) is below the threshold
    /// and fades in over the next smoothness (a smoothstep). Every surface is within 54.7 degrees
    /// (dot 0.577) of some axis, so 0.5 leaves no gaps; facets near 45 degrees get a little overlap.
    /// </summary>
    private const float NormalsFadeThreshold = 0.5f;
    private const float NormalsFadeSmoothness = 0.12f;

    /// <summary>Rays per side (squared) when surveying which ways a cube's rock faces.</summary>
    private const int SurveyGrid = 4;

    /// <summary>Texture work allowed per frame before the rest waits for the next one.</summary>
    private const double FlushBudgetMs = 6.0;

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
        public bool Queued;
    }

    private readonly Transform _parent;
    private readonly Dictionary<(Vector3Int Cube, int Bucket), Cell> _cells = new();

    /// <summary>
    /// For each surveyed cube, a bit per entry of <see cref="Axes"/> that some rock in it faces.
    /// 0 means no climbable surface: paint there would never be seen.
    /// </summary>
    private readonly Dictionary<Vector3Int, int> _cubeFacings = new();
    private readonly Queue<Cell> _pending = new();
    private readonly System.Diagnostics.Stopwatch _drawTimer = new();
    private readonly System.Diagnostics.Stopwatch _frameTimer = new();
    private double _drawWorkMs;
    private bool _reportWhenDrawn;

    /// <summary>Stamps waiting to be added, in paint order; see <see cref="AddLater"/>.</summary>
    private readonly Queue<(int StrokeIndex, StrokeMode Mode, Stamp Stamp)> _pendingStamps = new();
    private Material _baseMaterial;
    private TGBDecal _template;
    private bool _warned;

    public DecalPaint(Transform parent) => _parent = parent;

    public int CellCount => _cells.Count;

    /// <summary>Called once queued drawing finishes: cell count, total work ms, wall-clock ms.</summary>
    public Action<int, double, double> DrawingFinished { get; set; }

    public void Clear()
    {
        foreach (var cell in _cells.Values)
        {
            Destroy(cell);
        }

        _cells.Clear();
        _cubeFacings.Clear();
        _pending.Clear();
        _pendingStamps.Clear();
        _drawTimer.Reset();
        _drawWorkMs = 0;
    }

    /// <summary>
    /// Queue a stamp to be added during <see cref="Flush"/>, for bulk loads: surveying new cubes
    /// casts rays, which is too slow to do for a whole route in one frame.
    /// </summary>
    public void AddLater(int strokeIndex, StrokeMode mode, Stamp stamp)
    {
        _pendingStamps.Enqueue((strokeIndex, mode, stamp));
        StartDrawTimer();
    }

    /// <summary>Log how long drawing took once everything queued so far is done.</summary>
    public void ReportWhenDrawn() => _reportWhenDrawn = _pendingStamps.Count > 0 || _pending.Count > 0;

    public void Add(int strokeIndex, StrokeMode mode, Stamp stamp)
    {
        if (_pendingStamps.Count > 0)
        {
            // Keep paint order: later strokes must land after earlier ones still waiting.
            AddLater(strokeIndex, mode, stamp);
            return;
        }

        AddNow(strokeIndex, mode, stamp);
    }

    private void AddNow(int strokeIndex, StrokeMode mode, Stamp stamp)
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
            if (!SphereTouchesCube(p, r, center))
            {
                continue;
            }

            // Erase reaches every decal in the cube; paint goes where the survey found rock, plus
            // the dab's own surface (the survey can miss thin features).
            var facings = mode == StrokeMode.Erase ? (1 << Axes.Length) - 1 : Facings(key, center) | (1 << Dominant(stamp.Normal));

            for (var bucket = 0; bucket < Axes.Length; bucket++)
            {
                if ((facings & (1 << bucket)) == 0)
                {
                    continue;
                }

                // Erase only touches existing paint; paint creates the cube's decal on first use.
                if (!_cells.TryGetValue((key, bucket), out var cell))
                {
                    if (mode == StrokeMode.Erase)
                    {
                        continue;
                    }

                    var (_, right, up) = Orientation(Axes[bucket]);
                    cell = new Cell { Key = key, Bucket = bucket, Center = center, Axis = Axes[bucket], Right = right, Up = up, NeedsRedraw = true };
                    _cells.Add((key, bucket), cell);
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

                Enqueue(cell);
            }
        }
    }

    /// <summary>
    /// Which ways the rock inside a cube faces, as a bit mask over <see cref="Axes"/>, found once by
    /// casting a grid of rays into the cube from each side. A direction counts if any surface would
    /// get colour from that direction's decal at all (dot at or above the fade threshold).
    /// </summary>
    private int Facings(Vector3Int key, NVector3 center)
    {
        if (_cubeFacings.TryGetValue(key, out var known))
        {
            return known;
        }

        var mask = 0;
        var half = CellSize * 0.5f;
        var unityCenter = RouteOverlay.ToUnity(center);
        if (Physics.CheckBox(unityCenter, Vector3.one * half, Quaternion.identity, GameApi.SurfaceMask, QueryTriggerInteraction.Ignore))
        {
            foreach (var axis in Axes)
            {
                // Two directions spanning the face the rays start from.
                var a = MathF.Abs(axis.X) > 0.5f ? NVector3.UnitY : NVector3.UnitX;
                var b = NVector3.Cross(axis, a);
                var direction = RouteOverlay.ToUnity(-axis);
                for (var i = 0; i < SurveyGrid; i++)
                for (var j = 0; j < SurveyGrid; j++)
                {
                    var u = ((i + 0.5f) / SurveyGrid - 0.5f) * CellSize;
                    var v = ((j + 0.5f) / SurveyGrid - 0.5f) * CellSize;
                    var origin = center + axis * (half + 0.01f) + a * u + b * v;
                    if (!Physics.Raycast(RouteOverlay.ToUnity(origin), direction, out var hit, CellSize + 0.02f, GameApi.SurfaceMask, QueryTriggerInteraction.Ignore))
                    {
                        continue;
                    }

                    var normal = RouteOverlay.ToNumerics(hit.normal);
                    for (var bucket = 0; bucket < Axes.Length; bucket++)
                    {
                        if (NVector3.Dot(normal, Axes[bucket]) >= NormalsFadeThreshold)
                        {
                            mask |= 1 << bucket;
                        }
                    }
                }
            }
        }

        _cubeFacings[key] = mask;
        return mask;
    }

    private void Enqueue(Cell cell)
    {
        if (cell.Queued)
        {
            return;
        }

        cell.Queued = true;
        _pending.Enqueue(cell);
        StartDrawTimer();
    }

    private void StartDrawTimer()
    {
        if (!_drawTimer.IsRunning)
        {
            _drawTimer.Restart();
            _drawWorkMs = 0;
        }
    }

    /// <summary>
    /// Do queued texture work for up to a few milliseconds; call every frame. Anything left over
    /// is picked up next frame, so big routes fill in over a moment instead of freezing the game.
    /// </summary>
    public void Flush()
    {
        if (_pending.Count == 0 && _pendingStamps.Count == 0)
        {
            return;
        }

        _frameTimer.Restart();
        while (_pendingStamps.Count > 0 && _frameTimer.Elapsed.TotalMilliseconds < FlushBudgetMs)
        {
            var (strokeIndex, mode, stamp) = _pendingStamps.Dequeue();
            AddNow(strokeIndex, mode, stamp);
        }

        // Textures are only drawn once every stamp is in, so each cell is drawn once, not per stamp.
        while (_pendingStamps.Count == 0 && _pending.Count > 0 && _frameTimer.Elapsed.TotalMilliseconds < FlushBudgetMs)
        {
            var cell = _pending.Dequeue();
            cell.Queued = false;
            if (!_cells.ContainsKey((cell.Key, cell.Bucket)))
            {
                // Removed (undone) while waiting.
                continue;
            }

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

        _drawWorkMs += _frameTimer.Elapsed.TotalMilliseconds;
        if (_pending.Count == 0 && _pendingStamps.Count == 0 && _drawTimer.IsRunning)
        {
            _drawTimer.Stop();
            if (_reportWhenDrawn)
            {
                _reportWhenDrawn = false;
                DrawingFinished?.Invoke(_cells.Count, _drawWorkMs, _drawTimer.Elapsed.TotalMilliseconds);
            }
        }
    }

    public void RemoveStroke(int strokeIndex)
    {
        if (_pendingStamps.Count > 0)
        {
            var keep = new List<(int, StrokeMode, Stamp)>(_pendingStamps);
            _pendingStamps.Clear();
            foreach (var pending in keep)
            {
                if (pending.Item1 != strokeIndex)
                {
                    _pendingStamps.Enqueue(pending);
                }
            }
        }

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
            Enqueue(cell);
        }

        foreach (var key in emptied)
        {
            Destroy(_cells[key]);
            _cells.Remove(key);
        }
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

        var texture = Keep(new Texture2D(Resolution, Resolution, TextureFormat.RGBA32, false)
        {
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear,
            name = name,
        });
        var material = Keep(new Material(baseMaterial) { name = name, mainTexture = texture });

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
            if (decal.layer == TGBDecal.Layer.Enviro0 && material != null && material.shader.name == "TGB/Decal"
                && !decal.name.StartsWith("RoutePaint", StringComparison.Ordinal))
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
        Keep(_baseMaterial);
        MelonLogger.Msg($"Route paint uses TGB/Decal ({(_template != null ? $"settings from '{_template.name}'" : "default settings")}).");
        return _baseMaterial;
    }

    /// <summary>
    /// Protect a runtime asset from Resources.UnloadUnusedAssets, which the game runs on zone
    /// changes: anything only referenced from mod code counts as unused and gets destroyed
    /// (DontDestroyOnLoad only covers GameObjects).
    /// </summary>
    private static T Keep<T>(T asset)
        where T : Object
    {
        asset.hideFlags |= HideFlags.DontUnloadUnusedAsset;
        return asset;
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
