using System;
using System.Collections.Generic;
using CairnRouteMaker.Routes;
using UnityEngine;
using NVector3 = System.Numerics.Vector3;
using NVector4 = System.Numerics.Vector4;

namespace CairnRouteMaker.Editor;

/// <summary>
/// Where a route ends. The finish sphere is a glassy bubble that's clear in the middle and lit at its
/// rim, with a chequered ring painted on the rock where it meets it. A little cairn turns slowly
/// inside, and a beam of light rises from the top so the finish can be seen from the climb below.
///
/// The bubble's rim light depends on the view, so it's worked out per vertex each frame (a fresnel
/// term), as is the shading on the cairn's stones.
/// </summary>
internal sealed class FinishMarker
{
    private static readonly NVector3 Color = new(0.3f, 0.65f, 1f);
    private static readonly NVector3 White = new(0.95f, 0.97f, 1f);
    private static readonly NVector3 StoneColor = new(0.86f, 0.92f, 1f);

    private const int Longitudes = 40;
    private const int Latitudes = 20;

    /// <summary>How tightly the light gathers at the rim: higher keeps the middle clearer.</summary>
    private const float RimPower = 3f;
    private const float BubbleTint = 0.02f;
    private const float BubbleGlow = 0.03f;
    private const float RimTint = 0.3f;
    private const float RimGlow = 0.45f;

    /// <summary>From inside, the bubble fades to this much, so it doesn't fog the view.</summary>
    private const float InsideStrength = 0.2f;

    private const int PadResolution = 512;

    /// <summary>The chequered ring is two rows of squares this size, straddling the sphere's radius.</summary>
    private const float CheckSize = 0.1f;
    private const float PadMargin = 0.3f;
    private const float HaloWidth = 0.12f;
    private const float HaloAlpha = 0.3f;
    private const float FillAlpha = 0.05f;

    private const float BeaconHeight = 30f;
    private const float BeaconHalfWidth = 0.25f;
    private const float BeaconTint = 0.08f;
    private const float BeaconGlow = 0.5f;
    private const float NearFadeStart = 1.5f;
    private const float NearFadeEnd = 4f;

    /// <summary>
    /// The cairn's stones from the bottom up, for a 1 m finish: the centre (x, height above the
    /// cairn's foot), the ellipsoid's half-sizes (a across, b high, c deep) and a turn about the vertical.
    /// </summary>
    private static readonly (float X, float Y, float A, float B, float C, float YawDegrees)[] Stones =
    {
        (0f, 0.075f, 0.2f, 0.075f, 0.16f, 10f),
        (0.012f, 0.202f, 0.15f, 0.062f, 0.12f, 55f),
        (-0.01f, 0.304f, 0.105f, 0.05f, 0.085f, 105f),
        (0.004f, 0.382f, 0.065f, 0.038f, 0.055f, 150f),
    };

    private const float CairnWidth = 0.2f;
    private const float CairnHeight = 0.42f;

    /// <summary>The cairn grows with the finish up to this many times its 1 m size.</summary>
    private const float MaxCairnScale = 2f;

    /// <summary>How far the cairn floats clear of the rock, for a 1 m finish.</summary>
    private const float CairnClearance = 0.1f;
    private const float BobHeight = 0.03f;
    private const float BobSeconds = 4f;
    private const float SpinDegreesPerSecond = 15f;
    private const int StoneLongitudes = 14;
    private const int StoneLatitudes = 8;
    private static readonly NVector3 Light = NVector3.Normalize(new NVector3(0.3f, 1f, 0.2f));

    private readonly MarkerPad _pad;
    private readonly GlowMesh _mesh;
    private readonly MarkerRock _rock = new("Finish");

    /// <summary>The bubble's vertices on a unit sphere (so also its normals), row by row from the top.</summary>
    private readonly NVector3[] _sphere;

    /// <summary>The cairn at a 1 m finish, relative to its foot, before it turns.</summary>
    private readonly NVector3[] _stonePoints;
    private readonly NVector3[] _stoneNormals;
    private readonly int _beamFirst;
    private FinishZone _finish;
    private float _padRadius = -1f;
    private int _checks;

