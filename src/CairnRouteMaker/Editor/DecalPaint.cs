using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
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
/// A decal colours everything facing its way along a texel's whole column, so a dab only goes
/// into a direction's decal when rock inside the dab faces that way, and each decal box is cut
/// down along its axis to the depth its dabs span. Otherwise a stroke along a wall would also
/// colour the ledges above and below it, wherever the up/down decals' columns cross them.
///
/// Every decal is a draw of its own, so a heavily painted route can slow the game down. Only cubes
/// near a focus (Ava, or the camera in the route editor) are left switched on: see <see cref="ShowAround"/>.
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

    /// <summary>How much of a dab's radius its soft rim fades over.</summary>
    private const float DabRim = 0.2f;

    /// <summary>
    /// The decal shader draws nothing where dot(surface normal, decal axis) is below the threshold
    /// and fades in over the next smoothness (a smoothstep). Every surface is within 54.7 degrees
    /// (dot 0.577) of some axis, so 0.5 leaves no gaps; facets near 45 degrees get a little overlap.
    /// </summary>
    private const float NormalsFadeThreshold = 0.5f;
    private const float NormalsFadeSmoothness = 0.12f;

    /// <summary>Rays per side (squared) when surveying which ways a cube's rock faces.</summary>
    private const int SurveyGrid = 4;

    /// <summary>
    /// Offset, as a fraction of the radius, of the outer rays in the 3x3 grid cast through a dab
    /// to see which ways the rock inside it faces.
    /// </summary>
    private const float ReachSpread = 0.6f;

    /// <summary>Thinnest a decal box gets along its axis, for dabs that only graze a cube.</summary>
    private const float MinDepthSpan = 0.01f;

    /// <summary>Texture work allowed per frame before the rest waits for the next one.</summary>
    private const double FlushBudgetMs = 6.0;

    /// <summary>How far the edge glow reaches into the paint, in texels.</summary>
    private const float GlowWidth = 6f;

    /// <summary>
    /// How far in from the edge the glow is brightest, in texels. Rising from nothing at the edge
    /// keeps the glow's outline smooth when the texture is filtered.
    /// </summary>
    private const float GlowRise = 1.5f;

    /// <summary>Halvings of the gap between two texels when finding where an edge crosses it.</summary>
    private const int EdgeBisections = 5;

    /// <summary>
    /// The glow decals' _MainTexAsEmission: their texture times this is drawn as emission. Much
    /// above 0.5 the tone mapping washes yellow, then every colour, out to white.
    /// </summary>
    private const float GlowEmission = 0.5f;

    /// <summary>The glow pulse: seconds per breath, and how far the emission dims at its lowest.</summary>
    private const float PulsePeriod = 3f;
    private const float PulseDepth = 0.5f;

    /// <summary>
    /// The pulse retunes one in this many glow materials a frame, taking turns: each SetFloat crosses
    /// into the game, at about a microsecond apiece.
    /// </summary>
    private const int PulseGroups = 4;

    /// <summary>
    /// How much further than <see cref="ShowRadius"/> a cube that's showing stays shown, so cubes on
    /// the edge don't flick on and off as Ava shifts about.
    /// </summary>
    private const float ShowSlack = 3f;

    /// <summary>
    /// How much of <see cref="ShowRadius"/>, at its outer end, the paint fades out over, so it
    /// thins away rather than stopping at a hard edge, and cubes switch on unseen.
    /// </summary>
    private const float FadeShare = 0.4f;

    /// <summary>Smallest change in a cube's opacity worth a call into the game.</summary>
    private const float OpacityStep = 0.02f;

    /// <summary>How far the focus moves before the cubes are checked again: small enough that the fade looks smooth.</summary>
    private const float RecheckDistance = 0.25f;

    /// <summary>How far the focus moves before a faded texture is faded again; see <see cref="RefreshFades"/>.</summary>
    private const float FadeRefresh = 0.25f;

    /// <summary>
    /// How far the focus can get from where a cube's texture was faded before the texture is put
    /// back as drawn, and the cube's own opacity takes over until it's faded anew.
    /// </summary>
    private const float FadeTrust = 1f;

    /// <summary>Texture fading allowed per frame; cubes left over wait their turn.</summary>
    private const double FadeBudgetMs = 1.0;

    /// <summary>Furthest any point of a cube is from its centre.</summary>
    private static readonly float CellReach = CellSize * MathF.Sqrt(3f) * 0.5f;

    private static readonly int MainTexAsEmissionId = Shader.PropertyToID("_MainTexAsEmission");

    /// <summary>What <see cref="Facings"/> gives for a cube with nothing solid in it.</summary>
    private const int NoRock = -1;

    private static readonly NVector3[] Axes =
    {
        NVector3.UnitX, -NVector3.UnitX, NVector3.UnitY, -NVector3.UnitY, NVector3.UnitZ, -NVector3.UnitZ,
    };

    private sealed class Entry
    {
        public int StrokeIndex;
        public StrokeMode Mode;
        public NVector3 Position;
        public float Radius;
    }

    /// <summary>A stamp on a cell's texture (see <see cref="TexelCircle"/>), and the kind of paint it leaves inside.</summary>
    private struct Circle
    {
        public float X;
        public float Y;
        public float RadiusSquared;
        public byte Kind;
    }

    private sealed class Cell
    {
        /// <summary>The cube, in cube steps. Not a Vector3Int: reading one of its coordinates is a call into the game.</summary>
        public (int X, int Y, int Z) Key;
        public int Bucket;
        public NVector3 Center;
        public NVector3 Axis;
        public NVector3 Right;
        public NVector3 Up;

        /// <summary>Span along <see cref="Axis"/> covered by paint (not erase) entries' spheres.</summary>
        public float MinDepth = float.PositiveInfinity;
        public float MaxDepth = float.NegativeInfinity;
        public readonly List<Entry> Entries = new();
        public GameObject GameObject;
        public TGBDecal Decal;
        public Material Material;
        public Texture2D Texture;
        public byte[] Pixels;

        /// <summary>Per texel: 0 unpainted, 1 + (int)StrokeMode otherwise.</summary>
        public byte[] Modes;
        public float[] Coverage;

        /// <summary>A second, emissive decal (a child of <see cref="GameObject"/>) lighting up the paint's edges.</summary>
        public GameObject Glow;
        public TGBDecal GlowDecal;
        public Material GlowMaterial;
        public Texture2D GlowTexture;
        public bool NeedsRedraw;
        public bool NeedsUpload;
        public bool Queued;

        /// <summary>Whether <see cref="GameObject"/> is active, so its decals draw; see <see cref="ShowAround"/>.</summary>
        public bool Shown;

        /// <summary>The fill and glow decals' opacity multipliers; see <see cref="UpdateOpacity"/>.</summary>
        public float FillOpacity = 1f;
        public float GlowOpacity = 1f;

        /// <summary>
        /// The focus <see cref="Texture"/> was last faded around (see <see cref="FadeTexture"/>), or
        /// null while it holds the paint as drawn.
        /// </summary>
        public NVector3? FadedFor;

        /// <summary>The <see cref="ShowRadius"/> <see cref="FadedFor"/>'s fade was for.</summary>
        public float FadedRadius;

        /// <summary>Middle of the decal box, which <see cref="Place"/> moves along the axis to the paint.</summary>
        public NVector3 BoxCenter;
    }

    private readonly Transform _parent;
    private readonly Dictionary<((int X, int Y, int Z) Cube, int Bucket), Cell> _cells = new();

    /// <summary>
    /// For each surveyed cube, a bit per entry of <see cref="Axes"/> that some rock in it faces, or
    /// <see cref="NoRock"/> if there's no climbable surface in it at all.
    /// </summary>
    private readonly Dictionary<(int X, int Y, int Z), int> _cubeFacings = new();
    private readonly Queue<Cell> _pending = new();
    private readonly Stopwatch _drawTimer = new();
    private readonly Stopwatch _frameTimer = new();
    private double _drawWorkMs;
    private bool _reportWhenDrawn;

    /// <summary>Where the drawing time goes, in ms, for <see cref="DrawingFinished"/>.</summary>
    private struct DrawStats
    {
        public double Survey;
        public double Reach;
        public double Create;
        public double Paint;
        public double Glow;
        public double Upload;
        public int Cubes;
        public int EmptyCubes;
    }

    private DrawStats _stats;
    private double _animateMs;

    /// <summary>Stamps waiting to be added, in paint order; see <see cref="AddLater"/>.</summary>
    private readonly Queue<(int StrokeIndex, StrokeMode Mode, Stamp Stamp)> _pendingStamps = new();
    private Material _baseMaterial;
    private TGBDecal _template;
    private bool _warned;
    private Color32 _allowColor;
    private Color32 _denyColor;
    private Material _glowBaseMaterial;
    private bool _fill = true;
    private bool _glow = true;

    /// <summary>Where paint shows around (see <see cref="ShowAround"/>), or null for everywhere.</summary>
    private NVector3? _focus;

    /// <summary>The focus when the cubes were last checked against it.</summary>
    private NVector3 _checkedFocus;
    private float _showRadius = float.PositiveInfinity;

    /// <summary>Set when a bulk load is queued, so its cubes are drawn nearest the focus first.</summary>
    private bool _sortPending;

    /// <summary>Whether some texture may need fading or restoring; see <see cref="RefreshFades"/>.</summary>
    private bool _fadeWork;

    /// <summary>Where in <see cref="_cells"/> the next frame's texture fading starts, so every cube gets its turn.</summary>
    private int _fadeCursor;

    /// <summary>A cube's paint with its fade applied, for upload (pinned, see NewPixels).</summary>
    private readonly byte[] _fadePixels = NewPixels();

    /// <summary>Slowest <see cref="RefreshFades"/> since <see cref="TakeSlowestFadeMs"/> last asked, in ms.</summary>
    private double _slowestFadeMs;

    /// <summary>Whether the last <see cref="Animate"/> left the glow dimmed.</summary>
    private bool _dimmed;
    private int _pulseGroup;

    // Scratch space for DrawGlow: per texel, the squared distance to and position of the nearest
    // edge point found so far; the texture it draws (pinned, see NewPixels); and the cell's stamps
    // as circles.
    private readonly float[] _glowDistances = new float[Resolution * Resolution];
    private readonly float[] _glowNearestX = new float[Resolution * Resolution];
    private readonly float[] _glowNearestY = new float[Resolution * Resolution];
    private readonly byte[] _glowPixels = NewPixels();
    private Circle[] _circles = new Circle[256];

    public DecalPaint(Transform parent) => _parent = parent;

    /// <summary>Change the paint colours; what's already painted is redrawn over the next few frames.</summary>
    public void SetColors(Color32 allow, Color32 deny)
    {
        var changed = !PaintColors.Same(allow, _allowColor) || !PaintColors.Same(deny, _denyColor);
        _allowColor = allow;
        _denyColor = deny;
        if (!changed)
        {
            return;
        }

        foreach (var cell in _cells.Values)
        {
            cell.NeedsRedraw = true;
            Enqueue(cell);
        }
    }

    /// <summary>
    /// Goes up whenever paint decals register with the game's decal system. Decals draw in the order
    /// they register, so anything that has to draw over the paint (the start and finish rings)
    /// registers again after this changes.
    /// </summary>
    public int Registrations { get; private set; }

    /// <summary>Show or hide the fill and the edge glow. Glow textures are only drawn while it shows.</summary>
    public void SetStyle(bool fill, bool glow)
    {
        var fillShown = fill && !_fill;
        var glowShown = glow && !_glow;
        _fill = fill;
        _glow = glow;
        if (fillShown || glowShown)
        {
            Registrations++;
        }
        foreach (var cell in _cells.Values)
        {
            if (cell.GameObject == null)
            {
                continue;
            }

            cell.Decal.enabled = fill;
            if (cell.Glow == null)
            {
                continue;
            }

            // A fill shown again registers last, so draws over the glow; register the glow after it.
            if (fillShown && cell.Glow.activeSelf)
            {
                cell.GlowDecal.enabled = false;
                cell.GlowDecal.enabled = true;
            }

            cell.Glow.SetActive(glow);
            if (glowShown)
            {
                cell.NeedsUpload = true;
                Enqueue(cell);
            }
        }
    }

    /// <summary>Whether the edge glow slowly pulses; see <see cref="Animate"/>.</summary>
    public bool GlowPulse { get; set; } = true;

    /// <summary>
    /// Pulse the glow; call every frame with the time in seconds. It dims from full and back rather
    /// than brightening, since brighter washes the colours out (see <see cref="GlowEmission"/>).
    /// </summary>
    public void Animate(float time)
    {
        var pulsing = GlowPulse && _glow;
        if ((!pulsing && !_dimmed) || !_parent.gameObject.activeInHierarchy)
        {
            return;
        }

        var started = Stopwatch.GetTimestamp();
        var dim = pulsing ? PulseDepth * (0.5f - 0.5f * MathF.Cos(time * (2f * MathF.PI / PulsePeriod))) : 0f;
        var emission = GlowEmission * (1f - dim);

        // While pulsing, each shown material only takes its turn (see PulseGroups); stopping resets them all.
        _pulseGroup = (_pulseGroup + 1) % PulseGroups;
        var index = 0;
        foreach (var cell in _cells.Values)
        {
            if (cell.GlowMaterial != null && (!pulsing || (cell.Shown && index % PulseGroups == _pulseGroup)))
            {
                cell.GlowMaterial.SetFloat(MainTexAsEmissionId, emission);
            }

            index++;
        }

        _dimmed = pulsing;
        _animateMs = MsSince(started);
    }

    /// <summary>Called once queued drawing finishes, with a summary of the work for the log.</summary>
    public Action<string> DrawingFinished { get; set; }

    /// <summary>How far from the focus paint shows, in metres; infinity for everywhere.</summary>
    public float ShowRadius
    {
        get => _showRadius;
        set
        {
            if (value != _showRadius)
            {
                _showRadius = value;
                RecheckShown();
            }
        }
    }

    /// <summary>How many cubes have paint, how many of them are switched on, and how many of those have faded textures.</summary>
    public (int Cubes, int Shown, int Faded) Counts
    {
        get
        {
            var shown = 0;
            var faded = 0;
            foreach (var cell in _cells.Values)
            {
                if (cell.Shown)
                {
                    shown++;
                    if (cell.FadedFor != null)
                    {
                        faded++;
                    }
                }
            }

            return (_cells.Count, shown, faded);
        }
    }

    /// <summary>
    /// Show only the paint within <see cref="ShowRadius"/> of this point, or all of it for null; call
    /// every frame. Cubes further away are switched off, so the game's decal system drops them
    /// altogether, and the paint fades out towards the edge (see <see cref="FadeShare"/>). Cubes
    /// are only checked again once the focus has moved <see cref="RecheckDistance"/>.
    /// </summary>
    public void ShowAround(NVector3? focus)
    {
        var moved = focus is { } point
            ? _focus == null || NVector3.DistanceSquared(point, _checkedFocus) > RecheckDistance * RecheckDistance
            : _focus != null;
        _focus = focus;
        if (moved)
        {
            RecheckShown();
        }

        RefreshFades();
    }

    /// <summary>Switch every cube on or off for the current focus and radius.</summary>
    private void RecheckShown()
    {
        if (_focus is { } focus)
        {
            _checkedFocus = focus;
        }

        foreach (var cell in _cells.Values)
        {
            UpdateShown(cell);
        }

        _fadeWork = true;
    }

    private void UpdateShown(Cell cell)
    {
        if (cell.GameObject == null)
        {
            return;
        }

        var show = ShouldShow(cell);
        if (show)
        {
            // Before switching on, so a cube coming into range never draws a frame at its old opacity.
            UpdateOpacity(cell);
        }

        if (show != cell.Shown)
        {
            cell.Shown = show;
            cell.GameObject.SetActive(show);
            if (show)
            {
                Registrations++;
            }
        }
    }

    /// <summary>
    /// Fade a cube as a whole, by its centre's distance (see <see cref="OpacityFor"/>). That steps
    /// from cube to cube, so the fill's fade is done per texel instead once its texture has been
    /// faded (see <see cref="FadeTexture"/>); the glow's thin edges keep the whole-cube fade.
    /// </summary>
    private void UpdateOpacity(Cell cell)
    {
        // A texture faded around somewhere else, or for another radius, could hide paint that should show.
        if (cell.FadedFor is { } faded && (cell.FadedRadius != _showRadius || _focus is not { } focus
            || NVector3.DistanceSquared(faded, focus) > FadeTrust * FadeTrust))
        {
            Load(cell.Texture, cell.Pixels);
            cell.FadedFor = null;
        }

        var opacity = OpacityFor(cell);
        SetOpacity(cell.Decal, ref cell.FillOpacity, cell.FadedFor != null ? 1f : opacity);
        if (cell.GlowDecal != null)
        {
            SetOpacity(cell.GlowDecal, ref cell.GlowOpacity, opacity);
        }
    }

    /// <summary>Not the material's _Opacity: the decal's property block sets its own, which wins.</summary>
    private static void SetOpacity(TGBDecal decal, ref float current, float opacity)
    {
        if (opacity == current || (MathF.Abs(opacity - current) < OpacityStep && opacity is > 0f and < 1f))
        {
            return;
        }

        current = opacity;
        decal.SetOpacityMultiplier(opacity);
    }

    /// <summary>
    /// How opaque paint this far from the focus is: full near it, easing to nothing at
    /// <see cref="ShowRadius"/> over its outer <see cref="FadeShare"/>.
    /// </summary>
    private float FadeAt(float distance)
    {
        var t = (_showRadius - distance) / (_showRadius * FadeShare);
        return t >= 1f ? 1f : t <= 0f ? 0f : t * t * (3f - 2f * t);
    }

    private float OpacityFor(Cell cell) => _focus is { } focus && !float.IsPositiveInfinity(_showRadius)
        ? FadeAt(NVector3.Distance(cell.Center, focus))
        : 1f;

    /// <summary>Whether some of a showing cube's paint is in the fade, so its texture should be faded.</summary>
    private bool WantsFade(Cell cell)
    {
        if (!cell.Shown || _focus is not { } focus || float.IsPositiveInfinity(_showRadius))
        {
            return false;
        }

        var distance = NVector3.Distance(cell.Center, focus);
        return distance - CellReach < _showRadius && distance + CellReach > _showRadius * (1f - FadeShare);
    }

    /// <summary>Whether a cube's texture needs fading again, or putting back as drawn.</summary>
    private bool NeedsFade(Cell cell)
    {
        if (cell.Texture == null)
        {
            return false;
        }

        if (!WantsFade(cell))
        {
            return cell.FadedFor != null;
        }

        return cell.FadedFor is not { } faded || NVector3.DistanceSquared(faded, _focus.Value) > FadeRefresh * FadeRefresh;
    }

    /// <summary>
    /// Fade the textures of cubes reaching into the fade, and put back those that have left it,
    /// for up to <see cref="FadeBudgetMs"/> a frame, taking turns from where the last frame stopped.
    /// </summary>
    private void RefreshFades()
    {
        if (!_fadeWork)
        {
            return;
        }

        var started = Stopwatch.GetTimestamp();
        var budget = (long)(FadeBudgetMs * Stopwatch.Frequency / 1000.0);
        var stoppedAt = -1;
        for (var lap = 0; lap < 2 && stoppedAt < 0; lap++)
        {
            var index = 0;
            foreach (var cell in _cells.Values)
            {
                var turn = lap == 0 ? index >= _fadeCursor : index < _fadeCursor;
                index++;
                if (!turn || !NeedsFade(cell))
                {
                    continue;
                }

                if (Stopwatch.GetTimestamp() - started > budget)
                {
                    stoppedAt = index - 1;
                    break;
                }

                if (WantsFade(cell))
                {
                    FadeTexture(cell, _focus.Value);
                }
                else
                {
                    Load(cell.Texture, cell.Pixels);
                    cell.FadedFor = null;
                }

                UpdateOpacity(cell);
            }
        }

        _fadeWork = stoppedAt >= 0;
        _fadeCursor = Math.Max(stoppedAt, 0);
        _slowestFadeMs = Math.Max(_slowestFadeMs, MsSince(started));
    }

    /// <summary>The longest a frame's texture fading took since the last call, in ms, for the log.</summary>
    public double TakeSlowestFadeMs()
    {
        var slowest = _slowestFadeMs;
        _slowestFadeMs = 0;
        return slowest;
    }

    /// <summary>
    /// Upload a cube's paint with each texel's alpha faded by its own distance from the focus
    /// (<see cref="FadeAt"/>), which runs smoothly across cubes where fading whole cubes steps.
    /// A texel is taken to lie on the plane through the middle of the decal box.
    /// </summary>
    private void FadeTexture(Cell cell, NVector3 focus)
    {
        var pixels = cell.Pixels;
        var faded = _fadePixels;
        Buffer.BlockCopy(pixels, 0, faded, 0, pixels.Length);

        // Squared distance to texel (x, y) on the plane: |w + x Right + y Up|^2, Right and Up being orthonormal.
        var w = cell.BoxCenter - focus;
        var rightW = 2f * NVector3.Dot(cell.Right, w);
        var upW = 2f * NVector3.Dot(cell.Up, w);
        var wSquared = w.LengthSquared();
        for (var j = 0; j < Resolution; j++)
        {
            var y = (j + 0.5f) * TexelSize - CellSize * 0.5f;
            var row = wSquared + y * (upW + y);
            for (var i = 0; i < Resolution; i++)
            {
                var alpha = (j * Resolution + i) * 4 + 3;
                if (pixels[alpha] == 0)
                {
                    continue;
                }

                var x = (i + 0.5f) * TexelSize - CellSize * 0.5f;
                faded[alpha] = (byte)(pixels[alpha] * FadeAt(MathF.Sqrt(row + x * (rightW + x))));
            }
        }

        Load(cell.Texture, faded);
        cell.FadedFor = focus;
        cell.FadedRadius = _showRadius;
    }

    /// <summary>
    /// Whether any of a cube is near enough the focus to show, so it's on before its nearest paint
    /// starts fading in; one already showing gets <see cref="ShowSlack"/> more.
    /// </summary>
    private bool ShouldShow(Cell cell)
    {
        if (_focus is not { } focus || float.IsPositiveInfinity(_showRadius))
        {
            return true;
        }

        var reach = _showRadius + CellReach + (cell.Shown ? ShowSlack : 0f);
        return NVector3.DistanceSquared(cell.Center, focus) <= reach * reach;
    }

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
        _sortPending = false;
        _fadeWork = false;
        _fadeCursor = 0;
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
        _sortPending = true;
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
        var dominant = Dominant(stamp.Normal);

        // Bits over Axes: which directions have been checked with Reaches, and which passed.
        // The dab's own surface always counts.
        var checkedFacings = 1 << dominant;
        var reachedFacings = 1 << dominant;
        for (var x = minX; x <= maxX; x++)
        for (var y = minY; y <= maxY; y++)
        for (var z = minZ; z <= maxZ; z++)
        {
            var key = (x, y, z);
            var center = new NVector3(x + 0.5f, y + 0.5f, z + 0.5f) * CellSize;
            if (!SphereTouchesCube(p, r, center))
            {
                continue;
            }

            // Erase reaches every decal in the cube; paint goes where the survey found rock, plus
            // the dab's own surface (the survey can miss thin features), but never into a cube
            // with no rock in it, which a big brush reaches plenty of.
            var facings = (1 << Axes.Length) - 1;
            if (mode != StrokeMode.Erase)
            {
                var surveyed = Facings(key, center);
                if (surveyed == NoRock)
                {
                    continue;
                }

                facings = surveyed | (1 << dominant);
            }

            for (var bucket = 0; bucket < Axes.Length; bucket++)
            {
                if ((facings & (1 << bucket)) == 0)
                {
                    continue;
                }

                if (mode != StrokeMode.Erase)
                {
                    // Rock somewhere in the cube facing this way isn't enough: the dab has to touch it.
                    if ((checkedFacings & (1 << bucket)) == 0)
                    {
                        checkedFacings |= 1 << bucket;
                        var started = Stopwatch.GetTimestamp();
                        if (Reaches(stamp, Axes[bucket]))
                        {
                            reachedFacings |= 1 << bucket;
                        }

                        _stats.Reach += MsSince(started);
                    }

                    if ((reachedFacings & (1 << bucket)) == 0)
                    {
                        continue;
                    }
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
                    if (Extend(cell, entry))
                    {
                        Place(cell);
                    }

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
    /// get colour from that direction's decal at all (dot at or above the fade threshold). Gives
    /// <see cref="NoRock"/> if nothing solid overlaps the cube.
    /// </summary>
    private int Facings((int X, int Y, int Z) key, NVector3 center)
    {
        if (_cubeFacings.TryGetValue(key, out var known))
        {
            return known;
        }

        var started = Stopwatch.GetTimestamp();
        var mask = NoRock;
        var half = CellSize * 0.5f;
        var unityCenter = RouteOverlay.ToUnity(center);
        if (Physics.CheckBox(unityCenter, Vector3.one * half, Quaternion.identity, GameApi.SurfaceMask, QueryTriggerInteraction.Ignore))
        {
            mask = 0;
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
        _stats.Survey += MsSince(started);
        _stats.Cubes++;
        if (mask == NoRock)
        {
            _stats.EmptyCubes++;
        }

        return mask;
    }

    /// <summary>
    /// Whether rock inside a dab faces <paramref name="axis"/> closely enough for that direction's
    /// decal to colour it, found by casting a 3x3 grid of rays down the axis through the sphere.
    /// </summary>
    private static bool Reaches(Stamp stamp, NVector3 axis)
    {
        var a = MathF.Abs(axis.X) > 0.5f ? NVector3.UnitY : NVector3.UnitX;
        var b = NVector3.Cross(axis, a);
        var direction = RouteOverlay.ToUnity(-axis);
        var r = stamp.Radius;
        for (var i = -1; i <= 1; i++)
        for (var j = -1; j <= 1; j++)
        {
            var offset = (a * i + b * j) * (r * ReachSpread);

            // Cast only along the chord inside the sphere, so any hit is inside the dab.
            var halfChord = MathF.Sqrt(r * r - offset.LengthSquared());
            var origin = stamp.Position + offset + axis * halfChord;
            if (Physics.Raycast(RouteOverlay.ToUnity(origin), direction, out var hit, 2f * halfChord, GameApi.SurfaceMask, QueryTriggerInteraction.Ignore)
                && NVector3.Dot(RouteOverlay.ToNumerics(hit.normal), axis) >= NormalsFadeThreshold)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Grow a cell's depth span to cover a paint entry; true if it changed.</summary>
    private static bool Extend(Cell cell, Entry entry)
    {
        if (entry.Mode == StrokeMode.Erase)
        {
            return false;
        }

        var depth = NVector3.Dot(entry.Position, cell.Axis);
        if (depth - entry.Radius >= cell.MinDepth && depth + entry.Radius <= cell.MaxDepth)
        {
            return false;
        }

        cell.MinDepth = MathF.Min(cell.MinDepth, depth - entry.Radius);
        cell.MaxDepth = MathF.Max(cell.MaxDepth, depth + entry.Radius);
        return true;
    }

    /// <summary>
    /// Fit a cell's decal box to its cube across the axis and to its depth span along it. The
    /// shader takes uv from the box's local X and Z, so only the projection depth changes.
    /// </summary>
    private static void Place(Cell cell)
    {
        var half = CellSize * 0.5f;
        var centerDepth = NVector3.Dot(cell.Center, cell.Axis);
        var min = MathF.Max(cell.MinDepth, centerDepth - half);
        var max = MathF.Min(cell.MaxDepth, centerDepth + half);
        if (min > max)
        {
            // Nothing but erase entries left; the texture is blank anyway.
            min = centerDepth - half;
            max = centerDepth + half;
        }

        var (rotation, _, _) = Orientation(cell.Axis);
        var position = cell.Center + cell.Axis * ((min + max) * 0.5f - centerDepth);
        cell.BoxCenter = position;
        var transform = cell.GameObject.transform;
        transform.SetPositionAndRotation(RouteOverlay.ToUnity(position), rotation);
        transform.localScale = new Vector3(CellSize, MathF.Max(max - min, MinDepthSpan), CellSize);
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
            _stats = default;
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

        if (_pendingStamps.Count == 0 && _sortPending)
        {
            _sortPending = false;
            SortPendingByDistance();
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
                Upload(cell);
            }
        }

        _drawWorkMs += _frameTimer.Elapsed.TotalMilliseconds;
        if (_pending.Count == 0 && _pendingStamps.Count == 0 && _drawTimer.IsRunning)
        {
            _drawTimer.Stop();
            if (_reportWhenDrawn)
            {
                _reportWhenDrawn = false;
                var s = _stats;
                DrawingFinished?.Invoke(
                    $"{_cells.Count} decals, {_drawWorkMs:0} ms of work over {_drawTimer.Elapsed.TotalMilliseconds:0} ms "
                    + $"(surveying {s.Survey:0} ms for {s.Cubes} cubes, {s.EmptyCubes} without rock; reach checks {s.Reach:0} ms; "
                    + $"creating decals {s.Create:0} ms; painting {s.Paint:0} ms; glow {s.Glow:0} ms; uploading {s.Upload:0} ms). "
                    + $"Glow pulse: {_animateMs:0.00} ms a frame.");
            }
        }
    }

    /// <summary>Put the queued cubes nearest the focus first, so a route loads in around Ava.</summary>
    private void SortPendingByDistance()
    {
        if (_focus is not { } focus || _pending.Count < 2)
        {
            return;
        }

        var cells = _pending.ToArray();
        var distances = new float[cells.Length];
        for (var i = 0; i < cells.Length; i++)
        {
            distances[i] = NVector3.DistanceSquared(cells[i].Center, focus);
        }

        Array.Sort(distances, cells);
        _pending.Clear();
        foreach (var cell in cells)
        {
            _pending.Enqueue(cell);
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

        var emptied = new List<((int X, int Y, int Z), int)>();
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
        var started = Stopwatch.GetTimestamp();
        if (cell.GameObject == null)
        {
            var created = TryCreateDecal(cell);
            _stats.Create += MsSince(started);
            if (!created)
            {
                return;
            }

            started = Stopwatch.GetTimestamp();
        }

        cell.MinDepth = float.PositiveInfinity;
        cell.MaxDepth = float.NegativeInfinity;
        foreach (var entry in cell.Entries)
        {
            Extend(cell, entry);
        }

        Place(cell);

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
        _stats.Paint += MsSince(started);
    }

    private void Upload(Cell cell)
    {
        var started = Stopwatch.GetTimestamp();
        Load(cell.Texture, cell.Pixels);

        // The texture is as drawn again: the cube's own opacity fades it until it's faded anew.
        cell.FadedFor = null;
        UpdateOpacity(cell);
        _fadeWork = true;
        if (_glow && cell.GlowTexture != null)
        {
            _stats.Upload += MsSince(started);
            started = Stopwatch.GetTimestamp();
            DrawGlow(cell);
            _stats.Glow += MsSince(started);
            started = Stopwatch.GetTimestamp();
            Load(cell.GlowTexture, _glowPixels);
        }

        _stats.Upload += MsSince(started);
        cell.NeedsUpload = false;
    }

    /// <summary>
    /// Pixel memory for <see cref="Load"/>, pinned so the texture can copy straight out of it.
    /// Passing a byte[] to LoadRawTextureData instead would copy it into a new IL2CPP array first,
    /// leaving 64 KB of garbage on the game's heap per texture per upload.
    /// </summary>
    private static byte[] NewPixels() => GC.AllocateArray<byte>(Resolution * Resolution * 4, pinned: true);

    private static void Load(Texture2D texture, byte[] pixels)
    {
        texture.LoadRawTextureData(Marshal.UnsafeAddrOfPinnedArrayElement(pixels, 0), pixels.Length);
        texture.Apply(false);
    }

    private static double MsSince(long timestamp) => (Stopwatch.GetTimestamp() - timestamp) * 1000.0 / Stopwatch.Frequency;

    /// <summary>
    /// Draw a cell's glow texture into <see cref="_glowPixels"/>: a band along the inside of every
    /// edge of the paint, in the paint's colour, rising over <see cref="GlowRise"/> and gone by
    /// <see cref="GlowWidth"/>. Edges are where paint meets bare rock or paint of the other kind. The
    /// texture's own border isn't an edge, since the paint carries on in the next cube.
    ///
    /// Edges are placed between texel centres, so the glow doesn't follow the texels' staircase:
    /// wherever two neighbouring texels differ, bisection against the stamps finds where the paint
    /// changes between them. A vector distance transform (two sweeps there and back) then gives
    /// every texel the nearest of those points.
    /// </summary>
    private void DrawGlow(Cell cell)
    {
        const int N = Resolution;
        var kinds = cell.Modes;
        var distances = _glowDistances;
        Array.Fill(distances, float.PositiveInfinity);

        var circles = LoadCircles(cell);
        for (var j = 0; j < N; j++)
        for (var i = 0; i < N; i++)
        {
            var texel = j * N + i;
            if (i < N - 1 && kinds[texel] != kinds[texel + 1])
            {
                SeedEdge(circles, kinds[texel], texel, i, j, texel + 1, i + 1, j);
            }

            if (j < N - 1 && kinds[texel] != kinds[texel + N])
            {
                SeedEdge(circles, kinds[texel], texel, i, j, texel + N, i, j + 1);
            }
        }

        // Only painted texels need a distance, and the nearest edge point of each is in sight of it
        // across paint, so the sweeps pass points through painted texels only.
        for (var j = 0; j < N; j++)
        {
            for (var i = 0; i < N; i++)
            {
                var texel = j * N + i;
                if (kinds[texel] == 0) continue;
                if (i > 0) PullNearest(texel, i, j, texel - 1);
                if (j > 0)
                {
                    PullNearest(texel, i, j, texel - N);
                    if (i > 0) PullNearest(texel, i, j, texel - N - 1);
                    if (i < N - 1) PullNearest(texel, i, j, texel - N + 1);
                }
            }

            for (var i = N - 2; i >= 0; i--)
            {
                if (kinds[j * N + i] != 0) PullNearest(j * N + i, i, j, j * N + i + 1);
            }
        }

        for (var j = N - 1; j >= 0; j--)
        {
            for (var i = N - 1; i >= 0; i--)
            {
                var texel = j * N + i;
                if (kinds[texel] == 0) continue;
                if (i < N - 1) PullNearest(texel, i, j, texel + 1);
                if (j < N - 1)
                {
                    PullNearest(texel, i, j, texel + N);
                    if (i < N - 1) PullNearest(texel, i, j, texel + N + 1);
                    if (i > 0) PullNearest(texel, i, j, texel + N - 1);
                }
            }

            for (var i = 1; i < N; i++)
            {
                if (kinds[j * N + i] != 0) PullNearest(j * N + i, i, j, j * N + i - 1);
            }
        }

        var allowColor = _allowColor;
        var denyColor = _denyColor;
        var pixels = _glowPixels;
        for (var texel = 0; texel < N * N; texel++)
        {
            var kind = kinds[texel];
            var index = texel * 4;
            if (kind == 0)
            {
                pixels[index] = pixels[index + 1] = pixels[index + 2] = pixels[index + 3] = 0;
                continue;
            }

            var color = kind == 1 + (int)StrokeMode.Allow ? allowColor : denyColor;
            var d = MathF.Sqrt(distances[texel]);
            var rise = MathF.Min(d / GlowRise, 1f);
            var fall = MathF.Max(1f - MathF.Max(d - GlowRise, 0f) / (GlowWidth - GlowRise), 0f);
            pixels[index] = color.r;
            pixels[index + 1] = color.g;
            pixels[index + 2] = color.b;
            pixels[index + 3] = (byte)(rise * fall * fall * 255f);
        }
    }

    /// <summary>
    /// Find where the paint changes on the way from texel a, painted <paramref name="kind"/>, to its
    /// neighbour b, and offer that point to both as their nearest edge.
    /// </summary>
    private void SeedEdge(int circles, byte kind, int a, int ai, int aj, int b, int bi, int bj)
    {
        float low = 0f, high = 1f;
        for (var step = 0; step < EdgeBisections; step++)
        {
            var mid = (low + high) * 0.5f;
            if (KindAt(circles, ai + (bi - ai) * mid, aj + (bj - aj) * mid) == kind)
            {
                low = mid;
            }
            else
            {
                high = mid;
            }
        }

        var t = (low + high) * 0.5f;
        var x = ai + (bi - ai) * t;
        var y = aj + (bj - aj) * t;
        OfferNearest(a, ai, aj, x, y);
        OfferNearest(b, bi, bj, x, y);
    }

    private void OfferNearest(int texel, int i, int j, float x, float y)
    {
        var dx = x - i;
        var dy = y - j;
        var distance = dx * dx + dy * dy;
        if (distance < _glowDistances[texel])
        {
            _glowDistances[texel] = distance;
            _glowNearestX[texel] = x;
            _glowNearestY[texel] = y;
        }
    }

    /// <summary>Take a neighbour's nearest edge point if it's nearer than this texel's own.</summary>
    private void PullNearest(int texel, int i, int j, int from)
    {
        // Points much further than the glow reaches don't matter, or need passing on.
        if (_glowDistances[from] > (GlowWidth + 2f) * (GlowWidth + 2f))
        {
            return;
        }

        OfferNearest(texel, i, j, _glowNearestX[from], _glowNearestY[from]);
    }

    /// <summary>Put a cell's entries into <see cref="_circles"/>, oldest first; returns how many.</summary>
    private int LoadCircles(Cell cell)
    {
        var count = cell.Entries.Count;
        if (_circles.Length < count)
        {
            _circles = new Circle[Math.Max(count, _circles.Length * 2)];
        }

        for (var k = 0; k < count; k++)
        {
            var entry = cell.Entries[k];
            var (x, y, radius) = TexelCircle(cell, entry);
            var erase = entry.Mode == StrokeMode.Erase;

            // Erase only clears paint completely inside its soft rim.
            if (erase)
            {
                radius *= 1f - DabRim;
            }

            _circles[k] = new Circle { X = x, Y = y, RadiusSquared = radius * radius, Kind = erase ? (byte)0 : (byte)(1 + (int)entry.Mode) };
        }

        return count;
    }

    /// <summary>The paint at a point in texel coordinates, as in <see cref="Cell.Modes"/>: the newest stamp over it decides.</summary>
    private byte KindAt(int circles, float x, float y)
    {
        for (var k = circles - 1; k >= 0; k--)
        {
            ref var circle = ref _circles[k];
            var dx = x - circle.X;
            var dy = y - circle.Y;
            if (dx * dx + dy * dy < circle.RadiusSquared)
            {
                return circle.Kind;
            }
        }

        return 0;
    }

    /// <summary>
    /// Paint one stamp over a cell's texture. It's newer than everything already there, so it
    /// takes every texel it covers; where the colour is unchanged the stronger coverage is kept,
    /// so overlapping dabs of one stroke don't leave faint rings. Erase stamps fade out whatever
    /// is there instead.
    /// </summary>
    private void Rasterize(Cell cell, Entry entry)
    {
        var allowColor = _allowColor;
        var denyColor = _denyColor;
        var (ci, cj, radius) = TexelCircle(cell, entry);
        var i0 = Math.Max(0, (int)MathF.Floor(ci - radius));
        var i1 = Math.Min(Resolution - 1, (int)MathF.Ceiling(ci + radius));
        var j0 = Math.Max(0, (int)MathF.Floor(cj - radius));
        var j1 = Math.Min(Resolution - 1, (int)MathF.Ceiling(cj + radius));
        var erase = entry.Mode == StrokeMode.Erase;
        var mode = (byte)(1 + (int)entry.Mode);
        var rim = radius * DabRim;
        var grain = GrainSeed(cell);
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
            var color = texelMode == 1 + (int)StrokeMode.Allow ? allowColor : denyColor;
            var index = texel * 4;
            cell.Pixels[index] = color.r;
            cell.Pixels[index + 1] = color.g;
            cell.Pixels[index + 2] = color.b;
            cell.Pixels[index + 3] = (byte)(PaintAlpha * coverage * SprayGrain(grain, i, j) * 255f);
        }
    }

    /// <summary>
    /// A stamp on a cell's texture: its centre in texel coordinates, where texel (i, j) is centred
    /// on (i, j), and its radius in texels.
    /// </summary>
    private static (float X, float Y, float Radius) TexelCircle(Cell cell, Entry entry)
    {
        var local = entry.Position - cell.Center;
        var x = (NVector3.Dot(local, cell.Right) + CellSize * 0.5f) / TexelSize - 0.5f;
        var y = (NVector3.Dot(local, cell.Up) + CellSize * 0.5f) / TexelSize - 0.5f;
        return (x, y, entry.Radius / TexelSize);
    }

    private static int GrainSeed(Cell cell) => cell.Key.X * 73856093 ^ cell.Key.Y * 19349663 ^ cell.Key.Z * 83492791 ^ cell.Bucket * 1299709;

    /// <summary>Fixed per-texel speckle so the paint reads as sprayed rather than flat.</summary>
    private static float SprayGrain(int seed, int i, int j)
    {
        var h = (uint)(seed ^ i * 2654435761 ^ j * 40503);
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
        var created = CreateDecal($"RoutePaint {cell.Key}", BaseMaterial(), _parent);
        if (created == null)
        {
            return false;
        }

        (cell.GameObject, cell.Decal, cell.Material, cell.Texture) = created.Value;
        cell.Decal.enabled = _fill;
        cell.Pixels = NewPixels();
        cell.Modes = new byte[Resolution * Resolution];
        cell.Coverage = new float[Resolution * Resolution];

        // A child, so it shares the paint's box, and registers (so draws) right after it.
        var glow = CreateDecal($"RoutePaint {cell.Key} glow", GlowBaseMaterial(), cell.GameObject.transform);
        if (glow != null)
        {
            (cell.Glow, cell.GlowDecal, cell.GlowMaterial, cell.GlowTexture) = glow.Value;
            cell.Glow.SetActive(_glow);
        }

        cell.Shown = ShouldShow(cell);
        if (cell.Shown)
        {
            UpdateOpacity(cell);
            cell.GameObject.SetActive(true);
            Registrations++;
        }

        return true;
    }

    /// <summary>
    /// A TGBDecal with its own material and texture, left inactive: the decal registers itself
    /// with the game's decal system in OnEnable, so callers activate it once it's positioned.
    /// </summary>
    private (GameObject, TGBDecal, Material, Texture2D)? CreateDecal(string name, Material baseMaterial, Transform parent, int resolution = Resolution)
    {
        if (baseMaterial == null)
        {
            if (!_warned)
            {
                _warned = true;
                MelonLogger.Error("TGB/Decal shader not found; painted areas can't be shown.");
            }

            return null;
        }

        var texture = RuntimeAssets.Keep(new Texture2D(resolution, resolution, TextureFormat.RGBA32, false)
        {
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear,
            name = name,
        });
        var material = RuntimeAssets.Keep(new Material(baseMaterial) { name = name, mainTexture = texture });

        var go = new GameObject(name);
        go.SetActive(false);
        go.transform.SetParent(parent, false);
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
        return (go, decal, material, texture);
    }

    /// <summary>
    /// A decal apart from the paint (a start or finish marker's pad), lit and glowing like the paint's edges,
    /// with a blank square texture of its own. Left inactive, like <see cref="CreateDecal"/>.
    /// </summary>
    public (GameObject GameObject, TGBDecal Decal, Material Material, Texture2D Texture)? CreateGlowDecal(string name, Transform parent, int resolution) =>
        CreateDecal(name, GlowBaseMaterial(), parent, resolution);

    /// <summary>The paint material, drawing its texture as emission too.</summary>
    private Material GlowBaseMaterial()
    {
        if (_glowBaseMaterial == null && BaseMaterial() is { } baseMaterial)
        {
            _glowBaseMaterial = RuntimeAssets.Keep(new Material(baseMaterial) { name = "RoutePaintGlowBase" });
            _glowBaseMaterial.SetFloat("_MainTexAsEmission", GlowEmission);
        }

        return _glowBaseMaterial;
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
        RuntimeAssets.Keep(_baseMaterial);
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

        // The glow's GameObject goes with its parent.
        if (cell.GlowMaterial != null)
        {
            Object.Destroy(cell.GlowMaterial);
        }

        if (cell.GlowTexture != null)
        {
            Object.Destroy(cell.GlowTexture);
        }
    }
}
