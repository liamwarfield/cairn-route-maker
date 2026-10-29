using System;
using System.Runtime.InteropServices;
using UnityEngine;
using NVector3 = System.Numerics.Vector3;
using NVector4 = System.Numerics.Vector4;

namespace CairnRouteMaker.Editor;

/// <summary>
/// A pattern painted on the rock around a start or finish marker: a decal like the glowing edges of
/// the route paint (see <see cref="DecalPaint"/>), so it's lit like the rock and follows its bumps.
/// One box projects the pattern along the rock's normal at the marker.
/// </summary>
internal sealed class MarkerPad
{
    /// <summary>The paint glow's emission (see DecalPaint.GlowEmission); the breathing only dims it.</summary>
    private const float Emission = 0.5f;

    private static readonly int EmissionId = Shader.PropertyToID("_MainTexAsEmission");
    private static readonly int NormalsFadeThresholdId = Shader.PropertyToID("_NormalsFadeThreshold");

    private readonly DecalPaint _paint;
    private readonly Transform _parent;
    private readonly string _name;
    private readonly int _resolution;
    private GameObject _object;
    private Material _material;
    private Texture2D _texture;
    private byte[] _pixels;
    private float _size;
    private bool _failed;
    private bool _placed;
    private bool _visible = true;

    public MarkerPad(DecalPaint paint, Transform parent, string name, int resolution)
    {
        _paint = paint;
        _parent = parent;
        _name = name;
        _resolution = resolution;
    }

    /// <summary>
    /// Draw the pattern over a square <paramref name="size"/> metres across. <paramref name="texel"/>
    /// gives the colour (straight alpha, 0 to 1) at a spot in metres from the middle: x across, y
    /// forward (see <see cref="Place"/>).
    /// </summary>
    public void Draw(float size, Func<float, float, NVector4> texel)
    {
        if (!Create())
        {
            return;
        }

        _size = size;
        var step = size / _resolution;
        var p = 0;
        for (var j = 0; j < _resolution; j++)
        {
            var y = (j + 0.5f) * step - size * 0.5f;
            for (var i = 0; i < _resolution; i++)
            {
                var c = texel((i + 0.5f) * step - size * 0.5f, y);
                _pixels[p++] = Byte(c.X);
                _pixels[p++] = Byte(c.Y);
                _pixels[p++] = Byte(c.Z);
                _pixels[p++] = Byte(c.W);
            }
        }

        _texture.LoadRawTextureData(Marshal.UnsafeAddrOfPinnedArrayElement(_pixels, 0), _pixels.Length);
        _texture.Apply(false);
    }

    /// <summary>
    /// Lay the pattern on the rock at <paramref name="point"/>, facing out along
    /// <paramref name="normal"/>, with its forward (y) along <paramref name="forward"/> as far as the
    /// rock's slope allows. Rock up to half of <paramref name="depth"/> in front of or behind that
    /// plane still gets the pattern, as long as it faces out along the normal at least
    /// <paramref name="minFacing"/> (a dot product).
    /// </summary>
    public void Place(NVector3 point, NVector3 normal, NVector3 forward, float depth, float minFacing)
    {
        if (_object == null)
        {
            return;
        }

        // The decal shader projects along the box's local Y, which has to point into the rock, and
        // takes the texture's u and v from local X and Z.
        var along = forward - normal * NVector3.Dot(forward, normal);
        if (along.LengthSquared() < 1e-4f)
        {
            along = NVector3.Cross(normal, MathF.Abs(normal.X) < 0.9f ? NVector3.UnitX : NVector3.UnitZ);
        }

        var transform = _object.transform;
        transform.SetPositionAndRotation(RouteOverlay.ToUnity(point), Quaternion.LookRotation(RouteOverlay.ToUnity(NVector3.Normalize(along)), RouteOverlay.ToUnity(-normal)));
        transform.localScale = new Vector3(_size, depth, _size);
        _material.SetFloat(NormalsFadeThresholdId, minFacing);

        // The decal registers with the game's decal system when it's enabled, so only once it's in place.
        _placed = true;
        _object.SetActive(_visible);
    }

    public bool Visible
    {
        set
        {
            _visible = value;
            if (_object != null && _placed)
            {
                _object.SetActive(value);
            }
        }
    }

    /// <summary>Dim the glow, 1 being full; for the markers' breathing.</summary>
    public void SetBrightness(float brightness)
    {
        if (_material != null && _object.activeInHierarchy)
        {
            _material.SetFloat(EmissionId, Emission * brightness);
        }
    }

    /// <summary>The decal is made on first use, once the game's decal material can be found.</summary>
    private bool Create()
    {
        if (_object != null || _failed)
        {
            return _object != null;
        }

        var created = _paint.CreateGlowDecal(_name, _parent, _resolution);
        if (created == null)
        {
            // DecalPaint has already said why.
            _failed = true;
            return false;
        }

        (_object, _, _material, _texture) = created.Value;

        // Pinned, so the texture copies straight out of it (see DecalPaint.NewPixels).
        _pixels = GC.AllocateArray<byte>(_resolution * _resolution * 4, pinned: true);
        return true;
    }

    /// <summary>How much of a texel a shape covers, from the signed distance to its edge (negative inside).</summary>
    public static float Coverage(float edgeDistance, float texelSize) => Math.Clamp(0.5f - edgeDistance / texelSize, 0f, 1f);

    public static float SegmentDistance(float x, float y, (float X, float Y) a, (float X, float Y) b)
    {
        float abX = b.X - a.X, abY = b.Y - a.Y;
        var t = Math.Clamp(((x - a.X) * abX + (y - a.Y) * abY) / (abX * abX + abY * abY), 0f, 1f);
        float dx = x - a.X - abX * t, dy = y - a.Y - abY * t;
        return MathF.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>A crisp line (<paramref name="line"/> coverage) over a soft wash, as straight-alpha colour.</summary>
    public static NVector4 Over(NVector3 lineColor, float line, NVector3 washColor, float wash)
    {
        var under = wash * (1f - line);
        var alpha = line + under;
        var color = alpha > 0f ? (lineColor * line + washColor * under) / alpha : washColor;
        return new NVector4(color, alpha);
    }

    private static byte Byte(float value) => (byte)(Math.Clamp(value, 0f, 1f) * 255f + 0.5f);
}