    public FinishMarker(Transform parent, DecalPaint paint)
    {
        _pad = new MarkerPad(paint, parent, "RouteFinish pad", PadResolution);
        _mesh = new GlowMesh(parent, "RouteFinish");
        var triangles = new List<int>();

        _sphere = new NVector3[(Longitudes + 1) * (Latitudes + 1)];
        for (int row = 0, v = 0; row <= Latitudes; row++)
        {
            var polar = MathF.PI * row / Latitudes;
            for (var column = 0; column <= Longitudes; column++)
            {
                var around = 2f * MathF.PI * column / Longitudes;
                _sphere[v++] = new NVector3(MathF.Sin(polar) * MathF.Cos(around), MathF.Cos(polar), MathF.Sin(polar) * MathF.Sin(around));
            }
        }

        GlowMesh.AddGrid(triangles, 0, Longitudes + 1, Latitudes + 1);

        var perStone = (StoneLongitudes + 1) * (StoneLatitudes + 1);
        _stonePoints = new NVector3[Stones.Length * perStone];
        _stoneNormals = new NVector3[_stonePoints.Length];
        for (var s = 0; s < Stones.Length; s++)
        {
            BuildStone(s, s * perStone);
            GlowMesh.AddGrid(triangles, _sphere.Length + s * perStone, StoneLongitudes + 1, StoneLatitudes + 1);
        }

        _beamFirst = _sphere.Length + _stonePoints.Length;
        GlowMesh.AddBeam(triangles, _beamFirst);
        _mesh.SetTriangles(_beamFirst + GlowMesh.BeamVertices, triangles);
        _mesh.Visible = false;
    }

    public void Set(FinishZone finish)
    {
        _finish = finish;
        _mesh.Visible = finish != null;
        _pad.Visible = finish != null;
        if (finish == null)
        {
            return;
        }

        if (finish.Radius != _padRadius)
        {
            _padRadius = finish.Radius;
            _checks = Math.Max(8, 2 * (int)MathF.Round(MathF.PI * finish.Radius / CheckSize));
            _pad.Draw(2f * (finish.Radius + PadMargin), PadTexel);
        }

        _rock.Reset(finish.Position, finish.Radius);
        _rock.Update(Time.unscaledTime);
        Layout();
    }

    /// <summary>Shade the bubble and cairn for the camera, turn the cairn and breathe; call every frame, after the camera has moved.</summary>
    public void Update(Camera camera, float time)
    {
        if (_finish == null || camera == null || !_mesh.Shown)
        {
            return;
        }

        if (_rock.Update(time))
        {
            Layout();
        }

        var radius = _finish.Radius;
        var eye = RouteOverlay.ToNumerics(camera.transform.position) - _finish.Position;
        var breath = GlowMesh.Breath(time, 0.25f);
        var outside = GlowMesh.Smoothstep(0.9f * radius, 1.4f * radius, eye.Length());
        var bubble = breath * (InsideStrength + (1f - InsideStrength) * outside);
        _mesh.BeginFrame();

        for (var v = 0; v < _sphere.Length; v++)
        {
            var normal = _sphere[v];
            var point = normal * radius;
            var rim = Rim(normal, eye - point);
            _mesh.Set(v, point, GlowMesh.Paint(Color, (BubbleTint + RimTint * rim) * bubble, (BubbleGlow + RimGlow * rim) * bubble));
        }

        // The cairn floats clear of the rock whatever its slope: far enough out along the normal
        // that neither its side nor (under a roof) its top touches the plane through the point.
        var scale = MathF.Min(radius, MaxCairnScale);
        var up = _rock.Normal;
        var clearance = CairnWidth * MathF.Sqrt(MathF.Max(1f - up.Y * up.Y, 0f)) + MathF.Max(-CairnHeight * up.Y, 0f) + CairnClearance;
        var foot = up * (clearance * scale) + NVector3.UnitY * (BobHeight * scale * MathF.Sin(time * (2f * MathF.PI / BobSeconds)));
        var spin = time * SpinDegreesPerSecond * MathF.PI / 180f;
        float cos = MathF.Cos(spin), sin = MathF.Sin(spin);
        for (var i = 0; i < _stonePoints.Length; i++)
        {
            var point = foot + Turn(_stonePoints[i], cos, sin) * scale;
            var normal = Turn(_stoneNormals[i], cos, sin);
            var rim = Rim(normal, eye - point);
            var lit = 0.55f + 0.45f * MathF.Max(NVector3.Dot(normal, Light), 0f);
            _mesh.Set(_sphere.Length + i, point, GlowMesh.Paint(StoneColor * lit, (0.5f + 0.3f * rim) * breath, (0.1f + 0.4f * rim) * breath));
        }

        // The beam rises from the top of the bubble's open side: the very top on the ground, but out
        // from the rock on a wall, where the top is on the rock face and the beam would run up inside it.
        var rise = NVector3.UnitY + up;
        var beaconFoot = (rise.LengthSquared() > 0.25f ? NVector3.Normalize(rise) : NVector3.UnitY) * radius;
        var toAxis = eye - beaconFoot;
        toAxis.Y = 0f;
        var beacon = breath * GlowMesh.Smoothstep(NearFadeStart, NearFadeEnd, toAxis.Length());
        _mesh.SetBeam(_beamFirst, beaconFoot, BeaconHeight, BeaconHalfWidth, eye, Color, BeaconTint * beacon, BeaconGlow * beacon);
        _mesh.EndFrame();
        _pad.SetBrightness(GlowMesh.Breath(time, 0.3f));
    }

    /// <summary>Draw the painted ring over the route paint; see MarkerPad.DrawOnTop.</summary>
    public void DrawOnTop() => _pad.DrawOnTop();

    private void Layout()
    {
        // As for the start's ring (see StartMarker), wall rock gets a deeper, more forgiving pad.
        var wall = _rock.Climbing;
        _pad.Place(_finish.Position, _rock.Normal, NVector3.UnitZ, MathF.Max(wall ? 1.6f : 1f, 0.6f * _finish.Radius), wall ? 0.25f : 0.5f);
        _mesh.Position = RouteOverlay.ToUnity(_finish.Position);
    }

    /// <summary>The fresnel term: 0 where the surface faces the eye, 1 where it's seen edge-on.</summary>
    private static float Rim(NVector3 normal, NVector3 toEye)
    {
        var length = toEye.Length();
        var facing = length > 1e-5f ? MathF.Abs(NVector3.Dot(normal, toEye)) / length : 1f;
        return MathF.Pow(1f - facing, RimPower);
    }

    private static NVector3 Turn(NVector3 v, float cos, float sin) => new(v.X * cos + v.Z * sin, v.Y, v.Z * cos - v.X * sin);

    /// <summary>A flattened, slightly lumpy ellipsoid, so the stack looks hand-built rather than turned.</summary>
    private void BuildStone(int stone, int first)
    {
        var (x, y, a, b, c, yawDegrees) = Stones[stone];
        var yaw = yawDegrees * MathF.PI / 180f;
        float cos = MathF.Cos(yaw), sin = MathF.Sin(yaw);
        var seed = 1.7f * stone + 0.4f;
        var v = first;
        for (var row = 0; row <= StoneLatitudes; row++)
        {
            var polar = MathF.PI * row / StoneLatitudes;
            for (var column = 0; column <= StoneLongitudes; column++)
            {
                var around = 2f * MathF.PI * column / StoneLongitudes;
                var unit = new NVector3(MathF.Sin(polar) * MathF.Cos(around), MathF.Cos(polar), MathF.Sin(polar) * MathF.Sin(around));

                // Lumps fade out toward the poles, where every column meets at one point.
                var lumps = 1f + MathF.Sin(polar) * (0.07f * MathF.Sin(3f * around + seed) + 0.04f * MathF.Cos(2f * around - 2f * seed));
                var point = new NVector3(unit.X * a, unit.Y * b, unit.Z * c) * lumps;
                var normal = NVector3.Normalize(new NVector3(unit.X / a, unit.Y / b, unit.Z / c));
                _stonePoints[v] = Turn(point, cos, sin) + new NVector3(x, y, 0f);
                _stoneNormals[v] = Turn(normal, cos, sin);
                v++;
            }
        }
    }

    private NVector4 PadTexel(float x, float y)
    {
        var radius = _padRadius;
        var r = MathF.Sqrt(x * x + y * y);
        var band = MathF.Abs(r - radius) - CheckSize;
        var line = MarkerPad.Coverage(band, 2f * (radius + PadMargin) / PadResolution);
        var halo = HaloAlpha * (1f - GlowMesh.Smoothstep(0f, HaloWidth, band));
        var wash = MathF.Max(halo, r < radius ? FillAlpha : 0f);

        var column = (int)MathF.Floor((MathF.Atan2(y, x) / (2f * MathF.PI) + 0.5f) * _checks);
        var row = r < radius ? 0 : 1;
        var check = ((column + row) & 1) == 0 ? White : Color;
        return MarkerPad.Over(check, line, Color, wash);
    }
}
